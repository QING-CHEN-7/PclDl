<#
.SYNOPSIS
  PclDl 插件部署脚本：把 dist 安装到指定 PCL 目录，或卸载。
.DESCRIPTION
  只往 <PclDir>\PCL2Plugins 写文件、在 <PclDir> 创建启动快捷方式，不修改 PCL 本体。
.PARAMETER PclDir
  目标 PCL 目录（包含 "Plain Craft Launcher 2.exe"）。
.PARAMETER Action
  install / upgrade / status / uninstall，默认 install。
  upgrade 会先把旧插件目录备份成 PCL2Plugins.backup-<时间戳> 再覆盖。
.PARAMETER NoShortcut
  不创建快捷方式。
#>
param(
    [Parameter(Mandatory = $true)][string]$PclDir,
    [ValidateSet('install', 'upgrade', 'status', 'uninstall')][string]$Action = 'install',
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $here 'dist'
$PclDir = (Resolve-Path $PclDir).Path
$exe = Join-Path $PclDir 'Plain Craft Launcher 2.exe'
$pluginDir = Join-Path $PclDir 'PCL2Plugins'
$shortcut = Join-Path $PclDir '启动 PCL2（插件版）.lnk'

if (-not (Test-Path $exe)) { throw "目标目录里找不到 PCL：$exe" }

function Remove-PluginArtifacts {
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force; Write-Host "[remove] 快捷方式" }
    # 兼容旧版本残留（曾把依赖放在 PCL 根目录）
    foreach ($name in @('PclDl.Core.dll', 'PclDl.Plugin.dll', 'Newtonsoft.Json.dll', 'PclDl.Bootstrap.dll')) {
        $stale = Join-Path $PclDir $name
        if (Test-Path $stale) { Remove-Item $stale -Force; Write-Host "[remove] 旧版残留 $name" }
    }
    if (Test-Path $pluginDir) {
        Remove-Item $pluginDir -Recurse -Force
        Write-Host "[remove] $pluginDir"
    }
}

if ($Action -eq 'uninstall') {
    Remove-PluginArtifacts
    Write-Host "已卸载。PCL 本体未做任何修改。"
    exit 0
}

$installFile = Join-Path $pluginDir 'install.json'

if ($Action -eq 'status') {
    if (-not (Test-Path $installFile)) { Write-Host "未安装（找不到 $installFile）"; exit 1 }
    $meta = Get-Content $installFile -Raw | ConvertFrom-Json
    Write-Host ("产品：" + $meta.product)
    Write-Host ("版本：" + $meta.version)
    Write-Host ("安装时间：" + $meta.installedAt)
    Write-Host ("PCL：" + $meta.pclExe)
    Write-Host ("插件目录：" + $pluginDir)
    $bad = 0
    foreach ($entry in $meta.files) {
        $path = Join-Path $pluginDir $entry.name
        if (-not (Test-Path $path)) { Write-Host ("  [缺失] " + $entry.name); $bad++; continue }
        $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $entry.sha256) { Write-Host ("  [改动] " + $entry.name); $bad++ }
    }
    if ($bad -eq 0) { Write-Host ("文件校验：全部一致（" + $meta.files.Count + " 个）") }
    else { Write-Host ("文件校验：" + $bad + " 处不一致") }
    exit ($(if ($bad -eq 0) { 0 } else { 1 }))
}

if (-not (Test-Path $dist)) { throw "找不到 dist 目录，请先运行 build.ps1" }

# dist/build.json 由 build.ps1 生成，带上版本号与每个文件的 sha256
$buildFile = Join-Path $dist 'build.json'
$build = $null
if (Test-Path $buildFile) { $build = Get-Content $buildFile -Raw | ConvertFrom-Json }
$version = if ($build) { $build.version } else { '0.1.0' }

function Backup-ExistingPlugin {
    if (-not (Test-Path $pluginDir)) { return $null }
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $backup = Join-Path $PclDir ('PCL2Plugins.backup-' + $stamp)
    Copy-Item $pluginDir $backup -Recurse -Force
    Write-Host ("[backup] " + $backup)
    return $backup
}

$backupPath = $null
if ($Action -eq 'upgrade') {
    $oldVersion = if (Test-Path $installFile) { (Get-Content $installFile -Raw | ConvertFrom-Json).version } else { $null }
    Write-Host ("升级：" + $(if ($oldVersion) { $oldVersion } else { '(未安装)' }) + " -> " + $version)
    $backupPath = Backup-ExistingPlugin
}

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$copied = 0
foreach ($name in @('PclDl.Injector.exe', 'PclDl.Bridge.dll', 'PclDl.Plugin.dll', 'PclDl.Core.dll', 'Newtonsoft.Json.dll', 'PclDl.Probe.exe')) {
    $src = Join-Path $dist $name
    if (-not (Test-Path $src)) { Write-Warning "缺少产物：$name"; continue }
    Copy-Item $src (Join-Path $pluginDir $name) -Force
    $copied++
}
Write-Host "[ok] 已复制 $copied 个文件到 $pluginDir"

$installed = @()
foreach ($name in @('PclDl.Injector.exe', 'PclDl.Bridge.dll', 'PclDl.Plugin.dll', 'PclDl.Core.dll', 'Newtonsoft.Json.dll', 'PclDl.Probe.exe')) {
    $path = Join-Path $pluginDir $name
    if (-not (Test-Path $path)) { continue }
    $installed += [ordered]@{
        name   = $name
        size   = (Get-Item $path).Length
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
    }
}

$builtAt = $null
if ($build) { $builtAt = $build.builtAt }

$manifest = [ordered]@{
    product       = 'PclDl'
    version       = $version
    installedAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    pclExe        = $exe
    builtAt       = $builtAt
    backup        = $backupPath
    files         = $installed
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $pluginDir 'install.json') -Encoding UTF8

if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = Join-Path $pluginDir 'PclDl.Injector.exe'
    $lnk.Arguments = "--pcl `"$exe`" --auto-mount"
    $lnk.WorkingDirectory = $pluginDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = '通过 PclDl 插件启动 PCL2'
    $lnk.Save()
    Write-Host "[ok] 快捷方式：$shortcut"
}

Write-Host "安装完成。以后请用快捷方式启动 PCL2。"
