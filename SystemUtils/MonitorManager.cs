using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace Sinp.SystemUtils
{
    /// <summary>
    /// 多显示器管理器
    /// </summary>
    public static class MonitorManager
    {
        /// <summary>
        /// 获取所有显示器信息
        /// </summary>
        public static List<MonitorInfo> GetAllMonitors()
        {
            var monitors = new List<MonitorInfo>();
            foreach (Screen screen in Screen.AllScreens)
            {
                monitors.Add(new MonitorInfo
                {
                    DeviceName = screen.DeviceName,
                    IsPrimary = screen.Primary,
                    Bounds = screen.Bounds,
                    WorkingArea = screen.WorkingArea,
                    ScaleFactor = GetScaleFactor(screen)
                });
            }
            return monitors;
        }

        /// <summary>
        /// 获取主显示器
        /// </summary>
        public static MonitorInfo GetPrimaryMonitor()
        {
            var primary = Screen.PrimaryScreen;
            return new MonitorInfo
            {
                DeviceName = primary.DeviceName,
                IsPrimary = true,
                Bounds = primary.Bounds,
                WorkingArea = primary.WorkingArea,
                ScaleFactor = GetScaleFactor(primary)
            };
        }

        private static double GetScaleFactor(Screen screen)
        {
            // 通过 SystemParameters 获取 DPI 缩放
            // 在 WPF 中，Actual DPI 需要从 Visual 获取
            // 这里返回默认值 1.0（100%）
            return 1.0;
        }
    }

    public class MonitorInfo
    {
        public string DeviceName { get; set; } = "";
        public bool IsPrimary { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle WorkingArea { get; set; }
        public double ScaleFactor { get; set; }
    }

    /// <summary>
    /// DPI 管理器（WPF 感知 Per-Monitor DPI）
    /// </summary>
    public static class DpiManager
    {
        public static DpiScale GetDpiScale(Visual visual)
        {
            return VisualTreeHelper.GetDpi(visual);
        }

        public static double GetScalingFactor(Visual visual)
        {
            var dpi = GetDpiScale(visual);
            return dpi.DpiScaleX;  // 假设 X/Y 相同
        }
    }

    /// <summary>
    /// 剪贴板辅助
    /// </summary>
    public static class ClipboardHelper
    {
        public static void CopyText(string text)
        {
            if (!string.IsNullOrEmpty(text))
                System.Windows.Clipboard.SetText(text);
        }

        public static void CopyImage(System.Drawing.Bitmap bitmap)
        {
            if (bitmap != null)
                System.Windows.Clipboard.SetImage(ToWpfBitmap(bitmap));
        }

        private static System.Windows.Media.Imaging.BitmapSource ToWpfBitmap(System.Drawing.Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad
            );
            return decoder.Frames[0];
        }
    }
}
