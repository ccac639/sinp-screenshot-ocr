using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sinp.Hotkey
{
    /// <summary>
    /// 全局热键管理器（WPF 兼容，无 WinForms 依赖）
    ///
    /// 架构：
    ///   RegisterHotKey（主方案，需窗口句柄）
    ///   ↓ 兜底
    ///   LowLevelKeyboardHook（全局钩子，无窗口也能用）
    ///
    /// 使用方式：
    ///   var mgr = new GlobalHotkeyManager(hwnd);
    ///   mgr.Register("Ctrl+Shift+S", () => { ... });
    ///   mgr.Register("ESC",        () => { ... });
    ///   // 在 WPF WndProc 里调用 mgr.HandleWmHotkey(msg, wParam)
    ///   mgr.Dispose();
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        // ── Win32 常量 ─────────────────────────────────────
        private const int WM_HOTKEY = 0x0312;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        // Modifiers（RegisterHotKey 用）
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CTRL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        // ── P/Invoke：RegisterHotKey ───────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ── P/Invoke：LowLevelKeyboardHook ─────────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // ── 回调委托（必须保持引用，否则被 GC 回收）────────
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc = null!;
        private IntPtr _keyboardHook = IntPtr.Zero;

        // ── 状态 ───────────────────────────────────────────
        private readonly IntPtr _hwnd;
        private readonly Dictionary<int, Action> _registeredCallbacks = new();
        private int _nextHotkeyId = 1;
        private bool _disposed;

        public event Action<string>? OnHotkeyTriggered;

        // ── 已注册的热键表达式 → callback（LL 钩子用）────
        private readonly Dictionary<string, Action> _llCallbacks = new();

        // ── 组合键状态追踪（LL 钩子用）──────────────────
        private readonly HashSet<int> _keysDown = new();

        // ── 公共：修饰键当前是否按下 ─────────────────────
        private bool IsCtrlDown  => GetAsyncKeyState(0x11) < 0;  // VK_CONTROL
        private bool IsShiftDown => GetAsyncKeyState(0x10) < 0;  // VK_SHIFT
        private bool IsAltDown   => GetAsyncKeyState(0x12) < 0;  // VK_MENU
        private bool IsWinDown   => GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;

        // ── 外部回调（可选，用于回到 UI 线程）──────────────
        public Action<Action>? InvokeOnUiThread { get; set; }

        // =====================================================
        //  构造 / 析构
        // =====================================================
        public GlobalHotkeyManager(IntPtr windowHandle)
        {
            _hwnd = windowHandle;
        }

        // =====================================================
        //  注册热键（优先 RegisterHotKey，失败则走 LL 钩子）
        // =====================================================
        public bool Register(string hotkeyExpr, Action callback)
        {
            // 1) 先尝试 RegisterHotKey（需要有效 _hwnd）
            if (_hwnd != IntPtr.Zero)
            {
                var (mod, vk) = ParseHotkey(hotkeyExpr);
                if (vk != 0)
                {
                    var id = _nextHotkeyId++;
                    // MOD_NOREPEAT：防止长按重复触发
                    if (RegisterHotKey(_hwnd, id, mod | MOD_NOREPEAT, vk))
                    {
                        _registeredCallbacks[id] = callback;
                        return true;
                    }
                    // RegisterHotKey 失败 → 降级到 LL 钩子
                }
            }

            // 2) 降级：LowLevelKeyboardHook
            // （不依赖窗口句柄，全局生效）
            if (!TryEnsureLLHook())
                return false;

            _llCallbacks[hotkeyExpr.ToUpper()] = callback;
            return true;
        }

        // =====================================================
        //  WPF WndProc 里调用（处理 RegisterHotKey 的消息）
        // =====================================================
        public bool HandleWmHotkey(int msg, IntPtr wParam)
        {
            if (msg != WM_HOTKEY)
                return false;

            var id = wParam.ToInt32();
            if (_registeredCallbacks.TryGetValue(id, out var cb))
            {
                OnHotkeyTriggered?.Invoke($"Hotkey id={id}");
                cb?.Invoke();
                return true;
            }
            return false;
        }

        // =====================================================
        //  LowLevelKeyboardHook（兜底方案）
        // =====================================================
        private bool TryEnsureLLHook()
        {
            if (_keyboardHook != IntPtr.Zero)
                return true;   // 已安装

            _keyboardProc = new LowLevelKeyboardProc(KeyboardHookCallback);
            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);

            if (_keyboardHook == IntPtr.Zero)
            {
                _keyboardProc = null!;
                return false;
            }
            return true;
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 → 直接传递
            if (nCode < 0)
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            // 只处理 KEYDOWN
            if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam);

                // 更新按键状态
                _keysDown.Add(vk);

                // 检查所有已注册的 LL 热键
                foreach (var kv in _llCallbacks)
                {
                    var expr = kv.Key;
                    if (IsComboMatch(expr, vk))
                    {
                        // 匹配！触发回调
                        OnHotkeyTriggered?.Invoke($"LL Hook: {expr}");
                        // 用 InvokeOnUiThread 回到 UI 线程（如果设置过）
                        if (InvokeOnUiThread != null)
                            InvokeOnUiThread(kv.Value);
                        else
                            kv.Value();
                        break;
                    }
                }
            }
            else
            {
                // 键释放 → 清除状态
                int vk = Marshal.ReadInt32(lParam);
                _keysDown.Remove(vk);
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 判断当前按下的键是否匹配热键表达式
        /// </summary>
        private bool IsComboMatch(string expr, int justPressedVk)
        {
            var parts = expr.Split('+');
            // 检查修饰键
            bool needCtrl  = parts.Contains("CTRL");
            bool needShift = parts.Contains("SHIFT");
            bool needAlt   = parts.Contains("ALT");
            bool needWin   = parts.Contains("WIN");

            if (needCtrl  && !IsCtrlDown)  return false;
            if (needShift && !IsShiftDown) return false;
            if (needAlt   && !IsAltDown)   return false;
            if (needWin   && !IsWinDown)   return false;

            // 检查主键（刚才按下的键）
            string mainKey = parts.Last();
            int mainVk = GetVkFromKeyName(mainKey);
            return justPressedVk == mainVk;
        }

        private static int GetVkFromKeyName(string name)
        {
            return name switch
            {
                "S" => 0x53,
                "C" => 0x43,
                "V" => 0x56,
                "A" => 0x41,
                "P" => 0x50,
                "ESC" => 0x1B,
                "SPACE" => 0x20,
                "RETURN" or "ENTER" => 0x0D,
                "TAB" => 0x09,
                _ when Enum.TryParse<Keys>(name, true, out var k) => (int)k,
                _ => 0
            };
        }

        // =====================================================
        //  解析热键表达式 → (modifiers, vk)
        // =====================================================
        private static (uint modifiers, uint vk) ParseHotkey(string expr)
        {
            uint mod = 0;
            uint vk = 0;

            var parts = expr.ToUpper().Split('+');
            foreach (var part in parts)
            {
                if (part == "CTRL")  mod |= MOD_CTRL;
                else if (part == "SHIFT") mod |= MOD_SHIFT;
                else if (part == "ALT")   mod |= MOD_ALT;
                else if (part == "WIN")   mod |= MOD_WIN;
                else
                    vk = part switch
                    {
                        "S" => 0x53,
                        "C" => 0x43,
                        "V" => 0x56,
                        "A" => 0x41,
                        "P" => 0x50,
                        "ESC" => 0x1B,
                        "SPACE" => 0x20,
                        "ENTER" or "RETURN" => 0x0D,
                        _ => (uint)Enum.Parse<Keys>(part, true)
                    };
            }
            return (mod, vk);
        }

        // =====================================================
        //  Dispose
        // =====================================================
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 注销 RegisterHotKey
            foreach (var id in _registeredCallbacks.Keys)
                UnregisterHotKey(_hwnd, id);
            _registeredCallbacks.Clear();

            // 卸载 LL 钩子
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            _keyboardProc = null!;
        }
    }
}
