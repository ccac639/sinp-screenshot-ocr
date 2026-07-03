using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sinp.Overlay
{
    /// <summary>
    /// 截图选区窗口 — Snipaste 级体验
    ///
    /// 核心架构（不是透明画图，是截图冻结层）：
    ///   1. 进入时先截全屏 → 冻结背景
    ///   2. 在冻结背景上画选区（选区透明，露出截图）
    ///   3. 选区固定后显示 ActionBar
    ///   4. Enter/双击/确认按钮 → 回调选区
    ///
    /// 交互：
    ///   鼠标拖动 → 创建选区
    ///   松开 → 选区固定 + ActionBar 出现
    ///   拖边框/角 → 调整大小
    ///   拖内部 → 移动
    ///   Enter → 确认
    ///   ESC/右键 → 取消
    /// </summary>
    public class RegionSelector : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // 冻结背景（全屏截图）
        private Bitmap? _frozenBackground;

        // 最终裁剪结果（从冻结背景裁剪，不是二次截屏）
        public Bitmap? CapturedImage { get; private set; }

        // 选区
        private enum DragMode { None, Create, Move, ResizeTop, ResizeBottom, ResizeLeft, ResizeRight, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }
        private DragMode _dragMode = DragMode.None;
        private Point _startPoint;
        private Rectangle _rect;
        private Rectangle _rectBefore;

        // ActionBar
        private readonly string[] _toolLabels = { "✓ 确认", "✗ 取消", "🔍 OCR", "💾 保存", "📋 复制" };
        private int _hoverToolIndex = -1;
        private const int TOOL_BTN_HEIGHT = 36;
        private const int TOOL_BTN_WIDTH = 72;

        // 结果
        public Rectangle? SelectedRegion { get; private set; }
        public event Action<Rectangle>? OnRegionSelected;
        public event Action? OnCancelled;

        /// <summary>
        /// 程序化触发确认（供外部调用，如 Enter 热键）
        /// </summary>
        public void TriggerConfirm()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(ConfirmSelection));
            }
            else
            {
                ConfirmSelection();
            }
        }

        public RegionSelector()
        {
            InitializeComponent();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
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

            // 关键：先截全屏作为冻结背景
            CaptureFrozenBackground();

            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                    ConfirmSelection();
                else if (e.KeyCode == Keys.Escape)
                    Cancel();
            };
        }

        /// <summary>
        /// 截取全屏作为冻结背景（关键步骤！）
        /// 这样选区看起来是"透明"的，实际是露出冻结的截图
        /// </summary>
        private void CaptureFrozenBackground()
        {
            var bounds = SystemInformation.VirtualScreen;
            _frozenBackground = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_frozenBackground))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            // 设置为窗口背景（这样窗口不透明，但看起来像桌面）
            this.BackgroundImage = _frozenBackground;
            this.BackgroundImageLayout = ImageLayout.None;
        }

        // ── 鼠标事件 ───────────────────────────────────────
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int toolIndex = HitTestToolButton(e.Location);
                if (toolIndex >= 0)
                {
                    ExecuteToolCommand(toolIndex);
                    return;
                }

                _startPoint = e.Location;
                _dragMode = GetDragModeAt(e.Location);

                if (_dragMode == DragMode.None)
                {
                    _rect = new Rectangle(e.Location, Size.Empty);
                    _dragMode = DragMode.Create;
                }
                else
                {
                    _rectBefore = _rect;
                }
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
            if (e.Button == MouseButtons.Left)
            {
                switch (_dragMode)
                {
                    case DragMode.Create:
                        _rect = NormalizeRect(_startPoint, e.Location);
                        break;
                    case DragMode.Move:
                        _rect = new Rectangle(
                            _rectBefore.X + e.Location.X - _startPoint.X,
                            _rectBefore.Y + e.Location.Y - _startPoint.Y,
                            _rectBefore.Width, _rectBefore.Height);
                        break;
                    case DragMode.ResizeRight:
                        _rect.Width = Math.Max(10, e.Location.X - _rect.X);
                        break;
                    case DragMode.ResizeBottom:
                        _rect.Height = Math.Max(10, e.Location.Y - _rect.Y);
                        break;
                    case DragMode.ResizeLeft:
                        int oldRight = _rect.Right;
                        _rect.X = Math.Min(e.Location.X, oldRight - 10);
                        _rect.Width = oldRight - _rect.X;
                        break;
                    case DragMode.ResizeTop:
                        int oldBottom = _rect.Bottom;
                        _rect.Y = Math.Min(e.Location.Y, oldBottom - 10);
                        _rect.Height = oldBottom - _rect.Y;
                        break;
                    case DragMode.ResizeTopLeft:
                        int tlRight = _rect.Right;
                        int tlBottom = _rect.Bottom;
                        _rect.X = Math.Min(e.Location.X, tlRight - 10);
                        _rect.Y = Math.Min(e.Location.Y, tlBottom - 10);
                        _rect.Width = tlRight - _rect.X;
                        _rect.Height = tlBottom - _rect.Y;
                        break;
                    case DragMode.ResizeTopRight:
                        _rect.Y = Math.Min(e.Location.Y, _rect.Bottom - 10);
                        _rect.Height = _rect.Bottom - _rect.Y;
                        _rect.Width = Math.Max(10, e.Location.X - _rect.X);
                        break;
                    case DragMode.ResizeBottomLeft:
                        _rect.X = Math.Min(e.Location.X, _rect.Right - 10);
                        _rect.Width = _rect.Right - _rect.X;
                        _rect.Height = Math.Max(10, e.Location.Y - _rect.Y);
                        break;
                    case DragMode.ResizeBottomRight:
                        _rect.Width = Math.Max(10, e.Location.X - _rect.X);
                        _rect.Height = Math.Max(10, e.Location.Y - _rect.Y);
                        break;
                }
                this.Invalidate();
            }
            else
            {
                int oldHover = _hoverToolIndex;
                _hoverToolIndex = HitTestToolButton(e.Location);
                UpdateCursor(e.Location);
                if (oldHover != _hoverToolIndex)
                    this.Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragMode = DragMode.None;

                // 关键：鼠标释放时，如果选区有效，设置 SelectedRegion
                // 这样 MainWindow.ConfirmSelection (Enter 热键) 才能读到有效选区
                if (_rect.Width > 5 && _rect.Height > 5)
                {
                    SelectedRegion = _rect;
                }

                this.Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _rect.Contains(e.Location))
                ConfirmSelection();
            base.OnMouseDoubleClick(e);
        }

        // ── 确认 / 取消 ────────────────────────────────────
        private void ConfirmSelection()
        {
            if (_rect.Width > 5 && _rect.Height > 5 && _frozenBackground != null)
            {
                // 关键：直接从冻结背景图裁剪选区，不再二次截屏
                // 这样不会截到 Overlay 窗口自己
                CapturedImage = new Bitmap(_rect.Width, _rect.Height);
                using (var g = Graphics.FromImage(CapturedImage))
                {
                    g.DrawImage(_frozenBackground,
                        new Rectangle(0, 0, _rect.Width, _rect.Height),
                        _rect, GraphicsUnit.Pixel);
                }
                SelectedRegion = _rect;
                OnRegionSelected?.Invoke(_rect);
                this.Close();
            }
            else
            {
                Cancel();
            }
        }

        private void Cancel()
        {
            OnCancelled?.Invoke();
            this.Close();
        }

        // ── ActionBar ──────────────────────────────────────
        private Rectangle GetActionBarBounds()
        {
            if (_rect.Width <= 0 || _rect.Height <= 0)
                return Rectangle.Empty;

            int totalWidth = _toolLabels.Length * TOOL_BTN_WIDTH + 8;
            int x = _rect.X + (_rect.Width - totalWidth) / 2;
            int y = _rect.Bottom + 8;

            int screenBottom = SystemInformation.VirtualScreen.Bottom;
            if (y + TOOL_BTN_HEIGHT > screenBottom)
                y = _rect.Y - TOOL_BTN_HEIGHT - 8;

            return new Rectangle(x, y, totalWidth, TOOL_BTN_HEIGHT);
        }

        private Rectangle GetToolButtonRect(int index)
        {
            var bar = GetActionBarBounds();
            return new Rectangle(bar.X + 4 + index * TOOL_BTN_WIDTH, bar.Y + 2, TOOL_BTN_WIDTH - 4, TOOL_BTN_HEIGHT - 4);
        }

        private int HitTestToolButton(Point p)
        {
            if (_rect.Width <= 0 || _rect.Height <= 0)
                return -1;
            for (int i = 0; i < _toolLabels.Length; i++)
            {
                if (GetToolButtonRect(i).Contains(p))
                    return i;
            }
            return -1;
        }

        private void ExecuteToolCommand(int index)
        {
            switch (index)
            {
                case 0: ConfirmSelection(); break;
                case 1: Cancel(); break;
                case 2:
                case 3:
                case 4:
                    // 复用 ConfirmSelection 的裁剪逻辑
                    ConfirmSelection();
                    break;
            }
        }

        // ── 绘制（核心：在冻结背景上画遮罩 + 选区）──────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_rect.Width > 0 && _rect.Height > 0)
            {
                // 1. 选区外画半透明黑色遮罩（盖住冻结背景）
                var screenRect = new Rectangle(0, 0, this.ClientSize.Width, this.ClientSize.Height);
                using var maskBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillRectangle(maskBrush, screenRect);

                // 2. 选区内清除遮罩（露出冻结背景 = 截图）
                g.SetClip(_rect);
                g.Clear(Color.Transparent);
                g.ResetClip();

                // 3. 选区边框
                using var outerPen = new Pen(Color.FromArgb(255, 30, 30, 30), 3);
                using var innerPen = new Pen(Color.FromArgb(255, 255, 80, 80), 2);
                g.DrawRectangle(outerPen, _rect.X - 1, _rect.Y - 1, _rect.Width + 2, _rect.Height + 2);
                g.DrawRectangle(innerPen, _rect);

                // 4. 尺寸文字
                var dimText = string.Format("{0} x {1}", _rect.Width, _rect.Height);
                using var font = new Font("Segoe UI", 11, FontStyle.Bold);
                var textSize = g.MeasureString(dimText, font);
                var textRect = new Rectangle(_rect.X, _rect.Y - (int)textSize.Height - 4, (int)textSize.Width + 8, (int)textSize.Height + 4);
                using var bgBrush = new SolidBrush(Color.FromArgb(220, 255, 80, 80));
                g.FillRectangle(bgBrush, textRect);
                using var textBrush = new SolidBrush(Color.White);
                g.DrawString(dimText, font, textBrush, _rect.X + 4, _rect.Y - (int)textSize.Height - 2);

                // 5. 手柄（选区固定后显示）
                if (_dragMode != DragMode.Create)
                    DrawHandles(g);

                // 6. ActionBar（选区固定后显示）
                if (_dragMode != DragMode.Create)
                    DrawActionBar(g);
            }
            else
            {
                // 全屏半透明遮罩
                var screenRect = new Rectangle(0, 0, this.ClientSize.Width, this.ClientSize.Height);
                using var maskBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
                g.FillRectangle(maskBrush, screenRect);

                // 提示文字
                using var tipFont = new Font("Segoe UI", 16);
                using var tipBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
                var tip = "拖动鼠标选择截图区域";
                var size = g.MeasureString(tip, tipFont);
                g.DrawString(tip, tipFont, tipBrush,
                    (this.ClientSize.Width - size.Width) / 2,
                    (this.ClientSize.Height - size.Height) / 2);
            }
            base.OnPaint(e);
        }

        private void DrawHandles(Graphics g)
        {
            using var handleBrush = new SolidBrush(Color.FromArgb(255, 255, 80, 80));
            using var handleBorder = new Pen(Color.White, 1);
            var handles = GetHandleRects();
            foreach (var h in handles)
            {
                g.FillRectangle(handleBrush, h);
                g.DrawRectangle(handleBorder, h);
            }
        }

        private Rectangle[] GetHandleRects()
        {
            int s = 8;
            int h = s / 2;
            return new[]
            {
                new Rectangle(_rect.Left - h, _rect.Top - h, s, s),
                new Rectangle(_rect.Right - h, _rect.Top - h, s, s),
                new Rectangle(_rect.Left - h, _rect.Bottom - h, s, s),
                new Rectangle(_rect.Right - h, _rect.Bottom - h, s, s),
                new Rectangle(_rect.Left + _rect.Width/2 - h, _rect.Top - h, s, s),
                new Rectangle(_rect.Left + _rect.Width/2 - h, _rect.Bottom - h, s, s),
                new Rectangle(_rect.Left - h, _rect.Top + _rect.Height/2 - h, s, s),
                new Rectangle(_rect.Right - h, _rect.Top + _rect.Height/2 - h, s, s),
            };
        }

        private void DrawActionBar(Graphics g)
        {
            var bar = GetActionBarBounds();
            if (bar.IsEmpty) return;

            // ActionBar 背景
            using var bgBrush = new SolidBrush(Color.FromArgb(250, 40, 40, 55));
            using var bgPen = new Pen(Color.FromArgb(200, 80, 80, 100), 1);
            g.FillRectangle(bgBrush, bar);
            g.DrawRectangle(bgPen, bar);

            using var hoverBrush = new SolidBrush(Color.FromArgb(255, 70, 130, 180));
            using var font = new Font("Segoe UI", 10);
            using var textBrush = new SolidBrush(Color.White);

            for (int i = 0; i < _toolLabels.Length; i++)
            {
                var btn = GetToolButtonRect(i);
                if (i == _hoverToolIndex)
                    g.FillRectangle(hoverBrush, btn);

                var size = g.MeasureString(_toolLabels[i], font);
                g.DrawString(_toolLabels[i], font, textBrush,
                    btn.X + (btn.Width - size.Width) / 2,
                    btn.Y + (btn.Height - size.Height) / 2);
            }
        }

        // ── 拖拽模式判断 ───────────────────────────────────
        private DragMode GetDragModeAt(Point p)
        {
            if (_rect.Width <= 0 || _rect.Height <= 0)
                return DragMode.None;

            if (HitTestToolButton(p) >= 0)
                return DragMode.None;

            var handles = GetHandleRects();
            if (handles[0].Contains(p)) return DragMode.ResizeTopLeft;
            if (handles[1].Contains(p)) return DragMode.ResizeTopRight;
            if (handles[2].Contains(p)) return DragMode.ResizeBottomLeft;
            if (handles[3].Contains(p)) return DragMode.ResizeBottomRight;
            if (handles[4].Contains(p)) return DragMode.ResizeTop;
            if (handles[5].Contains(p)) return DragMode.ResizeBottom;
            if (handles[6].Contains(p)) return DragMode.ResizeLeft;
            if (handles[7].Contains(p)) return DragMode.ResizeRight;

            if (Math.Abs(p.X - _rect.Left) < 6 && p.Y > _rect.Top && p.Y < _rect.Bottom) return DragMode.ResizeLeft;
            if (Math.Abs(p.X - _rect.Right) < 6 && p.Y > _rect.Top && p.Y < _rect.Bottom) return DragMode.ResizeRight;
            if (Math.Abs(p.Y - _rect.Top) < 6 && p.X > _rect.Left && p.X < _rect.Right) return DragMode.ResizeTop;
            if (Math.Abs(p.Y - _rect.Bottom) < 6 && p.X > _rect.Left && p.X < _rect.Right) return DragMode.ResizeBottom;

            if (_rect.Contains(p)) return DragMode.Move;

            return DragMode.None;
        }

        private void UpdateCursor(Point p)
        {
            var mode = GetDragModeAt(p);
            this.Cursor = mode switch
            {
                DragMode.Move => Cursors.SizeAll,
                DragMode.ResizeLeft or DragMode.ResizeRight => Cursors.SizeWE,
                DragMode.ResizeTop or DragMode.ResizeBottom => Cursors.SizeNS,
                DragMode.ResizeTopLeft or DragMode.ResizeBottomRight => Cursors.SizeNWSE,
                DragMode.ResizeTopRight or DragMode.ResizeBottomLeft => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
        }

        private static Rectangle NormalizeRect(Point a, Point b)
        {
            return new Rectangle(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y)
            );
        }

        // ── 清理 ───────────────────────────────────────────
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _frozenBackground?.Dispose();
                _frozenBackground = null;
            }
            base.Dispose(disposing);
        }
    }
}
