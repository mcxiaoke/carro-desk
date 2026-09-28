#Requires -Version 5.1
<#
.SYNOPSIS
  回滚 setup-remote-access.ps1 所做的一切改动。

.DESCRIPTION
  依据 C:\ProgramData\RemoteAdmin\state.json 中记录的原始值恢复：
  RDP 开关与服务启动类型、WinRM 服务与 LocalAccountTokenFilterPolicy、
  sshd_config 与 administrators_authorized_keys、计划任务与防火墙规则、URL 保留。

.EXAMPLE
  pwsh -File .\uninstall-remote-access.ps1            # 全部回滚
  pwsh -File .\uninstall-remote-access.ps1 -Agent     # 只卸 Agent
#>
[CmdletBinding()]
param(
  [switch]$All, [switch]$Rdp, [switch]$WinRm, [switch]$Ssh, [switch]$Agent
)

$ErrorActionPreference = 'Stop'
$DataRoot  = 'C:\ProgramData\RemoteAdmin'
$StatePath = Join-Path $DataRoot 'state.json'

function Write-Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok([string]$m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Info([string]$m) { Write-Host "  $m" -ForegroundColor Gray }
function Write-Warn2([string]$m){ Write-Host "  [!] $m" -ForegroundColor Yellow }

function Test-Admin {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) {
  $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
  if ($All) { $a += '-All' }
  if ($Rdp) { $a += '-Rdp' }
  if ($WinRm) { $a += '-WinRm' }
  if ($Ssh) { $a += '-Ssh' }
  if ($Agent) { $a += '-Agent' }
  $exe = if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { 'pwsh.exe' } else { 'powershell.exe' }
  Start-Process -FilePath $exe -Verb RunAs -ArgumentList $a
  exit 0
}

if ($All) { $Rdp = $true; $WinRm = $true; $Ssh = $true; $Agent = $true }
if (-not ($Rdp -or $WinRm -or $Ssh -or $Agent)) { $Rdp = $true; $WinRm = $true; $Ssh = $true; $Agent = $true }

$state = $null
if (Test-Path $StatePath) {
  try { $state = Get-Content $StatePath -Raw -Encoding utf8 | ConvertFrom-Json } catch { $state = $null }
  Write-Info "已读取状态文件 $StatePath"
} else {
  Write-Warn2 "未找到 $StatePath，将按“回到 Windows 默认”处理（RDP 关闭、移除 Agent 等）"
}
$rules = @('RemoteAdmin-RDP-Tailnet', 'RemoteAdmin-WinRM-Tailnet', 'RemoteAdmin-SSH-Tailnet', 'RemoteAdmin-Agent-Tailnet')

function Remove-FirewallRules([string[]]$names) {
  foreach ($n in $names) {
    if (Get-NetFirewallRule -Name $n -ErrorAction SilentlyContinue) {
      Remove-NetFirewallRule -Name $n -ErrorAction SilentlyContinue
      Write-Ok "已删除防火墙规则 $n"
    }
  }
}

# ---------------------------------------------------------------- RDP
if ($Rdp) {
  Write-Step '回滚 RDP'
  Remove-FirewallRules @('RemoteAdmin-RDP-Tailnet')
  $k = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
  $target = if ($state -and $null -ne $state.rdpDenyOriginal) { $state.rdpDenyOriginal } else { 1 }
  Set-ItemProperty $k -Name fDenyTSConnections -Value $target -Type DWord
  Write-Ok "fDenyTSConnections = $target"

  if ($state -and $state.termServiceOriginal -and $state.termServiceOriginal -ne 'Automatic') {
    Set-Service TermService -StartupType $state.termServiceOriginal
    Write-Ok "TermService 启动类型恢复为 $($state.termServiceOriginal)"
  } else {
    Write-Info 'TermService 启动类型保持 Automatic（本就是系统默认）'
  }
}

# ---------------------------------------------------------------- WinRM
if ($WinRm) {
  Write-Step '回滚 WinRM'
  Remove-FirewallRules @('RemoteAdmin-WinRM-Tailnet')
  $pol = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
  if ($state -and $state.tokenFilterExisted -eq $false) {
    Remove-ItemProperty -Path $pol -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue
    Write-Ok '已移除 LocalAccountTokenFilterPolicy（恢复“远程 UAC 限制”）'
  } elseif ($state -and $null -ne $state.tokenFilterOriginal) {
    Set-ItemProperty -Path $pol -Name LocalAccountTokenFilterPolicy -Value $state.tokenFilterOriginal -Type DWord
    Write-Ok "LocalAccountTokenFilterPolicy 恢复为 $($state.tokenFilterOriginal)"
  } else {
    Remove-ItemProperty -Path $pol -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue
    Write-Info 'LocalAccountTokenFilterPolicy 已清理'
  }

  try { Disable-PSRemoting -Force -ErrorAction Stop; Write-Ok '已执行 Disable-PSRemoting' } catch { Write-Warn2 "Disable-PSRemoting 失败：$($_.Exception.Message)" }
  if ($state -and $state.winrmServiceOriginal) {
    Set-Service WinRM -StartupType $state.winrmServiceOriginal
    Write-Ok "WinRM 启动类型恢复为 $($state.winrmServiceOriginal)"
  }
  Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP-PUBLIC' -ErrorAction SilentlyContinue |
    Enable-NetFirewallRule -ErrorAction SilentlyContinue
  Write-Info 'WINRM-HTTP-In-TCP-PUBLIC 已恢复启用'
}

# ---------------------------------------------------------------- SSH
if ($Ssh) {
  Write-Step '回滚 SSH'
  Remove-FirewallRules @('RemoteAdmin-SSH-Tailnet')
  if ($state -and $state.sshConfigOriginal -and $state.sshConfigOriginal.Count -gt 0) {
    Set-Content -Path 'C:\ProgramData\ssh\sshd_config' -Value $state.sshConfigOriginal -Encoding utf8
    Write-Ok 'sshd_config 已还原'
  }
  $admKeys = 'C:\ProgramData\ssh\administrators_authorized_keys'
  if ($state -and $state.sshKeysOriginalLines) {
    if ($state.sshKeysOriginalLines.Count -gt 0) {
      Set-Content -Path $admKeys -Value $state.sshKeysOriginalLines -Encoding utf8
      Write-Ok 'administrators_authorized_keys 已还原'
    } elseif (-not $state.sshKeysFileExisted -and (Test-Path $admKeys)) {
      Remove-Item $admKeys -Force
      Write-Ok 'administrators_authorized_keys 已删除（原先不存在）'
    }
  } else {
    Write-Warn2 '状态里没有公钥记录，请手动检查 C:\ProgramData\ssh\administrators_authorized_keys'
  }
  if (Get-Service sshd -ErrorAction SilentlyContinue) {
    Restart-Service sshd -ErrorAction SilentlyContinue
    Write-Ok 'sshd 已重启'
  }
}

# ---------------------------------------------------------------- Agent
if ($Agent) {
  Write-Step '卸载 SYSTEM Agent'
  if (Get-ScheduledTask -TaskName 'RemoteAdminAgent' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'RemoteAdminAgent' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'RemoteAdminAgent' -Confirm:$false
    Write-Ok '计划任务 RemoteAdminAgent 已删除'
  }
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*agent.ps1*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Write-Ok "已结束 agent 进程 $($_.ProcessId)" } catch { } }

  Remove-FirewallRules @('RemoteAdmin-Agent-Tailnet')
  $port = if ($state -and $state.agentPort) { $state.agentPort } else { 8443 }
  & netsh http delete urlacl url="http://+:$port/" 2>$null | Out-Null
  Write-Ok "已删除 URL 保留 http://+:$port/"

  foreach ($f in 'agent.ps1', 'token.txt') {
    $p = Join-Path $DataRoot $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Ok "已删除 $p" }
  }
  $logs = Join-Path $DataRoot 'logs'
  if (Test-Path $logs) { Remove-Item $logs -Recurse -Force; Write-Ok "已删除 $logs" }
  Write-Info "保留 $StatePath（其他通道回滚还要用，且不含敏感信息）"
}

Write-Host "`n================ 回滚完成 ================" -ForegroundColor Green
Write-Host '提醒：RDP 已关闭时终端服务端口会停止监听；若仍想保留远程桌面，请只回滚 -Agent/-WinRm/-Ssh。' -ForegroundColor Gray
