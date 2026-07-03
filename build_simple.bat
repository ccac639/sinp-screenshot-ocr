@echo off
chcp 65001 >nul 2>&1
cd /d "%~dp0"

echo === Sinp Build ===
echo.

:: 检查 .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未安装 .NET 8 SDK
    echo 请访问: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
echo [OK] .NET SDK 已安装
echo.

:: 清理
echo [1/3] 清理旧产物...
for /d %%d in (*) do (
    if exist "%%d\obj" rd /s /q "%%d\obj" 2>nul
    if exist "%%d\bin" rd /s /q "%%d\bin" 2>nul
)
echo.

:: Restore
echo [2/3] 还原 NuGet 包（可能需要几分钟）...
dotnet restore Sinp.sln
if errorlevel 1 (
    echo [WARN] restore 有警告，继续编译...
)
echo.

:: Build
echo [3/3] 编译 Release...
dotnet build Sinp.sln -c Release --no-restore
if errorlevel 1 (
    echo.
    echo ==========
    echo  编译失败
    echo ==========
    echo 请把上面的错误信息发给我
    pause
    exit /b 1
)

echo.
echo ==========
echo  编译成功！
echo ==========
echo 输出路径:
echo   %~dp0Sinp.App\bin\Release\net8.0-windows10.0.17763.0\Sinp.exe
echo.

set /p RUN="是否立即运行? (Y/n): "
if /i "%RUN%"=="Y" (
    start "" "%~dp0Sinp.App\bin\Release\net8.0-windows10.0.17763.0\Sinp.exe"
) else if /i "%RUN%"=="" (
    start "" "%~dp0Sinp.App\bin\Release\net8.0-windows10.0.17763.0\Sinp.exe"
)
