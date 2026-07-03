"""
Sinp OCR Worker — HTTP Server（开发调试用）
生产环境请改用 NamedPipe（见 pipe_server.py）

使用方式：
  pip install -r requirements.txt
  python main.py                  # 默认 HTTP :8000
  python main.py --port 9000     # 指定端口
  python main.py --mode pipe     # NamedPipe 模式（需 pywin32）
"""

import sys
import json
import base64
import io
import argparse
import os

# ── 确保当前目录在 sys.path（方便 import 同目录模块）────────────
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from ocr_engine import get_engine
from postprocess import postprocess

# ── HTTP 模式（FastAPI）─────────────────────────────────────────
def start_http_server(port: int = 8000):
    try:
        import uvicorn
        from fastapi import FastAPI, UploadFile, File
        from fastapi.middleware.cors import CORSMiddleware
    except ImportError:
        print("[OCR Worker] 缺少依赖，请运行: pip install fastapi uvicorn python-multipart", flush=True)
        sys.exit(1)

    app = FastAPI(title="Sinp OCR Worker", version="1.0")

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_methods=["*"],
        allow_headers=["*"],
    )

    @app.get("/health")
    def health():
        return {"status": "ok", "engine": "PaddleOCR"}

    @app.post("/ocr")
    async def ocr_endpoint(file: UploadFile = File(...)):
        """OCR 识别接口（文件上传）"""
        image_bytes = await file.read()
        return _run_ocr_pipeline(image_bytes)

    @app.post("/ocr/base64")
    async def ocr_base64_endpoint(body: dict):
        """OCR 识别接口（Base64）— 供 C# NamedPipe 客户端调用"""
        image_b64 = body.get("image", "")
        image_bytes = base64.b64decode(image_b64)
        return _run_ocr_pipeline(image_bytes)

    print(f"[OCR Worker] HTTP 模式启动: http://127.0.0.1:{port}", flush=True)
    print(f"[OCR Worker] 接口: POST /ocr  |  POST /ocr/base64", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")


# ── NamedPipe 模式（Windows 生产）───────────────────────────────
def start_pipe_server():
    try:
        import win32pipe
        import win32file
        import win32security
    except ImportError:
        print("[OCR Worker] NamedPipe 模式需要 pywin32: pip install pywin32", flush=True)
        sys.exit(1)

    PIPE_NAME = "sinp_ocr_pipe"
    print(f"[OCR Worker] NamedPipe 模式启动: \\\\.\\pipe\\{PIPE_NAME}", flush=True)

    while True:
        try:
            sa = win32security.SECURITY_ATTRIBUTES()
            sa.SetSecurityDescriptor(win32security.SECURITY_DESCRIPTOR())

            pipe = win32pipe.CreateNamedPipe(
                f"\\\\.\\pipe\\{PIPE_NAME}",
                win32pipe.PIPE_ACCESS_DUPLEX,
                win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_WAIT,
                1, 65536, 65536, 0, sa
            )

            print("[OCR Worker] 等待客户端连接...", flush=True)
            win32pipe.ConnectNamedPipe(pipe, None)
            print("[OCR Worker] 客户端已连接，处理请求...", flush=True)

            # 读 4 字节长度头
            nread, len_bytes = win32file.ReadFile(pipe, 4)
            total = int.from_bytes(len_bytes, "little")

            # 读请求体
            data = b""
            while len(data) < total:
                n, chunk = win32file.ReadFile(pipe, min(4096, total - len(data)))
                data += chunk

            req = json.loads(data.decode("utf-8"))
            image_bytes = base64.b64decode(req["image"])

            result = _run_ocr_pipeline(image_bytes)

            resp = json.dumps(result, ensure_ascii=False).encode("utf-8")
            resp_len = len(resp).to_bytes(4, "little")
            win32file.WriteFile(pipe, resp_len + resp)
            win32pipe.DisconnectNamedPipe(pipe)
            win32file.CloseHandle(pipe)

        except KeyboardInterrupt:
            print("\n[OCR Worker] 退出", flush=True)
            break
        except Exception as e:
            print(f"[OCR Worker] Pipe 处理错误: {e}", flush=True)


# ── OCR 流水线（共用）───────────────────────────────────────────
def _run_ocr_pipeline(image_bytes: bytes) -> dict:
    """图片 bytes → OCR 结果（含后处理）"""
    try:
        engine = get_engine()
        raw_lines = engine.infer(image_bytes)
        cleaned = postprocess(raw_lines)

        return {
            "success": True,
            "lines": cleaned,
            "count": len(cleaned)
        }
    except Exception as e:
        print(f"[OCR Worker] OCR 推理错误: {e}", flush=True)
        return {
            "success": False,
            "error": str(e),
            "lines": [],
            "count": 0
        }


# ── 入口 ────────────────────────────────────────────────────────
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Sinp OCR Worker")
    parser.add_argument("--mode", choices=["http", "pipe"], default="http",
                        help="通信模式: http(调试) / pipe(生产)")
    parser.add_argument("--port", type=int, default=8000,
                        help="HTTP 模式端口 (默认 8000)")
    args = parser.parse_args()

    print("=" * 50)
    print("  Sinp OCR Worker v1.0")
    print(f"  模式: {args.mode}")
    print("=" * 50)

    if args.mode == "http":
        start_http_server(args.port)
    else:
        start_pipe_server()
