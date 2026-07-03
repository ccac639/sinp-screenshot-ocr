using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;

namespace Sinp.Overlay
{
    /// <summary>
    /// 截图选区窗口 — Snipaste 级体验
    ///
    /// 核心架构（冻结背景层）：
    ///   1. 进入时先截全屏 → 冻结背景
    ///   2. 在冻结背景上画选区 + 半透明遮罩
    ///   3. 选区固定后显示 ActionBar
    ///   4. Enter/双击 → 确认，ESC/右键 → 取消
    ///   5. 长截图按钮 → 自动滚动捕获（Snipaste 模式）
    ///
    /// 长截图自动滚动流程：
    ///   点击 "📜 自动滚屏" → 隐藏 Overlay → 定位目标窗口
    ///   → 发送 PageDown 滚动 → 截取新帧 → 对比检测是否到底
    ///   → 到底后自动拼接 → 关闭并返回结果
    /// </summary>
    public class RegionSelector : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // ── Win32 API（自动滚动用）────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // WM_VSCROLL 参数
        private const uint WM_VSCROLL = 0x0115;
        private static readonly UIntPtr SB_PAGEDOWN = new UIntPtr(3);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        // 冻结背景（全屏截图）
        private Bitmap? _frozenBackground;

        // 最终裁剪结果
        public Bitmap? CapturedImage { get; private set; }

        // 选区
        private enum DragMode { None, Create, Move, ResizeTop, ResizeBottom, ResizeLeft, ResizeRight,
                               ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }
        private DragMode _dragMode = DragMode.None;
        private Point _startPoint;
        private Rectangle _rect;
        private Rectangle _rectBefore;

        // ActionBar
        private readonly string[] _toolLabels = { "✓ 确认", "✗ 取消", "🔍 OCR", "💾 保存", "📋 复制", "📜 自动滚屏" };
        private int _hoverToolIndex = -1;
        private const int TOOL_BTN_HEIGHT = 36;
        private const int TOOL_BTN_WIDTH = 78;

        // 长截图状态
        private bool _longScreenshotActive = false;       // 是否正在自动滚动
        private readonly List<Bitmap> _longFrames = new();
        public IReadOnlyList<Bitmap> LongFrames => _longFrames.AsReadOnly();
        public bool IsLongScreenshot { get; private set; } = false;

        // 自动滚动定时器
        private System.Windows.Forms.Timer? _scrollTimer;

        // 结果事件
        public Rectangle? SelectedRegion { get; private set; }
        public event Action<Rectangle>? OnRegionSelected;
        public event Action? OnCancelled;
        public event Action<string>? OnStatusMessage;

        public void TriggerConfirm()
        {
            if (this.InvokeRequired) this.Invoke(new Action(ConfirmSelection));
            else ConfirmSelection();
        }

        // =====================================================
        //  构造 / 初始化
        // =====================================================
        public RegionSelector() { InitializeComponent(); }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; return cp; }
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Normal;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.KeyPreview = true;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = SystemInformation.VirtualScreen;

            CaptureFrozenBackground();

            this.KeyDown += (s, e) =>
            {
                if (_longScreenshotActive) return; // 滚动中忽略键盘
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                    ConfirmSelection();
                else if (e.KeyCode == Keys.Escape)
                    Cancel();
            };
        }

        private void CaptureFrozenBackground()
        {
            var bounds = SystemInformation.VirtualScreen;
            _frozenBackground = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_frozenBackground))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            this.BackgroundImage = _frozenBackground;
            this.BackgroundImageLayout = ImageLayout.None;
        }

        // =====================================================
        //  鼠标事件
        // =====================================================
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_longScreenshotActive) return;

            if (e.Button == MouseButtons.Left)
            {
                if (HitTestToolButton(e.Location) >= 0)
                    ExecuteToolCommand(HitTestToolButton(e.Location));

                _startPoint = e.Location;
                _dragMode = GetDragModeAt(e.Location);
                if (_dragMode == DragMode.None)
                    { _rect = new Rectangle(e.Location, Size.Empty); _dragMode = DragMode.Create; }
                else _rectBefore = _rect;
                this.Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                Cancel();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_longScreenshotActive) return;

            if (e.Button == MouseButtons.Left && _dragMode != DragMode.None)
            {
                switch (_dragMode)
                {
                    case DragMode.Create: _rect = NormalizeRect(_startPoint, e.Location); break;
                    case DragMode.Move:
                        _rect = new Rectangle(
                            _rectBefore.X + e.Location.X - _startPoint.X,
                            _rectBefore.Y + e.Location.Y - _startPoint.Y,
                            _rectBefore.Width, _rectBefore.Height); break;
                    case DragMode.ResizeRight: _rect.Width = Math.Max(10, e.Location.X - _rect.X); break;
                    case DragMode.ResizeBottom: _rect.Height = Math.Max(10, e.Location.Y - _rect.Y); break;
                    case DragMode.ResizeLeft: int or2 = _rect.Right; _rect.X = Math.Min(e.Location.X, or2 - 10); _rect.Width = or2 - _rect.X; break;
                    case DragMode.ResizeTop: int ob2 = _rect.Bottom; _rect.Y = Math.Min(e.Location.Y, ob2 - 10); _rect.Height = ob2 - _rect.Y; break;
                    case DragMode.ResizeTopLeft: int tlr = _rect.Right, tlb = _rect.Bottom; _rect.X = Math.Min(e.Location.X, tlr-10); _rect.Y = Math.Min(e.Location.Y, tlb-10); _rect.Width = tlr-_rect.X; _rect.Height = tlb-_rect.Y; break;
                    case DragMode.ResizeTopRight: _rect.Y = Math.Min(e.Location.Y, _rect.Bottom-10); _rect.Height = _rect.Bottom-_rect.Y; _rect.Width = Math.Max(10, e.Location.X-_rect.X); break;
                    case DragMode.ResizeBottomLeft: _rect.X = Math.Min(e.Location.X, _rect.Right-10); _rect.Width = _rect.Right-_rect.X; _rect.Height = Math.Max(10, e.Location.Y-_rect.Y); break;
                    case DragMode.ResizeBottomRight: _rect.Width = Math.Max(10, e.Location.X-_rect.X); _rect.Height = Math.Max(10, e.Location.Y-_rect.Y); break;
                }
                this.Invalidate();
            }
            else
            {
                int oldHover = _hoverToolIndex; _hoverToolIndex = HitTestToolButton(e.Location); UpdateCursor(e.Location);
                if (oldHover != _hoverToolIndex) this.Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragMode = DragMode.None;
                if (_rect.Width > 5 && _rect.Height > 5) SelectedRegion = _rect;
                this.Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _rect.Contains(e.Location)) ConfirmSelection();
            base.OnMouseDoubleClick(e);
        }

        // =====================================================
        //  确认 / 取消
        // =====================================================
        private void ConfirmSelection()
        {
            if (_rect.Width > 5 && _rect.Height > 5 && _frozenBackground != null)
            {
                CapturedImage = new Bitmap(_rect.Width, _rect.Height);
                using (var g = Graphics.FromImage(CapturedImage))
                    g.DrawImage(_frozenBackground, new Rectangle(0, 0, _rect.Width, _rect.Height), _rect, GraphicsUnit.Pixel);
                SelectedRegion = _rect;
                IsLongScreenshot = false;
                OnRegionSelected?.Invoke(_rect);
                this.Close();
            } else Cancel();
        }

        private void Cancel()
        {
            StopAutoScroll();
            OnCancelled?.Invoke();
            this.Close();
        }

        // =====================================================
        //  ★★★ 自动滚动长截图（核心功能）★★★
        //
        //  流程：
        //    ① 隐藏 Overlay 窗口（不遮挡屏幕）
        //    ② 找到选区下方的目标窗口
        //    ③ 截取第一帧
        //    ④ 发送 PageDown 到目标窗口
        //    ⑤ 等待渲染 → 截取下一帧
        //    ⑥ 对比两帧是否相同（相同=到底了）
        //    ⑦ 重复 ④~⑥ 直到到底
        //    ⑧ 自动拼接所有帧 → 返回结果
        // =====================================================

        /// <summary>
        /// 启动自动滚动长截图
        /// </summary>
        private async void StartAutoScrollCapture()
        {
            if (_rect.Width <= 5 || _rect.Height <= 5) return;

            _longScreenshotActive = true;
            _longFrames.Clear();

            // 更新 UI 提示
            _toolLabels[5] = "⏳ 滚动中...";
            _toolLabels[0] = "⏹ 停止";
            this.Invalidate();
            OnStatusMessage?.Invoke("正在自动滚动截图...");

            try
            {
                // ① 隐藏自己，让用户看到实际内容
                this.Hide();
                await Task.Delay(200); // 等 Overlay 完全消失

                // ② 找到选区中心点下的窗口句柄
                var centerPt = new POINT { X = _rect.X + _rect.Width / 2, Y = _rect.Y + _rect.Height / 2 };
                var targetHwnd = WindowFromPoint(centerPt);

                // ③ 截取第一帧（直接从屏幕截）
                var firstFrame = CaptureScreenRegion(_rect);
                if (firstFrame != null)
                    _longFrames.Add(firstFrame);

                OnStatusMessage?.Invoke($"已捕获第 1 帧，开始自动滚动...");

                // ④~⑦ 循环：滚动 → 截图 → 检测是否到底
                int maxFrames = 50; // 安全上限
                int noChangeCount = 0; // 连续无变化次数

                for (int frameIdx = 1; frameIdx < maxFrames; frameIdx++)
                {
                    // 发送 PageDown 滚动
                    if (targetHwnd != IntPtr.Zero)
                        SendMessage(targetHwnd, WM_VSCROLL, SB_PAGEDOWN, IntPtr.Zero);
                    else
                        // 兜底：用 SendKeys
                        SendKeys.SendWait("{PGDN}");

                    // 等待页面渲染（可调）
                    await Task.Delay(350);

                    // 截取当前屏幕的同一区域
                    var nextFrame = CaptureScreenRegion(_rect);
                    if (nextFrame == null) break;

                    // ⑤ 检测是否到底（和上一帧对比）
                    if (IsSimilarToLast(nextFrame))
                    {
                        noChangeCount++;
                        nextFrame.Dispose(); // 相同帧不要
                        if (noChangeCount >= 2) break; // 连续2次无变化 → 到底了
                        continue;
                    }
                    noChangeCount = 0;
                    _longFrames.Add(nextFrame);

                    // 更新进度
                    string progressMsg = $"已捕获 {_longFrames.Count} 帧...";
                    OnStatusMessage?.Invoke(progressMsg);
                }

                // ⑧ 完成！拼接或返回最后一帧
                OnStatusMessage?.Invoke($"滚动完成！共捕获 {_longFrames.Count} 帧");
                IsLongScreenshot = true;

                if (_longFrames.Count >= 2)
                {
                    // 多帧 → CapturedImage 设为 null，让 MainWindow 用 LongFrames 做拼接
                    CapturedImage = null;
                }
                else if (_longFrames.Count == 1)
                {
                    // 只有1帧 → 直接当普通截图
                    CapturedImage = _longFrames[0];
                }
                else
                {
                    // 0帧 → 用冻结背景裁剪
                    CapturedImage = new Bitmap(_rect.Width, _rect.Height);
                    using (var g = Graphics.FromImage(CapturedImage))
                        g.DrawImage(_frozenBackground!, new Rectangle(0,0,_rect.Width,_rect.Height), _rect, GraphicsUnit.Pixel);
                }

                SelectedRegion = _rect;
                OnRegionSelected?.Invoke(_rect);
            }
            catch (Exception ex)
            {
                OnStatusMessage?.Invoke($"长截图出错：{ex.Message}");
                IsLongScreenshot = false;
            }
            finally
            {
                _longScreenshotActive = false;
                this.Close();
            }
        }

        /// <summary>
        /// 直接从屏幕截取指定区域（不经过冻结背景）
        /// </summary>
        private static Bitmap? CaptureScreenRegion(Rectangle region)
        {
            try
            {
                var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(region.X, region.Y, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>
        /// 检测新帧是否与上一帧相似（判断是否到底）
        /// </summary>
        private bool IsSimilarToLast(Bitmap newBmp)
        {
            if (_longFrames.Count == 0) return false;
            var lastBmp = _longFrames[_longFrames.Count - 1];
            if (newBmp.Size != lastBmp.Size) return false;

            // 抽样比较像素（每 20px 取一个点，性能好够用了）
            int step = 20, sameCount = 0, total = 0;
            for (int y = 0; y < newBmp.Height; y += step)
            {
                for (int x = 0; x < newBmp.Width; x += step)
                {
                    total++;
                    if (newBmp.GetPixel(x, y).ToArgb() == lastBmp.GetPixel(x, y).ToArgb())
                        sameCount++;
                }
            }
            // 95% 以上像素相同 → 认为没变化（到底了）
            return total > 0 && sameCount * 100 / total > 95;
        }

        /// <summary>
        /// 停止自动滚动
        /// </summary>
        private void StopAutoScroll()
        {
            _longScreenshotActive = false;
            _scrollTimer?.Stop();
            _scrollTimer?.Dispose();
            _scrollTimer = null;
        }

        // =====================================================
        //  ActionBar
        // =====================================================
        private Rectangle GetActionBarBounds()
        {
            if (_rect.Width <= 0 || _rect.Height <= 0) return Rectangle.Empty;
            int tw = _toolLabels.Length * TOOL_BTN_WIDTH + 8;
            int x = _rect.X + (_rect.Width - tw) / 2;
            int y = _rect.Bottom + 8;
            if (y + TOOL_BTN_HEIGHT > SystemInformation.VirtualScreen.Bottom)
                y = _rect.Y - TOOL_BTN_HEIGHT - 8;
            return new Rectangle(x, y, tw, TOOL_BTN_HEIGHT);
        }

        private Rectangle GetToolButtonRect(int index)
        {
            var bar = GetActionBarBounds();
            return new Rectangle(bar.X + 4 + index * TOOL_BTN_WIDTH, bar.Y + 2, TOOL_BTN_WIDTH - 4, TOOL_BTN_HEIGHT - 4);
        }

        private int HitTestToolButton(Point p)
        {
            if (_rect.Width <= 0 || _rect.Height <= 0) return -1;
            for (int i = 0; i < _toolLabels.Length; i++)
                if (GetToolButtonRect(i).Contains(p)) return i;
            return -1;
        }

        private void ExecuteToolCommand(int index)
        {
            switch (index)
            {
                case 0: // 确认 / 停止
                    if (_longScreenshotActive)
                        StopAutoScroll();
                    else
                        ConfirmSelection();
                    break;
                case 1: Cancel(); break;
                case 5: StartAutoScrollCapture(); break; // 📜 自动滚屏
                default: ConfirmSelection(); break; // OCR/保存/复制
            }
        }

        // =====================================================
        //  绘制
        // =====================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_rect.Width > 0 && _rect.Height > 0)
            {
                // 1. 全屏遮罩
                var sr = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                using var mb = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillRectangle(mb, sr);

                // 2. 选区内画回冻结背景
                g.DrawImage(_frozenBackground, _rect, _rect, GraphicsUnit.Pixel);

                // 3. 边框
                using var op = new Pen(Color.FromArgb(255, 30, 30, 30), 3);
                using var ip = new Pen(Color.FromArgb(255, 255, 80, 80), 2);
                g.DrawRectangle(op, _rect.X-1, _rect.Y-1, _rect.Width+2, _rect.Height+2);
                g.DrawRectangle(ip, _rect);

                // 4. 尺寸文字
                var dt = $"{_rect.Width} x {_rect.Height}";
                using var f = new Font("Segoe UI", 11, FontStyle.Bold);
                var ts = g.MeasureString(dt, f);
                var tr = new Rectangle(_rect.X, _rect.Y-(int)ts.Height-4, (int)ts.Width+8, (int)ts.Height+4);
                using var tb = new SolidBrush(Color.FromArgb(220, 255, 80, 80));
                g.FillRectangle(tb, tr);
                using var txb = new SolidBrush(Color.White);
                g.DrawString(dt, f, txb, _rect.X+4, _rect.Y-(int)ts.Height-2);

                // 5. 手柄 + ActionBar
                if (_dragMode != DragMode.Create)
                { DrawHandles(g); DrawActionBar(g); }
            }
            else
            {
                var sr = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                using var mb = new SolidBrush(Color.FromArgb(60, 0, 0, 0)); g.FillRectangle(mb, sr);
                using var tf = new Font("Segoe UI", 16);
                using var tp = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
                var tip = "拖动鼠标选择截图区域";
                var s = g.MeasureString(tip, tf);
                g.DrawString(tip, tf, tp, (ClientSize.Width-s.Width)/2, (ClientSize.Height-s.Height)/2);
            }
            base.OnPaint(e);
        }

        private void DrawHandles(Graphics g)
        {
            using var hb = new SolidBrush(Color.FromArgb(255, 255, 80, 80));
            using var hb2 = new Pen(Color.White, 1);
            foreach (var h in GetHandleRects()) { g.FillRectangle(hb, h); g.DrawRectangle(hb2, h); }
        }

        private Rectangle[] GetHandleRects()
        {
            int s = 8, h = s/2;
            return new[]
            {
                new Rectangle(_rect.Left-h, _rect.Top-h, s, s), new Rectangle(_rect.Right-h, _rect.Top-h, s, s),
                new Rectangle(_rect.Left-h, _rect.Bottom-h, s, s), new Rectangle(_rect.Right-h, _rect.Bottom-h, s, s),
                new Rectangle(_rect.Left+_rect.Width/2-h, _rect.Top-h, s, s), new Rectangle(_rect.Left+_rect.Width/2-h, _rect.Bottom-h, s, s),
                new Rectangle(_rect.Left-h, _rect.Top+_rect.Height/2-h, s, s), new Rectangle(_rect.Right-h, _rect.Top+_rect.Height/2-h, s, s),
            };
        }

        private void DrawActionBar(Graphics g)
        {
            var bar = GetActionBarBounds(); if (bar.IsEmpty) return;
            using var bgb = new SolidBrush(Color.FromArgb(250, 40, 40, 55));
            using var bgp = new Pen(Color.FromArgb(200, 80, 80, 100), 1);
            g.FillRectangle(bgb, bar); g.DrawRectangle(bgp, bar);
            using var hb = new SolidBrush(Color.FromArgb(255, 70, 130, 180));
            using var font = new Font("Segoe UI", 10);
            using var tb = new SolidBrush(Color.White);
            for (int i = 0; i < _toolLabels.Length; i++)
            {
                var btn = GetToolButtonRect(i);
                if (i == _hoverToolIndex) g.FillRectangle(hb, btn);
                var sz = g.MeasureString(_toolLabels[i], font);
                g.DrawString(_toolLabels[i], font, tb, btn.X+(btn.Width-sz.Width)/2, btn.Y+(btn.Height-sz.Height)/2);
            }
        }

        // ── 拖拽模式判断 ────────────────────────────────────
        private DragMode GetDragModeAt(Point p)
        {
            if (_rect.Width <= 0 || _rect.Height <= 0) return DragMode.None;
            if (HitTestToolButton(p) >= 0) return DragMode.None;
            var handles = GetHandleRects();
            if (handles[0].Contains(p)) return DragMode.ResizeTopLeft; if (handles[1].Contains(p)) return DragMode.ResizeTopRight;
            if (handles[2].Contains(p)) return DragMode.ResizeBottomLeft; if (handles[3].Contains(p)) return DragMode.ResizeBottomRight;
            if (handles[4].Contains(p)) return DragMode.ResizeTop; if (handles[5].Contains(p)) return DragMode.ResizeBottom;
            if (handles[6].Contains(p)) return DragMode.ResizeLeft; if (handles[7].Contains(p)) return DragMode.ResizeRight;
            if (Math.Abs(p.X-_rect.Left)<6 && p.Y>_rect.Top && p.Y<_rect.Bottom) return DragMode.ResizeLeft;
            if (Math.Abs(p.X-_rect.Right)<6 && p.Y>_rect.Top && p.Y<_rect.Bottom) return DragMode.ResizeRight;
            if (Math.Abs(p.Y-_rect.Top)<6 && p.X>_rect.Left && p.X<_rect.Right) return DragMode.ResizeTop;
            if (Math.Abs(p.Y-_rect.Bottom)<6 && p.X>_rect.Left && p.X<_rect.Right) return DragMode.ResizeBottom;
            if (_rect.Contains(p)) return DragMode.Move;
            return DragMode.None;
        }

        private void UpdateCursor(Point p)
        {
            this.Cursor = GetDragModeAt(p) switch
            {
                DragMode.Move => Cursors.SizeAll,
                DragMode.ResizeLeft or DragMode.ResizeRight => Cursors.SizeWE,
                DragMode.ResizeTop or DragMode.ResizeBottom => Cursors.SizeNS,
                DragMode.ResizeTopLeft or DragMode.ResizeBottomRight => Cursors.SizeNWSE,
                DragMode.ResizeTopRight or DragMode.ResizeBottomLeft => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
        }

        private static Rectangle NormalizeRect(Point a, Point b) =>
            new(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y));

        // =====================================================
        //  清理
        // =====================================================
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopAutoScroll();
                _frozenBackground?.Dispose(); _frozenBackground = null;
                foreach (var f in _longFrames) f.Dispose(); _longFrames.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
