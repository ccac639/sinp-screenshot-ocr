using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sinp.OCRClient
{
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
    /// OCR 客户端（NamedPipe IPC → Python Worker）
    /// </summary>
    public class OCRClient : IDisposable
    {
        private const string PIPE_NAME = "sinp_ocr_pipe";
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 发送图片做 OCR 识别（异步）
        /// </summary>
        public async Task<OCRResponse> RecognizeAsync(byte[] imageBytes)
        {
            try
            {
                // 连接 Python Worker 的 NamedPipe Server
                using var pipe = new NamedPipeClientStream(
                    ".", PIPE_NAME, PipeDirection.InOut, PipeOptions.None
                );

                await pipe.ConnectAsync((int)_timeout.TotalMilliseconds);

                // 发送请求
                var request = new
                {
                    image = Convert.ToBase64String(imageBytes)
                };
                var json = JsonSerializer.Serialize(request);
                var bytes = Encoding.UTF8.GetBytes(json);

                // 先写长度
                var lenBytes = BitConverter.GetBytes(bytes.Length);
                await pipe.WriteAsync(lenBytes, 0, 4);
                await pipe.WriteAsync(bytes, 0, bytes.Length);
                await pipe.FlushAsync();

                // 读取响应长度
                var respLenBytes = new byte[4];
                await pipe.ReadAsync(respLenBytes, 0, 4);
                var respLen = BitConverter.ToInt32(respLenBytes, 0);

                // 读取响应内容
                var respBytes = new byte[respLen];
                var read = 0;
                while (read < respLen)
                {
                    read += await pipe.ReadAsync(respBytes, read, respLen - read);
                }

                var respJson = Encoding.UTF8.GetString(respBytes);
                var resp = JsonDocument.Parse(respJson);

                if (resp.RootElement.GetProperty("success").GetBoolean())
                {
                    var lines = new List<OCRTextLine>();
                    foreach (var line in resp.RootElement.GetProperty("lines").EnumerateArray())
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
                    resp.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error");
            }
            catch (Exception ex)
            {
                return new OCRResponse(false, Array.Empty<OCRTextLine>(), ex.Message);
            }
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

        public void Dispose() { }
    }
}
