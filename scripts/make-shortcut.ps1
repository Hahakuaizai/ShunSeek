#Requires -Version 7.0
<#
  在桌面创建「瞬觅 ShunSeek」快捷方式，指向 E:\Apps\ShunSeek\ShunSeek.exe。
  单独做成脚本文件（UTF-8）而不是命令行里传中文——中文经 cmd/bash 转一手很容易被编码搞坏，
  之前桌面上那个 ShunSeek.lnk 就是这么来的。
#>
param(
    [string]$Target = 'E:\Apps\ShunSeek\ShunSeek.exe',
    [string]$Name = '瞬觅 ShunSeek'
)

$ErrorActionPreference = 'Stop'
$desktop = [Environment]::GetFolderPath('Desktop')

# 清掉历史遗留下来的歪名字快捷方式
Get-ChildItem $desktop -Filter *.lnk | Where-Object {
    $_.BaseName -in @('ShunSeek', '瞬觅 ShunSeek') -or $_.BaseName -like '*ShunSeek*'
} | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Output "已删除旧快捷方式：$($_.Name)"
}

$lnkPath = Join-Path $desktop ($Name + '.lnk')
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnkPath)
$sc.TargetPath = $Target
$sc.WorkingDirectory = Split-Path $Target -Parent
$sc.IconLocation = "$Target,0"
$sc.Description = '瞬觅 ShunSeek · Alt+Space 呼出搜索'
$sc.Save()

# 让资源管理器重读图标缓存
Get-Command ie4uinit.exe -ErrorAction SilentlyContinue | Out-Null
& ie4uinit.exe -show 2>$null

$verify = $shell.CreateShortcut($lnkPath)
Write-Output "快捷方式：$($verify.FullName)"
Write-Output "目标    ：$($verify.TargetPath)"
Write-Output "图标    ：$($verify.IconLocation)"
