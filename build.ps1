# Sinp Build Script (PowerShell)
# 右键 → 使用 PowerShell 运行
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Host.UI.RawUI.WindowTitle = "Sinp - Build & Run"
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Sinp Screenshot OCR Tool - Build" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Set-Location $PSScriptRoot
$projDir = Get-Location

# 1. 检查 .NET SDK
Write-Host "[1/4] 检查 .NET SDK..." -NoNewline
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host " FAIL" -ForegroundColor Red
    Write-Host "[ERROR] 未找到 dotnet，请安装 .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    Read-Host "按回车退出"
    exit 1
}
$ver = (& dotnet --version 2>$null)
Write-Host " OK (dotnet $ver)" -ForegroundColor Green

# 2. 清理旧产物
Write-Host "[2/4] 清理旧编译产物..."
Get-ChildItem -Path $projDir -Recurse -Directory -Filter "obj" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $projDir -Recurse -Directory -Filter "bin" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 3. Restore + Build
Write-Host "[3/4] Restore NuGet 包..."
& dotnet restore Sinp.sln --no-interactive 2>&1 | Out-Null

Write-Host "[4/4] 编译中..." -ForegroundColor Yellow
$output = dotnet build Sinp.sln -c Release --no-restore 2>&1
$lastLine = $output | Select-Object -Last 1

if ($lastLine -match "成功|succeeded") {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  BUILD SUCCESS!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    $exePath = "$projDir\Sinp.App\bin\Release\net8.0-windows10.0.17763.0\Sinp.exe"
    Write-Host "输出: $exePath" -ForegroundColor Cyan
    Write-Host ""

    $run = Read-Host "是否立即运行? (Y/n)"
    if ($run -eq "" -or $run -match "^y") {
        Start-Process $exePath
        Write-Host "已启动!" -ForegroundColor Green
    }
} else {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  BUILD FAILED" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    $output | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Read-Host "`n按回车退出"
}
