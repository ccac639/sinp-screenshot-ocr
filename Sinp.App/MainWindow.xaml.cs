using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Sinp.CaptureCore;
using Sinp.Overlay;
using Sinp.OCRClient;
using Sinp.Hotkey;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using System.Windows.Forms;  // for NotifyIcon

namespace Sinp.App
{
    public partial class MainWindow : Window
    {
        private GlobalHotkeyManager? _hotkeyMgr;
        private Sinp.OCRClient.OCRClient _ocrClient = new();
        private HwndSource? _hwndSource;
        private Overlay.RegionSelector? _activeSelector;
        private NotifyIcon? _notifyIcon;
        private bool _isExiting = false;

        public MainWindow()
        {
            InitializeComponent();

            // 重置窗口位置（避免在屏幕外）
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Left = 100;
            this.Top = 100;
            this.Width = 420;
            this.Height = 560;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeyMgr = new GlobalHotkeyManager(hwnd);
            _hotkeyMgr.OnHotkeyTriggered += msg => Logger.Info(msg);

            // ESC 用全局热键，确保在截图模式下一定能取消
            // ENTER 不注册全局热键 —— RegionSelector 内部已处理 KeyDown(Enter)
            // 全局 LL 钩子会拦截系统所有按键，导致 ENTER 在其他窗口失灵
            bool ok1 = _hotkeyMgr.Register("Ctrl+Shift+S", OnCaptureHotkey);
            bool ok2 = _hotkeyMgr.Register("ESC", OnCancelHotkey);
            bool ok3 = false; // ENTER 不注册全局热键

            Logger.Info(string.Format("Hotkey: Ctrl+Shift+S={0}, ESC={1}, ENTER={2}",
                ok1 ? "OK" : "FAIL", ok2 ? "OK" : "FAIL", ok3 ? "OK" : "FAIL"));

            _hwndSource = HwndSource.FromHwnd(hwnd);
            if (_hwndSource != null)
                _hwndSource.AddHook(WndProcHook);

            // 确保窗口显示在最前面
            this.Topmost = true;
            this.Show();
            this.Activate();
            this.Focus();

            // 延迟取消 Topmost（1 秒后）
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, ev) =>
            {
                this.Topmost = false;
                timer.Stop();
            };
            timer.Start();

            // 初始化系统托盘图标（暂时禁用以排查启动问题）
            // InitTrayIcon();

            // 处理窗口关闭事件（最小化到托盘而不是直接退出）
            // this.Closing += MainWindow_Closing;

            StatusText.Text = "就绪 | Ctrl+Shift+S 截图 | ESC 取消";
            Logger.Info("主窗口加载完成，热键系统就绪");
        }

        private void InitTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            // 使用应用程序图标（如果没有，使用系统默认图标）
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sinp.ico");
                if (System.IO.File.Exists(iconPath))
                    _notifyIcon.Icon = new Icon(iconPath);
                else
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            _notifyIcon.Text = "Sinp 截图OCR";
            _notifyIcon.Visible = true;

            // 双击托盘图标恢复窗口
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            // 右键菜单
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("打开", null, (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            });
            contextMenu.Items.Add("-"); // 分隔符
            contextMenu.Items.Add("退出", null, (s, e) =>
            {
                _isExiting = true;
                Application.Current.Shutdown();
            });
            _notifyIcon.ContextMenuStrip = contextMenu;

            Logger.Info("系统托盘图标初始化完成");
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true; // 取消关闭
                this.Hide(); // 隐藏窗口（最小化到托盘）
                _notifyIcon?.ShowBalloonTip(2000, "Sinp", "程序已最小化到系统托盘", ToolTipIcon.Info);
                Logger.Info("窗口已最小化到系统托盘");
            }
            else
            {
                // 真正退出，清理托盘图标
                _notifyIcon?.Dispose();
                _notifyIcon = null;
            }
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                if (_hotkeyMgr != null && _hotkeyMgr.HandleWmHotkey(msg, wParam))
                    handled = true;
            }
            return IntPtr.Zero;
        }

        // ── 热键回调 ─────────────────────────────────────────
        private void OnCaptureHotkey()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.Info("热键触发：截图");
                BtnCapture_Click(this, new RoutedEventArgs());
            }));
        }

        private void OnCancelHotkey()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.Info("热键触发：取消");
                if (_activeSelector != null && !_activeSelector.IsDisposed)
                {
                    _activeSelector.Close();
                    _activeSelector = null;
                }
            }));
        }

        private void ConfirmSelection()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.Info("热键触发：确认选区");
                if (_activeSelector != null && !_activeSelector.IsDisposed)
                {
                    // 程序化触发确认（会裁剪图像并设置 CapturedImage）
                    _activeSelector.TriggerConfirm();
                    // TriggerConfirm 已关闭窗口，现在读取结果
                    _activeSelector = null;
                }
            }));
        }

        // ── 标题栏 ─────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            this.WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) =>
            this.Close();

        // ── 截图按钮 ─────────────────────────────────────────
        private async void BtnCapture_Click(object sender, RoutedEventArgs e)
        {
            await DoCapture(false);
        }

        private async void BtnLongCapture_Click(object sender, RoutedEventArgs e)
        {
            await DoCapture(true);
        }

        // ── 核心流程：隐藏主窗口 → 选区 → 截图 → 剪贴板 → OCR ──
        private async Task DoCapture(bool isLong)
        {
            this.Visibility = Visibility.Collapsed;
            await Task.Delay(350);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            try
            {
                if (isLong)
                    await DoLongCapture();
                else
                    await DoRegionCapture();
            }
            finally
            {
                this.Visibility = Visibility.Visible;
            }
        }

        private async Task DoRegionCapture()
        {
            StatusText.Text = "请在屏幕上拖动选择区域...";
            Logger.Info("进入区域选择模式");

            var tcs = new TaskCompletionSource<Rectangle?>();

            var selector = new Overlay.RegionSelector();
            _activeSelector = selector;
            selector.OnRegionSelected += rect =>
            {
                Logger.Info(string.Format("选区确定: {0}", rect));
                tcs.TrySetResult(rect);
            };
            selector.OnCancelled += () =>
            {
                Logger.Info("选区取消");
                tcs.TrySetResult(null);
            };

            selector.ShowDialog();

            var region = await tcs.Task;
            if (region == null || !region.HasValue)
            {
                StatusText.Text = "已取消";
                selector.Dispose();
                return;
            }

            // 关键：直接用 RegionSelector 裁剪好的图片，不再二次截屏
            // （二次截屏会截到 Overlay 窗口，导致黑色）
            var capturedImage = selector.CapturedImage;
            selector.Dispose();

            if (capturedImage == null)
            {
                StatusText.Text = "截图失败: 未获取到图像";
                Logger.Error("截图失败: CapturedImage 为 null");
                return;
            }

            // 复制到剪贴板（必须在 UI 线程）
            Dispatcher.Invoke(() =>
            {
                CopyImageToClipboard(capturedImage);
            });

            StatusText.Text = "截图已复制到剪贴板";
            Logger.Info("截图已复制到剪贴板");

            // OCR 识别
            await RunOCR(capturedImage);
        }

        private async Task ProcessRegionCapture(Rectangle region)
        {
            // 热键 Enter 确认时，selector 已经在 ConfirmSelection 里裁剪好图片
            if (_activeSelector == null) return;
            var capturedImage = _activeSelector.CapturedImage;
            _activeSelector.Dispose();
            _activeSelector = null;

            if (capturedImage == null)
            {
                StatusText.Text = "截图失败: 未获取到图像";
                Logger.Error("截图失败: CapturedImage 为 null");
                return;
            }

            Dispatcher.Invoke(() =>
            {
                CopyImageToClipboard(capturedImage);
            });

            StatusText.Text = "截图已复制到剪贴板";
            Logger.Info("截图已复制到剪贴板");

            await RunOCR(capturedImage);
        }

        private async Task DoLongCapture()
        {
            StatusText.Text = "长截图模式：请在屏幕上拖动选择要捕获的区域...";
            Logger.Info("进入长截图模式");

            var tcs = new TaskCompletionSource<bool>();

            var selector = new Overlay.RegionSelector();
            _activeSelector = selector;

            // 状态提示转发
            selector.OnStatusMessage += msg =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = msg;
                }));
            };

            selector.OnRegionSelected += rect =>
            {
                Logger.Info(string.Format("长截图选区确定: {0}, 帧数: {1}", rect, selector.LongFrames.Count));
                tcs.TrySetResult(true);
            };
            selector.OnCancelled += () =>
            {
                Logger.Info("长截图取消");
                tcs.TrySetResult(false);
            };

            selector.ShowDialog();

            var success = await tcs.Task;
            _activeSelector = null;

            if (!success)
            {
                StatusText.Text = "长截图已取消";
                selector.Dispose();
                return;
            }

            // 长截图：判断结果来源
            Bitmap? resultImage = selector.CapturedImage;
            bool needStitch = selector.IsLongScreenshot && selector.LongFrames.Count > 1;

            // 如果 CapturedImage 为 null 但有多个帧 → 需要拼接
            if (resultImage == null && needStitch)
            {
                // CapturedImage=null 表示 RegionSelector 把帧留给 MainWindow 拼接
            }
            else if (resultImage == null && !needStitch)
            {
                // 没有任何图像数据
                StatusText.Text = "长截图失败：未捕获到图像";
                selector.Dispose();
                return;
            }

            // 拼接长截图帧
            if (needStitch)
            {
                StatusText.Text = "正在拼接长截图...";
                Logger.Info(string.Format("开始拼接 {0} 帧", selector.LongFrames.Count));
                try
                {
                    using var stitcher = new Sinp.Stitch.StitchEngine();
                    foreach (var frame in selector.LongFrames)
                        stitcher.AddFrame(frame);

                    var stitchResult = stitcher.Stitch();
                    if (stitchResult.Success && stitchResult.Image != null)
                    {
                        resultImage = stitchResult.Image;
                        Logger.Info(string.Format("拼接完成：{0} 帧 → {1}x{2}",
                            stitchResult.FrameCount, resultImage.Width, resultImage.Height));
                    }
                    else
                    {
                        Logger.Error(string.Format("拼接失败: {0}", stitchResult.ErrorMessage));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("长截图拼接异常", ex);
                }
            }

            selector.Dispose();

            if (resultImage == null)
            {
                StatusText.Text = "长截图失败";
                return;
            }

            // 复制到剪贴板
            Dispatcher.Invoke(() =>
            {
                CopyImageToClipboard(resultImage);
            });

            StatusText.Text = string.Format("长截图已复制（{0} 帧拼接）", selector.LongFrames.Count);
            Logger.Info("长截图已复制到剪贴板");

            // 对拼接结果做 OCR
            await RunOCR(resultImage);
        }

        // ── 复制截图到剪贴板（WPF STA 线程）─────────────────
        private void CopyImageToClipboard(Bitmap bmp)
        {
            try
            {
                // Bitmap → PNG bytes → WPF BitmapSource → Clipboard
                // 关键：BitmapCacheOption.OnLoad 确保流关闭后图像仍可用
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad
                );
                var bitmapSource = decoder.Frames[0];
                bitmapSource.Freeze(); // 关键：Freeze 后可跨线程使用

                Clipboard.SetImage(bitmapSource);
                Logger.Info("截图已复制到剪贴板 (WPF BitmapSource)");
            }
            catch (Exception ex)
            {
                Logger.Error("复制截图到剪贴板失败", ex);
            }
        }

        // ── OCR 识别 ─────────────────────────────────────────
        private async Task RunOCR(Bitmap image)
        {
            StatusText.Text = "正在 OCR 识别...";
            Logger.Info("开始 OCR 识别");

            try
            {
                var imageBytes = Sinp.OCRClient.OCRClient.BitmapToBytes(image);
                var resp = await _ocrClient.RecognizeAsync(imageBytes);

                if (resp.Success)
                {
                    var sb = new StringBuilder();
                    foreach (var line in resp.Lines)
                        sb.AppendLine(line.Text);

                    ResultText.Text = sb.ToString();
                    ResultText.Foreground = System.Windows.Media.Brushes.White;
                    StatusText.Text = string.Format("识别完成，共 {0} 行", resp.Lines.Length);
                    Logger.Info(string.Format("OCR 完成: {0} 行", resp.Lines.Length));
                }
                else
                {
                    StatusText.Text = string.Format("OCR 失败: {0}", resp.ErrorMessage);
                    Logger.Error(string.Format("OCR 失败: {0}", resp.ErrorMessage));
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format("OCR 错误: {0}", ex.Message);
                Logger.Error("OCR 异常", ex);
            }
        }

        // ── 按钮事件 ─────────────────────────────────────────
        private void BtnOCR_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "OCR 按钮（开发中）";
        }

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            StatusText.Text = this.Topmost ? "窗口已固定" : "窗口固定已取消";
        }

        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(ResultText.Text) && !ResultText.Text.StartsWith("（"))
            {
                Clipboard.SetText(ResultText.Text);
                StatusText.Text = "已复制到剪贴板";
                Logger.Info("复制 OCR 结果到剪贴板");
            }
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ResultText.Text) || ResultText.Text.StartsWith("（"))
                return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = System.IO.Path.Combine(baseDir, "..", "..", "data");
            System.IO.Directory.CreateDirectory(dataDir);

            var path = System.IO.Path.Combine(dataDir, string.Format("ocr_{0}.txt", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            await System.IO.File.WriteAllTextAsync(path, ResultText.Text);
            StatusText.Text = string.Format("已导出: {0}", System.IO.Path.GetFileName(path));
            Logger.Info(string.Format("导出 OCR 结果: {0}", path));
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotkeyMgr?.Dispose();
            _hwndSource?.RemoveHook(WndProcHook);
            _hwndSource = null;
            base.OnClosed(e);
        }
    }
}
