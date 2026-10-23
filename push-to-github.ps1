# 把本地仓库推到 GitHub（用你自己的凭据：PAT 或 SSH）
# 例： powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Repo https://github.com/<账号>/PclDl.git -Token <PAT>
# 例： powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Repo git@github.com:<账号>/PclDl.git -SshKey G:\gh-key\id_ed25519
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Token,
    [string]$SshKey
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
if ($SshKey) { $env:GIT_SSH_COMMAND = 'ssh -i ' + $SshKey + ' -o StrictHostKeyChecking=accept-new' }
$url = $Repo
if ($Token) {
    $u = [uri]$Repo
    $url = $u.Scheme + '://x-access-token:' + $Token + '@' + $u.Host + $u.AbsolutePath
}
git push $url HEAD:refs/heads/main
if ($LASTEXITCODE -ne 0) { throw 'push failed' }
git remote remove origin 2>$null
git remote add origin $Repo
Write-Host ('pushed, origin = ' + $Repo)