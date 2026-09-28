#Requires -Version 5.1
<#
.SYNOPSIS
  开发机远程管理通道初始化：RDP / WinRM / SSH / SYSTEM Agent（四通道可单独或全部启用）。

.DESCRIPTION
  * 需要管理员权限，脚本会自动提权（持续一次 UAC 确认）。
  * 幂等：可重复执行，不会重复添加防火墙规则或公钥。
  * 原始状态记录在 C:\ProgramData\RemoteAdmin\state.json，供 uninstall-remote-access.ps1 回滚。
  * 所有新增的入站防火墙规则都只允许 Tailscale 网段（100.64.0.0/10），不做公网暴露。

.PARAMETER All
  依次执行四个通道（不含 -TailscaleUnattended，需显式指定）。

.EXAMPLE
  pwsh -File .\setup-remote-access.ps1 -All -TailscaleUnattended

.EXAMPLE
  pwsh -File .\setup-remote-access.ps1 -Rdp

.EXAMPLE
  $key = Get-Content .\phone.pub -Raw
  pwsh -File .\setup-remote-access.ps1 -Ssh -SshPublicKey $key
#>
[CmdletBinding()]
param(
  [switch]$All,
  [switch]$Rdp,
  [switch]$WinRm,
  [switch]$Ssh,
  [switch]$Agent,
  [switch]$TailscaleUnattended,

  # SSH：客户端公钥（字符串或文件路径），形如 "ssh-ed25519 AAAA... phone"
  [string]$SshPublicKey,
  [string]$SshPublicKeyPath,
  [switch]$DisableSshPassword,

  # Agent：访问令牌；不传则随机生成并打印
  [string]$Token,
  [int]$AgentPort = 8443,

  # 可选：把进度写成 JSONL，便于无人在场时轮询
  [string]$StatusFile
)

$ErrorActionPreference = 'Stop'
$TailnetCidr = '100.64.0.0/10'
$DataRoot = 'C:\ProgramData\RemoteAdmin'
$StatePath = Join-Path $DataRoot 'state.json'
$HostUrl = 'https://tailscale.com/download'

# ---------------------------------------------------------------- 输出与状态
function Write-Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan; Emit $m 'progress' }
function Write-Ok([string]$m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Info([string]$m) { Write-Host "  $m" -ForegroundColor Gray }
function Write-Warn2([string]$m){ Write-Host "  [!] $m" -ForegroundColor Yellow; Emit $m 'warn' $m }
function Emit([string]$phase, [string]$st, [string]$msg) {
  if (-not $StatusFile) { return }
  try {
    ([pscustomobject]@{ phase = $phase; state = $st; msg = "$msg" } | ConvertTo-Json -Compress) |
      Add-Content -Path $StatusFile -Encoding utf8
  } catch { }
}

function Test-Admin {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- 自提权
if (-not (Test-Admin)) {
  Write-Host '需要管理员权限，正在提权（请在 UAC 对话框中点“是”）...' -ForegroundColor Yellow
  $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
  if ($All) { $a += '-All' }
  if ($Rdp) { $a += '-Rdp' }
  if ($WinRm) { $a += '-WinRm' }
  if ($Ssh) { $a += '-Ssh' }
  if ($Agent) { $a += '-Agent' }
  if ($TailscaleUnattended) { $a += '-TailscaleUnattended' }
  if ($DisableSshPassword) { $a += '-DisableSshPassword' }
  if ($SshPublicKeyPath) { $a += @('-SshPublicKeyPath', "`"$SshPublicKeyPath`"") }
  if ($SshPublicKey) { $a += @('-SshPublicKey', "`"$SshPublicKey`"") }
  if ($Token) { $a += @('-Token', "`"$Token`"") }
  if ($AgentPort -ne 8443) { $a += @('-AgentPort', "$AgentPort") }
  if ($StatusFile) { $a += @('-StatusFile', "`"$StatusFile`"") }
  $exe = if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { 'pwsh.exe' } else { 'powershell.exe' }
  Start-Process -FilePath $exe -Verb RunAs -ArgumentList $a
  exit 0
}

if ($All) { $Rdp = $true; $WinRm = $true; $Ssh = $true; $Agent = $true }
if (-not ($Rdp -or $WinRm -or $Ssh -or $Agent -or $TailscaleUnattended)) {
  throw '未指定任何动作。请用 -All 或至少一个 -Rdp/-WinRm/-Ssh/-Agent/-TailscaleUnattended。'
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
if ($StatusFile) { '' | Set-Content -Path $StatusFile -Encoding utf8 }

# ---------------------------------------------------------------- 状态记录（用于回滚）
function Get-State {
  if (Test-Path $StatePath) {
    try { return (Get-Content $StatePath -Raw -Encoding utf8 | ConvertFrom-Json) } catch { }
  }
  [pscustomobject]@{
    createdAt            = (Get-Date).ToString('s')
    tailnetCidr          = $TailnetCidr
    rdpDenyOriginal      = $null
    termServiceOriginal  = $null
    winrmServiceOriginal = $null
    tokenFilterOriginal  = $null
    tokenFilterExisted   = $false
    firewallRules        = @()
    sshKeysFileExisted   = $false
    sshKeysOriginalLines = @()
    sshConfigOriginal    = @()
    agentPort            = 0
  }
}
$state = Get-State
function Save-State { $state | ConvertTo-Json -Depth 6 | Set-Content -Path $StatePath -Encoding utf8 }

function Add-FirewallRule {
  param([string]$Name, [string]$DisplayName, [int]$Port, [string]$Protocol = 'TCP')
  if (-not ($state.firewallRules -contains $Name)) {
    $state.firewallRules += $Name
  }
  Get-NetFirewallRule -Name $Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
  New-NetFirewallRule -Name $Name -DisplayName $DisplayName -Direction Inbound -Action Allow `
    -Protocol $Protocol -LocalPort $Port -RemoteAddress $TailnetCidr -Profile Any -ErrorAction Stop | Out-Null
  Write-Ok "防火墙规则 $Name（仅 $TailnetCidr 允许入站 $Protocol/$Port）"
}

function Get-TailscaleExe {
  $p = 'C:\Program Files\Tailscale\tailscale.exe'
  if (Test-Path $p) { return $p }
  $c = Get-Command tailscale.exe -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  return $null
}

# ================================================================ 1. RDP
function Enable-Rdp {
  Write-Step '启用远程桌面（RDP）'
  $k = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
  $cur = (Get-ItemProperty $k -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
  if ($null -eq $state.rdpDenyOriginal) { $state.rdpDenyOriginal = $cur }
  Set-ItemProperty $k -Name fDenyTSConnections -Value 0 -Type DWord
  Write-Ok '注册表 fDenyTSConnections = 0'

  $w = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
  Set-ItemProperty $w -Name UserAuthentication -Value 1 -Type DWord -ErrorAction SilentlyContinue
  Write-Ok 'RDP-Tcp UserAuthentication = 1（要求 NLA，安全性更高）'

  $svc = Get-Service TermService
  if ($null -eq $state.termServiceOriginal) { $state.termServiceOriginal = $svc.StartType.ToString() }
  Set-Service TermService -StartupType Automatic
  if ($svc.Status -ne 'Running') { Start-Service TermService }
  Write-Ok "TermService = Automatic / $((Get-Service TermService).Status)"

  Add-FirewallRule -Name 'RemoteAdmin-RDP-Tailnet' -DisplayName 'RemoteAdmin RDP (Tailnet only)' -Port 3389

  # 空密码账户无法 RDP，提前提示
  $netUser = (net user $env:USERNAME) -join "`n"
  $needPwd = ($netUser -split "`n" | Select-String -Pattern 'Password required|需要密码').Line
  if ($needPwd -match 'No|否') { Write-Warn2 '当前账户密码为空，RDP 会拒绝登录，请先设置密码（net user 或 设置→账户）' }

  Start-Sleep -Seconds 2
  $listen = Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue
  if ($listen) { Write-Ok '3389 已在监听' } else { Write-Warn2 '3389 尚未监听，可能需要重启机器后再确认' }
  Save-State
}

# ================================================================ 2. WinRM
function Enable-WinRm {
  Write-Step '启用 WinRM / PowerShell Remoting'
  $svc = Get-Service WinRM
  if ($null -eq $state.winrmServiceOriginal) { $state.winrmServiceOriginal = $svc.StartType.ToString() }

  Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
  Set-Service WinRM -StartupType Automatic
  Start-Service WinRM
  Write-Ok "WinRM = Automatic / $((Get-Service WinRM).Status)"

  # 本地账户（非域）默认会被“远程 UAC 限制”过滤成标准权限，置 1 后远程会话直接拿管理员令牌
  $pol = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
  $orig = (Get-ItemProperty $pol -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
  $state.tokenFilterExisted = ($null -ne $orig)
  if ($null -eq $state.tokenFilterOriginal) { $state.tokenFilterOriginal = $orig }
  New-ItemProperty -Path $pol -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force | Out-Null
  Write-Ok 'LocalAccountTokenFilterPolicy = 1（远程会话获得完整管理员令牌，不再弹 UAC）'

  Add-FirewallRule -Name 'RemoteAdmin-WinRM-Tailnet' -DisplayName 'RemoteAdmin WinRM (Tailnet only)' -Port 5985

  # Enable-PSRemoting 会额外放开 PUBLiC 配置文件的任意来源入站，这里收回，仅保留 Tailscale 规则
  Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP-PUBLIC' -ErrorAction SilentlyContinue |
    Disable-NetFirewallRule -ErrorAction SilentlyContinue
  Write-Info '已禁用 WINRM-HTTP-In-TCP-PUBLIC（改用仅 Tailscale 的规则）'
  Save-State
}

# ================================================================ 3. SSH
function Set-SshdDirective {
  param([string]$Key, [string]$Value)
  $cfg = 'C:\ProgramData\ssh\sshd_config'
  $lines = Get-Content $cfg -Encoding utf8
  if ($null -eq $state.sshConfigOriginal -or $state.sshConfigOriginal.Count -eq 0) {
    $state.sshConfigOriginal = @($lines)
  }
  $kept = @($lines | Where-Object { $_ -notmatch "^\s*$Key\s" })
  $kept += "$Key $Value"
  Set-Content -Path $cfg -Value $kept -Encoding utf8
  Write-Ok "sshd_config: $Key $Value"
}

function Enable-Ssh {
  Write-Step '配置 SSH（复用已在运行的 sshd）'
  $svc = Get-Service sshd -ErrorAction SilentlyContinue
  if (-not $svc) { throw 'sshd 未安装。可在“设置→应用→可选功能”里安装 OpenSSH 服务器后重跑。' }
  Set-Service sshd -StartupType Automatic
  if ($svc.Status -ne 'Running') { Start-Service sshd }
  Write-Ok "sshd = Automatic / $((Get-Service sshd).Status)"

  $key = $SshPublicKey
  if (-not $key -and $SshPublicKeyPath) { $key = Get-Content $SshPublicKeyPath -Raw }
  $keysPath = Join-Path $DataRoot 'sshkeys'
  New-Item -ItemType Directory -Force -Path $keysPath | Out-Null

  if ($key) {
    $key = ($key -replace "`r?`n", ' ').Trim()
    if ($key -match 'PRIVATE KEY') { throw '给的是私钥！请只提供公钥（.pub 内容），私钥永远留在客户端。' }
    if ($key -notmatch '^(ssh-rsa|ssh-ed25519|ecdsa-sha2-|sk-ssh-)') {
      throw "公钥格式不对，应以 ssh-ed25519 / ssh-rsa / ecdsa-sha2- 开头：$key"
    }
    $blob = ($key -split '\s+')[1]
    $admKeys = 'C:\ProgramData\ssh\administrators_authorized_keys'
    if (-not (Test-Path $admKeys)) { $state.sshKeysFileExisted = $false; New-Item -ItemType File -Path $admKeys -Force | Out-Null }
    else { $state.sshKeysFileExisted = $true }
    $state.sshKeysOriginalLines = @(Get-Content $admKeys -Encoding utf8 -ErrorAction SilentlyContinue)

    $existing = @(Get-Content $admKeys -Encoding utf8 -ErrorAction SilentlyContinue)
    if ($existing -match [regex]::Escape($blob)) {
      Write-Info '该公钥已存在，跳过'
    } else {
      Add-Content -Path $admKeys -Value $key -Encoding utf8
      Write-Ok "已写入 administrators_authorized_keys：$($key.Substring(0, [Math]::Min(40, $key.Length)))..."
    }

    # 该文件必须只允许 SYSTEM 与 Administrators 读取，否则 sshd 会拒绝（用 SID，避免语言差异）
    icacls $admKeys /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    Write-Ok 'administrators_authorized_keys ACL 已收紧（SYSTEM + Administrators）'

    Set-Content -Path (Join-Path $keysPath 'authorized_keys.snapshot') -Value $key -Encoding utf8
  } else {
    Write-Warn2 '未提供 -SshPublicKey/-SshPublicKeyPath，跳过公钥配置（当前只能用密码登录 SSH）'
  }

  if ($DisableSshPassword) {
    if (-not $key) { throw '-DisableSshPassword 需要同时提供公钥，否则会把自己锁在外面。' }
    Set-SshdDirective -Key 'PasswordAuthentication' -Value 'no'
    Set-SshdDirective -Key 'PubkeyAuthentication' -Value 'yes'
  }

  Restart-Service sshd
  Start-Sleep -Seconds 2
  $sshListen = Get-NetTCPConnection -LocalPort 22 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($sshListen) { Write-Ok 'sshd 已重启，22 端口在监听' } else { Write-Warn2 'sshd 已重启，但 22 端口未监听，请检查服务日志' }

  Add-FirewallRule -Name 'RemoteAdmin-SSH-Tailnet' -DisplayName 'RemoteAdmin SSH (Tailnet only)' -Port 22
  Save-State
}

# ================================================================ 4. SYSTEM Agent
function Install-Agent {
  Write-Step '安装 SYSTEM 级远程执行端（RemoteAdminAgent）'
  $agentSrc = Join-Path $PSScriptRoot 'remote-admin-agent.ps1'
  if (-not (Test-Path $agentSrc)) { throw "缺少 $agentSrc" }
  $agentDst = Join-Path $DataRoot 'agent.ps1'
  Copy-Item $agentSrc $agentDst -Force
  Write-Ok "agent 脚本已复制到 $agentDst"

  $tokenFile = Join-Path $DataRoot 'token.txt'
  if (-not $Token) {
    if (Test-Path $tokenFile) { $Token = (Get-Content $tokenFile -Raw).Trim() }
    else {
      $bytes = New-Object byte[] 32
      [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
      $Token = ([Convert]::ToBase64String($bytes) -replace '\+', '-' -replace '/', '_' -replace '=')
    }
  }
  Set-Content -Path $tokenFile -Value $Token -Encoding ascii -NoNewline
  icacls $tokenFile /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
  $state.agentPort = $AgentPort
  Save-State

  # 监听前缀用 + 通配，靠脚本内的来源 IP 白名单（仅 Tailscale 网段/回环）做限制
  $urlAcl = "http://+:$AgentPort/"
  & netsh http delete urlacl url=$urlAcl 2>$null | Out-Null
  & netsh http add urlacl url=$urlAcl sddl='D:(A;;GX;;;SY)(A;;GX;;;BA)' 2>&1 | Out-Null
  Write-Info "已为 $urlAcl 配置 URL 保留（SYSTEM/Administrators）"

  Add-FirewallRule -Name 'RemoteAdmin-Agent-Tailnet' -DisplayName 'RemoteAdmin Agent (Tailnet only)' -Port $AgentPort

  $psExe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $action = New-ScheduledTaskAction -Execute $psExe `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$agentDst`" -Port $AgentPort"
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
  Register-ScheduledTask -TaskName 'RemoteAdminAgent' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
  Write-Ok '计划任务 RemoteAdminAgent 已注册（SYSTEM / 开机启动）'

  Start-ScheduledTask -TaskName 'RemoteAdminAgent'
  Start-Sleep -Seconds 4
  $probe = Test-NetConnection -ComputerName '127.0.0.1' -Port $AgentPort -InformationLevel Quiet -WarningAction SilentlyContinue
  if ($probe) { Write-Ok "Agent 已在 $AgentPort 监听" }
  else { Write-Warn2 "Agent 未监听，请看 $DataRoot\logs\agent-error.log" }

  $tail = (Get-TailscaleExe)
  $ip = if ($tail) { (& $tail ip -4) } else { '<tailscale-ip>' }
  Write-Host ''
  Write-Host '  ===== Agent 访问信息（请保存到手机/密码管理器） =====' -ForegroundColor White
  Write-Host "  URL   : http://$ip`:$AgentPort/" -ForegroundColor White
  Write-Host "  Token : $Token" -ForegroundColor White
  Write-Host "  令牌文件: $tokenFile" -ForegroundColor Gray
}

# ================================================================ 5. Tailscale unattended
function Set-TailscaleUnattended {
  Write-Step '设置 Tailscale 无人值守模式（注销/无登录时保持在线）'
  $tail = Get-TailscaleExe
  if (-not $tail) { Write-Warn2 '未找到 tailscale.exe，跳过'; return }
  $out = & $tail up --unattended=true 2>&1
  $out | ForEach-Object { Write-Info "$_" }
  $st = & $tail status 2>&1 | Select-Object -First 1
  Write-Ok "tailscale: $st"
}

# ================================================================ 执行
$summary = @()
try {
  if ($Rdp) {
    Enable-Rdp; $summary += 'RDP: 已启用（仅 Tailscale 网段可入站，3389）'
  }
  if ($WinRm) {
    Enable-WinRm; $summary += "WinRM: 已启用（5985，仅 Tailscale 网段；LocalAccountTokenFilterPolicy=1）"
  }
  if ($Ssh) {
    Enable-Ssh; $summary += 'SSH: sshd 就绪（22，仅 Tailscale 网段）'
  }
  if ($Agent) {
    Install-Agent; $summary += "Agent: SYSTEM 执行端已安装（$AgentPort，令牌在 $DataRoot\token.txt）"
  }
  if ($TailscaleUnattended) {
    Set-TailscaleUnattended; $summary += 'Tailscale: unattended 模式已设置'
  }
  Save-State
  Emit 'done' 'ok' ($summary -join '; ')
} catch {
  Emit 'error' 'fail' $_.Exception.Message
  Write-Host "`n[失败] $($_.Exception.Message)" -ForegroundColor Red
  throw
}

Write-Host "`n================ 完成 ================" -ForegroundColor Green
$summary | ForEach-Object { Write-Host "  - $_" }
Write-Host "`n状态文件: $StatePath"
Write-Host "回滚命令: pwsh -File .\uninstall-remote-access.ps1`n"
if ($StatusFile) { Set-Content -Path "$StatusFile.done" -Value 'ok' -Encoding utf8 }
