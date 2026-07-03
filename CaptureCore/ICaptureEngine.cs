using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;   // ← 改用 WinForms

namespace Sinp.CaptureCore
{
    /// <summary>
    /// 截图结果
    /// </summary>
    public record CaptureResult(
        bool Success,
        Bitmap? Image,
        string? ErrorMessage = null
    );

    /// <summary>
    /// 截图引擎接口（策略模式）
    /// </summary>
    public interface ICaptureEngine : IDisposable
    {
        string Name { get; }
        bool IsAvailable { get; }
        CaptureResult Capture(Rectangle? region = null);
        event Action<Bitmap>? OnFrameCaptured;
    }

    /// <summary>
    /// DXGI Desktop Duplication 截图引擎（推荐，GPU 级性能）
    /// 使用 SharpDX / DirectNX 或直接 P/Invoke
    /// 此处提供接口 + GDI 兜底实现，DXGI 实现需安装 DirectX 包
    /// </summary>
    public class DxgiCaptureEngine : ICaptureEngine
    {
        public string Name => "DXGI Desktop Duplication";
        public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public event Action<Bitmap>? OnFrameCaptured;

        public CaptureResult Capture(Rectangle? region = null)
        {
            // DXGI Desktop Duplication 实现
            // 需要引用 SharpDX.Direct3D11 / Windows.Graphics.Capture
            // 这里先返回 NotSupported，由 GdiCaptureEngine 兜底
            return new CaptureResult(false, null, "DXGI 实现需要从 NuGet 安装 Microsoft.Windows.Capture");
        }

        public void Dispose() { }
    }

    /// <summary>
    /// GDI 兜底截图引擎（兼容性好，性能一般）
    /// </summary>
    public class GdiCaptureEngine : ICaptureEngine
    {
        public string Name => "GDI Fallback Capture";
        public bool IsAvailable => true;

        public event Action<Bitmap>? OnFrameCaptured;

        public CaptureResult Capture(Rectangle? region = null)
        {
            try
            {
                // WinForms：SystemInformation.VirtualScreen = 全虚拟屏幕（所有显示器）
                var bounds = region ?? SystemInformation.VirtualScreen;

                var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                OnFrameCaptured?.Invoke(bitmap);
                return new CaptureResult(true, bitmap);
            }
            catch (Exception ex)
            {
                return new CaptureResult(false, null, ex.Message);
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 截图引擎工厂（自动选择最优引擎）
    /// </summary>
    public static class CaptureEngineFactory
    {
        public static ICaptureEngine CreateBest()
        {
            // 优先 DXGI，失败则 GDI 兜底
            var dxgi = new DxgiCaptureEngine();
            if (dxgi.IsAvailable)
            {
                var test = dxgi.Capture();
                if (test.Success)
                    return dxgi;
            }

            return new GdiCaptureEngine();
        }
    }
}
