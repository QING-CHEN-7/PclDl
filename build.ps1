# Build and assemble dist
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

dotnet build pcl2-mcdl.sln -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "managed build failed" }

# native bridge (C++/CLI)
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
if (Test-Path $vcvars) {
    cmd /c (Join-Path $root 'native\build-native.bat')
    if ($LASTEXITCODE -ne 0) { throw "native build failed" }
} else {
    Write-Warning "vcvars64.bat not found, skip native bridge"
}

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist 'cli') -Force | Out-Null

$files = @(
    'src\PclDl.Injector\bin\Release\PclDl.Injector.exe',
    'src\PclDl.Launcher\bin\Release\PclDl.Launcher.exe',
    'src\PclDl.Bootstrap\bin\Release\PclDl.Bootstrap.dll',
    'src\PclDl.Plugin\bin\Release\PclDl.Plugin.dll',
    'src\PclDl.Core\bin\Release\PclDl.Core.dll',
    'src\PclDl.Host\bin\Release\PclDl.Host.exe',
    'src\PclDl.Probe\bin\Release\PclDl.Probe.exe',
    'native\PclDl.Bridge\bin\PclDl.Bridge.dll',
    'libs\Newtonsoft.Json.dll'
)
foreach ($f in $files) {
    $src = Join-Path $root $f
    if (-not (Test-Path $src)) { Write-Warning ("missing: " + $src); continue }
    Copy-Item $src (Join-Path $dist (Split-Path $src -Leaf)) -Force
}

$cliFiles = @('src\PclDl.Cli\bin\Release\PclDl.Cli.exe','src\PclDl.Core\bin\Release\PclDl.Core.dll','libs\Newtonsoft.Json.dll')
foreach ($f in $cliFiles) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $dist ('cli\' + (Split-Path $src -Leaf))) -Force }
}

# ---- 单文件一键安装器（PclDl.Setup.exe）----
$setupProj = Join-Path $root 'src\PclDl.Setup'
$payload = Join-Path $setupProj 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$distFiles = Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'PclDl.Setup.exe' -and $_.Name -ne 'build.json' }
$setupVersion = (Get-Date).ToString('yyyy.MM.dd.HHmm')
$versionTmp = Join-Path $env:TEMP ('pcldl-version-' + [guid]::NewGuid().ToString('N') + '.txt')
Set-Content -Path $versionTmp -Value $setupVersion -Encoding UTF8
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $versionTmp, 'version.txt') | Out-Null
    foreach ($f in $distFiles) { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $f.Name) | Out-Null }
    $cliDir = Join-Path $dist 'cli'
    if (Test-Path $cliDir) {
        foreach ($f in Get-ChildItem $cliDir -File) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, 'cli/' + $f.Name) | Out-Null
        }
    }
} finally { $zip.Dispose(); Remove-Item $versionTmp -Force -ErrorAction SilentlyContinue }
Write-Host ("payload 打包完成：" + [math]::Round((Get-Item $payload).Length / 1MB, 2) + " MB")

dotnet build (Join-Path $setupProj 'PclDl.Setup.csproj') -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }
$setupExe = Join-Path $setupProj 'bin\Release\net48\PclDl.Setup.exe'
if (Test-Path $setupExe) { Copy-Item $setupExe (Join-Path $dist 'PclDl.Setup.exe') -Force }
else { Write-Warning "PclDl.Setup.exe 未生成" }

Write-Host ("dist ready: " + ((Get-ChildItem $dist -File | Select-Object -ExpandProperty Name) -join ', '))
Write-Host ("version: " + $version)

# 版本戳 + 产物指纹：部署脚本 / 安装器用它做"安装/升级/校验"
$version = (Get-Date).ToString('yyyy.MM.dd.HHmm')
$entries = @()
foreach ($f in (Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'build.json' })) {
    $entries += [ordered]@{
        name   = $f.Name
        size   = $f.Length
        sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
    }
}
$build = [ordered]@{
    product = 'PclDl'
    version = $version
    builtAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    files   = $entries
}
$build | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dist 'build.json') -Encoding UTF8
Write-Host ("version: " + $version)
