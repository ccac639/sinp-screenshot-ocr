@echo off
echo 正在启动 Sinp 截图 OCR 工具...

:: 杀死旧进程
taskkill /f /im Sinp.App.exe >nul 2>&1

:: 等待 1 秒
timeout /t 1 /nobreak >nul

:: 启动应用（从项目目录）
cd /d D:\sinp\Sinp.App\bin\Debug\net8.0-windows
start "" "Sinp.App.exe"

echo 已启动！请查看任务栏或按 Alt+Tab 切换
pause
