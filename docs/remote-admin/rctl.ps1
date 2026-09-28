#Requires -Version 5.1
<#
.SYNOPSIS
  在电脑上通过 RemoteAdminAgent 以 SYSTEM 身份执行命令。

.EXAMPLE
  pwsh -File .\rctl.ps1 'Get-Service GameViewerService | Format-List Name,Status,StartType'

.EXAMPLE
  # 令牌可放在 %USERPROFILE%\.remote-admin-token 里，避免每次输入
  'xxxxxxxx' | Set-Content $env:USERPROFILE\.remote-admin-token -NoNewline
  pwsh -File .\rctl.ps1 'Restart-Service TermService'
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$Command,
  [string]$Server = '100.99.99.105',
  [int]$Port = 8443,
  [string]$Token,
  [string]$TokenFile = (Join-Path $env:USERPROFILE '.remote-admin-token'),
  [int]$TimeoutSec = 300,
  [switch]$Json
)

$ErrorActionPreference = 'Stop'

if (-not $Token -and (Test-Path $TokenFile)) { $Token = (Get-Content $TokenFile -Raw).Trim() }
if (-not $Token) { throw "缺少令牌：用 -Token 传入，或写入 $TokenFile" }

$url  = "http://${Server}:$Port/api/exec"
$body = @{ cmd = $Command; timeoutSec = $TimeoutSec } | ConvertTo-Json -Compress
$headers = @{ 'X-Auth-Token' = $Token }

try {
  $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body
} catch {
  Write-Host "[连接失败] $($_.Exception.Message)" -ForegroundColor Red
  Write-Host '排查：1) Agent 是否运行（Get-ScheduledTask RemoteAdminAgent）2) 防火墙 8443 3) Tailscale 是否在线' -ForegroundColor Yellow
  exit 2
}

if ($Json) { $resp | ConvertTo-Json -Depth 6; exit $resp.exitCode }

if ($resp.stdout) { Write-Host $resp.stdout }
if ($resp.stderr) { Write-Host $resp.stderr -ForegroundColor Yellow }
if ($resp.timedOut) { Write-Host "[超时] 超过 ${TimeoutSec}s，进程已被终止" -ForegroundColor Red }
if ($resp.truncated) { Write-Host '[提示] 输出过长已截断' -ForegroundColor Yellow }
Write-Host ("--- exitCode={0}  {1}ms ---" -f $resp.exitCode, $resp.durationMs) -ForegroundColor Gray

exit $resp.exitCode
