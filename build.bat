@echo off
title Sinp Build

echo ========================================
echo   Sinp Screenshot OCR Tool - Build
echo ========================================
echo.

echo [0/4] Killing running instances...
:: 强力结束进程（多重保险）
taskkill /f /im Sinp.App.exe >nul 2>&1
taskkill /f /im dotnet.exe >nul 2>&1
powershell -Command "Get-Process -Name 'Sinp.App' -ErrorAction SilentlyContinue | Stop-Process -Force; Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue | Where-Object {$_.Id -ne $PID} | Stop-Process -Force" >nul 2>&1
:: 等待进程完全结束
timeout /t 2 >nul
echo Done.
echo.

echo [1/4] Creating directories...
for %%p in (Sinp.App CaptureCore OverlaySystem StitchEngine HotkeySystem OCRClient SystemUtils) do (
    mkdir "%%p\obj\Release\net8.0-windows\ref" 2>nul
    mkdir "%%p\obj\Release\net8.0-windows\refint" 2>nul
    mkdir "%%p\bin\Release\net8.0-windows" 2>nul
)
echo Done.
echo.

echo [2/4] Restoring packages...
dotnet restore Sinp.sln
if %errorlevel% neq 0 (
    echo.
    echo *** RESTORE FAILED ***
    pause
    exit /b 1
)
echo.

echo [3/4] Building...
dotnet build Sinp.sln -c Release
if %errorlevel% neq 0 (
    echo.
    echo *** BUILD FAILED ***
    echo.
    echo If file lock error: close Sinp.App.exe from Task Manager, then retry.
    pause
    exit /b 1
)
echo.
echo ========================================
echo   BUILD SUCCESS!
echo ========================================
echo.
echo Output: Sinp.App\bin\Release\net8.0-windows\Sinp.App.exe
echo.

set /p RUN="Launch now? (Y/N): "
if /i "%RUN%"=="Y" (
    start "" "Sinp.App\bin\Release\net8.0-windows\Sinp.App.exe"
)
pause
