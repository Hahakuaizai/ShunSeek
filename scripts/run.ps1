#Requires -Version 7.0
<#
    瞬觅 ShunSeek · 启动脚本

    用法（在本目录下）：
      pwsh -File .\run.ps1              # Debug 编译并启动
      pwsh -File .\run.ps1 -Release     # Release 编译并启动
      pwsh -File .\run.ps1 -NoBuild     # 不编译，直接启动上次的产物
      pwsh -File .\run.ps1 -BuildOnly   # 只编译，不启动
      pwsh -File .\run.ps1 -Bench       # 跑速度基准（2 百万条合成数据，不启界面）
      pwsh -File .\run.ps1 -Wait        # 启动后等它退出（前台调试）

    说明：
      - 会自动找 dotnet：优先 PATH 里的，其次 %ProgramFiles%\dotnet、
        %USERPROFILE%\.dotnet（dotnet-install 免管理员装的那种）。
      - 这种"装在用户目录"的 SDK 不会被系统登记，WPF 的 exe 直接双击会报
        "You must install .NET"。所以脚本会给子进程补上 DOTNET_ROOT / PATH。
      - 启动前会先结束已在跑的 ShunSeek：程序是单实例的，不结束的话
        新进程会自己退出（只会把旧窗口叫出来）。
#>
param(
    [switch]$Release,
    [switch]$Bench,
    [switch]$NoBuild,
    [switch]$BuildOnly,
    [switch]$Show,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent   # 脚本在 scripts\ 下，仓库根在上一层
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$appProject = Join-Path $root 'src\ShunSeek.App\ShunSeek.App.csproj'
$benchProject = Join-Path $root 'src\ShunSeek.Bench\ShunSeek.Bench.csproj'
$appExe = Join-Path $root "src\ShunSeek.App\bin\$configuration\net8.0-windows\ShunSeek.exe"

function Resolve-Dotnet {
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($cmd) { return @{ Exe = $cmd.Source; Root = $null } }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return @{ Exe = $candidate; Root = (Split-Path $candidate -Parent) }
        }
    }

    throw '找不到 dotnet。装一个 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0'
}

$dotnet = Resolve-Dotnet
Write-Host "dotnet : $($dotnet.Exe)" -ForegroundColor DarkGray
Write-Host "配置   : $configuration" -ForegroundColor DarkGray

# 让 WPF 的 apphost 能找到运行时（用户目录安装的 SDK 不写系统环境变量）
if ($dotnet.Root) {
    $env:DOTNET_ROOT = $dotnet.Root
    if (($env:PATH -split ';') -notcontains $dotnet.Root) {
        $env:PATH = "$($dotnet.Root);$env:PATH"
    }
}

if ($Bench) {
    & $dotnet.Exe run --project $benchProject -c $configuration
    exit $LASTEXITCODE
}

if (-not $NoBuild) {
    Write-Host "编译中…" -ForegroundColor Cyan
    & $dotnet.Exe build $appProject -c $configuration
    if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
}

if ($BuildOnly) {
    Write-Host '编译完成，未启动。' -ForegroundColor Green
    exit 0
}

if (-not (Test-Path $appExe)) {
    throw "找不到 $appExe，先去掉 -NoBuild 编一次。"
}

# 单实例：先结束旧进程，否则新进程会立刻退出
$running = Get-Process -Name ShunSeek -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    Write-Host '已结束上一个实例。' -ForegroundColor DarkGray
}

Write-Host "启动 $appExe" -ForegroundColor Cyan
$arguments = if ($Show) { @('--show') } else { @() }
$process = Start-Process $appExe -ArgumentList $arguments -PassThru
Start-Sleep -Milliseconds 1200

if ($process.HasExited) {
    throw "进程退出了（退出码 $($process.ExitCode)）。崩溃日志在 %LocalAppData%\ShunSeek\logs"
}

Write-Host ''
Write-Host '已经在托盘里跑起来了：' -ForegroundColor Green
Write-Host '  Alt+Space           呼出搜索窗'
Write-Host '  托盘右键            立即刷新索引 / 重建索引 / 以管理员身份重启（全盘索引）'
Write-Host '  首次启动正在后台建索引，状态栏能看到进度；普通权限只索引用户目录'
Write-Host ''

if ($Wait) {
    Write-Host "等待进程 $($process.Id) 退出…" -ForegroundColor DarkGray
    $process.WaitForExit()
}
