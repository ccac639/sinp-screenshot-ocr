# Sinp — 截图 & OCR 工具（工业可落地版）

> Snipaste 级截图体验 + PaddleOCR 本地离线识别

## 架构

```
C# WPF Desktop App                Python OCR Worker
┌──────────────────────┐         ┌──────────────────────┐
│  CaptureCore         │         │  main.py (FastAPI)   │
│  OverlaySystem       │  IPC    │  ocr_engine.py       │
│  StitchEngine        │ ─────→  │  inference.py        │
│  HotkeySystem        │ NamedPipe│  postprocess.py      │
│  OCRClient          │         │                       │
│  UI (MainWindow)    │         └──────────────────────┘
└──────────────────────┘
```

## 目录结构

```
D:\开发\sinp\
├── Sinp.sln                 # VS Solution
├── Sinp.App\                # 主程序（WPF）
├── CaptureCore\             # 截图引擎（DXGI/GDI）
├── OverlaySystem\           # 透明选区窗口
├── StitchEngine\           # 长截图拼接
├── HotkeySystem\            # 全局热键
├── OCRClient\              # IPC 通信客户端
├── SystemUtils\             # DPI/多屏/剪贴板
├── ocr_worker\             # Python OCR 服务
│   ├── main.py
│   ├── requirements.txt
│   └── ...
├── logs\                   # 日志（按日期滚动）
├── data\                   # OCR 导出数据
└── bin\                    # 编译输出
```

## 快速开始

### 1. 安装 .NET 8 SDK
https://dotnet.microsoft.com/download/dotnet/8.0

### 2. 安装 Python OCR 依赖
```bash
cd D:\开发\sinp\ocr_worker
pip install -r requirements.txt
```

### 3. 启动 OCR Worker（先启动）
```bash
# HTTP 模式（调试用）
python main.py --mode http --port 8000

# NamedPipe 模式（生产用）
python main.py --mode pipe
```

### 4. 编译 & 运行 C# 主程序
```bash
cd D:\开发\sinp
dotnet restore
dotnet run --project Sinp.App\Sinp.App.csproj
```

## 热键

| 热键 | 功能 |
|------|------|
| Ctrl + Shift + S | 区域截图 + OCR |
| Ctrl + Shift + L | 长截图（拼接） |
| Ctrl + Shift + O | 剪贴板图片 OCR |
| Esc | 取消选区 |

## 技术栈

- **C# / WPF** — .NET 8 (Windows 10 17763+)
- **OpenCvSharp4** — 图像拼接（ORB 特征匹配）
- **PaddleOCR** — 中文 OCR 引擎（本地离线）
- **NamedPipe / FastAPI** — IPC 通信

## 开发状态

- [x] 项目结构 & Solution
- [x] 日志系统
- [x] GDI 截图引擎（DXGI 待接入 SharpDX）
- [x] 透明选区 Overlay
- [x] 全局热键
- [x] OCRClient IPC（NamedPipe）
- [x] Python OCR Worker（HTTP 模式）
- [x] 主窗口 UI（深色主题）
- [x] 截图 → OCR → 结果显示流程
- [ ] DXGI Desktop Duplication 引擎
- [ ] 长截图拼接（StitchEngine 集成）
- [ ] 历史记录面板
- [ ] 导出 Word / Excel
- [ ] 系统托盘图标

## 日志

日志存放在 `D:\开发\sinp\logs\YYYY-MM-DD.log`，按日期自动滚动。

## License

MIT
