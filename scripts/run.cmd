@echo off
rem 双击运行用的小壳子：转给 run.ps1（参数原样透传）
chcp 65001 >nul

where pwsh >nul 2>nul
if errorlevel 1 (
    echo 需要 PowerShell 7 ^(pwsh^)，安装一下：winget install Microsoft.PowerShell
    pause
    exit /b 1
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 pause
