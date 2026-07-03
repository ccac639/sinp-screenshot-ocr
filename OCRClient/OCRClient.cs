using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sinp.OCRClient
{
    /// <summary>
    /// OCR 通信模式
    /// </summary>
    public enum OCRMode
    {
        NamedPipe,  // 生产模式（Windows）
        Http         // 调试模式（跨平台）
    }

    /// <summary>
    /// OCR 推理请求
    /// </summary>
    public record OCRRequest(
        string ImageBase64,
        string? Language = "ch",
        bool UseAngle = true
    );

    /// <summary>
    /// OCR 单行结果
    /// </summary>
    public record OCRTextLine(
        string Text,
        double Confidence,
        int[] BBox  // [x1, y1, x2, y2]
    );

    /// <summary>
    /// OCR 推理响应
    /// </summary>
    public record OCRResponse(
        bool Success,
        OCRTextLine[] Lines,
        string? ErrorMessage = null
    );

    /// <summary>
    /// OCR 客户端（支持 NamedPipe 和 HTTP 两种模式）
    /// </summary>
    public class OCRClient : IDisposable
    {
        private const string PIPE_NAME = "sinp_ocr_pipe";
        private const int HTTP_PORT = 8000;
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
        private readonly OCRMode _mode;
        private Process? _pythonWorkerProcess;
        private HttpClient? _httpClient;

        public OCRMode CurrentMode => _mode;
        public bool IsWorkerRunning => _pythonWorkerProcess != null && !_pythonWorkerProcess.HasExited;

        public OCRClient(OCRMode mode = OCRMode.Http)  // 默认使用 HTTP 模式（更易调试）
        {
            _mode = mode;
            if (_mode == OCRMode.Http)
            {
                _httpClient = new HttpClient();
                _httpClient.Timeout = _timeout;
            }
        }

        /// <summary>
        /// 启动 Python OCR Worker（如果未运行）
        /// </summary>
        public async Task<bool> EnsureWorkerRunningAsync()
        {
            if (_mode == OCRMode.NamedPipe)
            {
                // NamedPipe 模式：检查管道是否可连接
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.None);
                    await pipe.ConnectAsync(1000);
                    return true;
                }
                catch
                {
                    // 未运行，尝试启动
                    return await StartPythonWorkerAsync();
                }
            }
            else
            {
                // HTTP 模式：检查 HTTP 服务是否可访问
                try
                {
                    var resp = await _httpClient!.GetAsync($"http://127.0.0.1:{HTTP_PORT}/health");
                    return resp.IsSuccessStatusCode;
                }
                catch
                {
                    // 未运行，尝试启动
                    return await StartPythonWorkerAsync();
                }
            }
        }

        private async Task<bool> StartPythonWorkerAsync()
        {
            try
            {
                // 查找 Python Worker 目录
                var workerDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "ocr_worker");
                workerDir = Path.GetFullPath(workerDir);

                if (!Directory.Exists(workerDir))
                {
                    // 尝试绝对路径
                    workerDir = @"D:\sinp\ocr_worker";
                }

                if (!Directory.Exists(workerDir))
                {
                    Console.WriteLine($"[OCRClient] 找不到 ocr_worker 目录: {workerDir}");
                    return false;
                }

                var pythonExe = FindPythonExecutable();
                if (string.IsNullOrEmpty(pythonExe))
                {
                    Console.WriteLine("[OCRClient] 找不到 Python 可执行文件");
                    return false;
                }

                var modeArg = _mode == OCRMode.Http ? "http" : "pipe";
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"main.py --mode {modeArg}" + (_mode == OCRMode.Http ? $" --port {HTTP_PORT}" : ""),
                    WorkingDirectory = workerDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _pythonWorkerProcess = Process.Start(startInfo);
                Console.WriteLine($"[OCRClient] Python Worker 已启动 (PID: {_pythonWorkerProcess?.Id})");

                // 等待服务就绪（最多 10 秒）
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (_mode == OCRMode.Http)
                    {
                        try
                        {
                            var resp = await _httpClient!.GetAsync($"http://127.0.0.1:{HTTP_PORT}/health");
                            if (resp.IsSuccessStatusCode)
                            {
                                Console.WriteLine("[OCRClient] HTTP Worker 已就绪");
                                return true;
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            using var pipe = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.None);
                            await pipe.ConnectAsync(100);
                            Console.WriteLine("[OCRClient] NamedPipe Worker 已就绪");
                            return true;
                        }
                        catch { }
                    }
                }

                Console.WriteLine("[OCRClient] Worker 启动超时");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OCRClient] 启动 Worker 失败: {ex.Message}");
                return false;
            }
        }

        private string? FindPythonExecutable()
        {
            // 尝试常见 Python 可执行文件名
            var names = new[] { "python.exe", "python3.exe", "py.exe" };
            foreach (var name in names)
            {
                // 在 PATH 中查找
                var path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (var dir in path.Split(Path.PathSeparator))
                    {
                        var fullPath = Path.Combine(dir, name);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                }
            }

            // 尝试常见安装位置
            var commonPaths = new[]
            {
                @"C:\Python39\python.exe",
                @"C:\Python310\python.exe",
                @"C:\Python311\python.exe",
                @"C:\Python312\python.exe",
                @"C:\Users\2121\AppData\Local\Programs\Python\Python311\python.exe",
            };

            foreach (var p in commonPaths)
            {
                if (File.Exists(p))
                    return p;
            }

            return null;
        }

        /// <summary>
        /// 发送图片做 OCR 识别（异步）
        /// </summary>
        public async Task<OCRResponse> RecognizeAsync(byte[] imageBytes)
        {
            try
            {
                // 确保 Worker 正在运行
                if (!await EnsureWorkerRunningAsync())
                {
                    return new OCRResponse(false, Array.Empty<OCRTextLine>(),
                        "OCR Worker 未运行，且无法自动启动。请手动运行: python ocr_worker/main.py");
                }

                if (_mode == OCRMode.NamedPipe)
                {
                    return await RecognizeViaPipe(imageBytes);
                }
                else
                {
                    return await RecognizeViaHttp(imageBytes);
                }
            }
            catch (Exception ex)
            {
                return new OCRResponse(false, Array.Empty<OCRTextLine>(), ex.Message);
            }
        }

        private async Task<OCRResponse> RecognizeViaPipe(byte[] imageBytes)
        {
            using var pipe = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.None);
            await pipe.ConnectAsync((int)_timeout.TotalMilliseconds);

            var request = new { image = Convert.ToBase64String(imageBytes) };
            var json = JsonSerializer.Serialize(request);
            var bytes = Encoding.UTF8.GetBytes(json);

            var lenBytes = BitConverter.GetBytes(bytes.Length);
            await pipe.WriteAsync(lenBytes, 0, 4);
            await pipe.WriteAsync(bytes, 0, bytes.Length);
            await pipe.FlushAsync();

            var respLenBytes = new byte[4];
            await pipe.ReadAsync(respLenBytes, 0, 4);
            var respLen = BitConverter.ToInt32(respLenBytes, 0);

            var respBytes = new byte[respLen];
            var read = 0;
            while (read < respLen)
            {
                read += await pipe.ReadAsync(respBytes, read, respLen - read);
            }

            return ParseOCRResponse(respBytes);
        }

        private async Task<OCRResponse> RecognizeViaHttp(byte[] imageBytes)
        {
            var base64 = Convert.ToBase64String(imageBytes);
            var request = new { image = base64 };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient!.PostAsync($"http://127.0.0.1:{HTTP_PORT}/ocr/base64", content);
            response.EnsureSuccessStatusCode();

            var respBytes = await response.Content.ReadAsByteArrayAsync();
            return ParseOCRResponse(respBytes);
        }

        private OCRResponse ParseOCRResponse(byte[] respBytes)
        {
            var respJson = Encoding.UTF8.GetString(respBytes);
            using var doc = JsonDocument.Parse(respJson);

            if (doc.RootElement.GetProperty("success").GetBoolean())
            {
                var lines = new List<OCRTextLine>();
                foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
                {
                    lines.Add(new OCRTextLine(
                        line.GetProperty("text").GetString() ?? "",
                        line.GetProperty("confidence").GetDouble(),
                        line.GetProperty("bbox").EnumerateArray().Select(b => b.GetInt32()).ToArray()
                    ));
                }
                return new OCRResponse(true, lines.ToArray());
            }

            return new OCRResponse(false, Array.Empty<OCRTextLine>(),
                doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error");
        }

        /// <summary>
        /// Bitmap → Base64（压缩后传输）
        /// </summary>
        public static byte[] BitmapToBytes(System.Drawing.Bitmap bmp, int quality = 85)
        {
            using var ms = new MemoryStream();
            var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var ps = new System.Drawing.Imaging.EncoderParameters(1);
            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, quality
            );
            bmp.Save(ms, encoder, ps);
            return ms.ToArray();
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _httpClient = null;

            if (_pythonWorkerProcess != null && !_pythonWorkerProcess.HasExited)
            {
                _pythonWorkerProcess.Kill();
                _pythonWorkerProcess.Dispose();
                _pythonWorkerProcess = null;
            }
        }
    }
}
