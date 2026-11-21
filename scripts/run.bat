@echo off
rem 瞬觅 ShunSeek · 双击启动脚本（纯批处理，不依赖 PowerShell / bash）
rem
rem 直接双击 = 编译 + 启动并弹出搜索窗；也可以在命令行里带参数：
rem   run.bat -Release      用 Release 配置
rem   run.bat -NoBuild      不编译，直接启动上次的产物
rem   run.bat -BuildOnly    只编译，不启动
rem   run.bat -Bench        跑速度基准（200 万条合成数据）
rem   run.bat -NoShow       启动但不弹窗（只进托盘）
setlocal
cd /d "%~dp0.."

set "CONFIG=Debug"
set "NOBUILD="
set "BENCH="
set "BUILDONLY="
set "SHOW=1"
set "RAWARGS=%*"

:args
if "%~1"=="" goto prepare
if /i "%~1"=="-release"   set "CONFIG=Release"
if /i "%~1"=="-nobuild"   set "NOBUILD=1"
if /i "%~1"=="-bench"     set "BENCH=1"
if /i "%~1"=="-buildonly" set "BUILDONLY=1"
if /i "%~1"=="-show"      set "SHOW=1"
if /i "%~1"=="-noshow"    set "SHOW="
shift
goto args

:prepare
rem ── 找 dotnet：PATH → Program Files → 用户目录（dotnet-install 免管理员装的那种）──
set "DOTNET="
for %%D in (dotnet.exe) do if not defined DOTNET if exist "%%~$PATH:D" set "DOTNET=%%~$PATH:D"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LocalAppData%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LocalAppData%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET if exist "%UserProfile%\.dotnet\dotnet.exe" set "DOTNET=%UserProfile%\.dotnet\dotnet.exe"
if not defined DOTNET (
    echo [错误] 找不到 dotnet，先装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

rem 装在用户目录的 SDK 没写系统环境变量，WPF 的 exe 会报 "You must install .NET"，这里补上
for %%P in ("%DOTNET%") do set "DOTNET_ROOT=%%~dpP"
set "PATH=%DOTNET_ROOT%;%PATH%"

if defined BENCH (
    "%DOTNET%" run --project "src\ShunSeek.Bench\ShunSeek.Bench.csproj" -c %CONFIG%
    echo.
    pause
    exit /b 0
)

rem 程序要是还跑着，exe 被占用，编译会失败——先结束它
taskkill /im ShunSeek.exe /f >nul 2>nul
if not errorlevel 1 (
    ping -n 1 -w 500 127.0.0.1 >nul
    echo 已结束上一个实例。
)

if not defined NOBUILD (
    echo [1/2] 编译 %CONFIG% ...
    "%DOTNET%" build "src\ShunSeek.App\ShunSeek.App.csproj" -c %CONFIG%
    if errorlevel 1 (
        echo.
        echo [错误] 编译失败
        pause
        exit /b 1
    )
)

if defined BUILDONLY (
    echo 编译完成，未启动。
    exit /b 0
)

set "EXE=%CD%\src\ShunSeek.App\bin\%CONFIG%\net8.0-windows\ShunSeek.exe"
if not exist "%EXE%" (
    echo [错误] 找不到 %EXE%，先不带 -NoBuild 编一次
    pause
    exit /b 1
)


echo [2/2] 启动
if defined SHOW (
    start "" "%EXE%" --show
) else (
    start "" "%EXE%"
)

rem 双击时不要停在这儿，直接把控制台收掉
exit /b 0
