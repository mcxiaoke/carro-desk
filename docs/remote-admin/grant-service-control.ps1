#Requires -RunAsAdministrator
<#
.SYNOPSIS
  把指定 Windows 服务的 启动/停止 权限授予当前用户（IPC 设计 §11.2 路径 A 的一次性授权）。

.DESCRIPTION
  原理：不改 CarroDesk 的权限，而是在服务对象的 DACL 上为当前用户 SID 追加一条
  ACE（RP=启动 / WP=停止 / DT=暂停继续 / LC / CC / CR / RC 查询控制）。
  之后 CarroDesk（含微信远程路径）以普通权限即可 Start-Service / Stop-Service。

  ⚠️ 只对「固定几个服务」使用本脚本；每改一个服务都要先备份原 DACL（脚本自动做）。

.PARAMETER ServiceName
  目标 Windows 服务名（短名，如 GameViewerService，不是显示名）。

.PARAMETER AccountSid
  要授权的账户 SID。缺省 = 当前用户 SID（本机 CarroDesk 运行账户）。

.EXAMPLE
  .\grant-service-control.ps1 -ServiceName GameViewerService

.NOTES
  回滚：备份文件在 C:\ProgramData\CarroDesk\service-dacl-backup\<服务名>.sddl.txt，
  用其中的原始 SDDL 执行 sc.exe sdset <服务名> "<原始串>" 即可恢复。
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ServiceName,
    [string]$AccountSid
)

$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Error "服务不存在: $ServiceName（请用服务短名，如 GameViewerService）"
    exit 1
}

if (-not $AccountSid) {
    $AccountSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
}

# ---------- 1. 读取并备份当前 DACL ----------
$sdshow = & sc.exe sdshow $ServiceName 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "sc.exe sdshow 失败: $($sdshow -join ' ')"
    exit 1
}
# sdshow 输出为两行：说明行 + SDDL 串（D:(...)S:(...)）
$rawSddl = ($sdshow | Where-Object { $_ -match '^(D:|G:|S:)' }) -join ''
if (-not $rawSddl) {
    Write-Error "无法解析 sdshow 输出：`n$($sdshow -join "`n")"
    exit 1
}

$backupDir = 'C:\ProgramData\CarroDesk\service-dacl-backup'
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$backupFile = Join-Path $backupDir "$ServiceName.sddl.txt"
$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
"# $timestamp  service=$ServiceName  sid=$AccountSid" | Set-Content -Path $backupFile -Encoding UTF8
$rawSddl | Add-Content -Path $backupFile -Encoding UTF8
Write-Host "[1/4] 原 DACL 已备份到 $backupFile"

# ---------- 2. 幂等检查 ----------
if ($rawSddl -match [regex]::Escape($AccountSid)) {
    Write-Host "[2/4] 该 SID 已在 DACL 中，跳过（无需重复授权）。"
    exit 0
}

# ---------- 3. 在 D: 段末尾追加 ACE（只增不改，S:/G: 段原样保留） ----------
# 权限字母：RP=启动 WP=停止 DT=暂停/继续 LC=查状态 CC=查配置 CR=用户自定义控制 RC=读控制
#
# ⚠️ SDDL 的 D: 段由多个独立括号组组成：D:(ACE1)(ACE2)(ACE3)，没有外层括号。
# 插入点必须是「整个 D: 段的末尾」（下一个 S:/G: 段之前，或串尾），
# 而不是 D: 后第一个右括号——插到第一条 ACE 内部会产生嵌套括号的非法 ACL
# （ConvertStringSecurityDescriptorToSecurityDescriptor 报错 1336）。
$ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$AccountSid)"
$daclStart = $rawSddl.IndexOf('D:')
if ($daclStart -lt 0) {
    # 极罕见：无 D: 段，整体重建
    $newSddl = "D:$ace" + $rawSddl
} else {
    # D: 段结束位置 = 下一个 G:/S: 段标记之前（SDDL 中唯一的冒号就是段标记）
    $sectionEnd = $rawSddl.Length
    foreach ($marker in @('G:', 'S:')) {
        $idx = $rawSddl.IndexOf($marker, $daclStart + 2)
        if ($idx -ge 0 -and $idx -lt $sectionEnd) { $sectionEnd = $idx }
    }
    $newSddl = $rawSddl.Substring(0, $sectionEnd) + $ace + $rawSddl.Substring($sectionEnd)
}

# 预检：交给 .NET 解析一次，非法 SDDL 在这里就报错，不去碰服务
try {
    New-Object System.Security.AccessControl.RawSecurityDescriptor -ArgumentList $newSddl | Out-Null
} catch {
    Write-Error "构造的 SDDL 无法通过 .NET 解析（未对服务做任何修改）: $newSddl`n$($_.Exception.Message)"
    exit 1
}
Write-Host "[3/4] 新 SDDL: $newSddl"

$sdset = & sc.exe sdset $ServiceName "$newSddl" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "sc.exe sdset 失败（原 DACL 未被破坏，可用备份恢复）: $($sdset -join ' ')"
    exit 1
}
Write-Host "[3/4] 已追加 ACE: $ace"

# ---------- 4. 验证 ----------
$verify = (& sc.exe sdshow $ServiceName 2>&1 | Where-Object { $_ -match '^(D:|G:|S:)' }) -join ''
if ($verify -match [regex]::Escape($AccountSid)) {
    Write-Host "[4/4] 验证通过：$ServiceName 的 DACL 已包含该 SID。"
    Write-Host ""
    Write-Host "请用一个【非提权】终端验证（不要在管理员窗口里测）："
    Write-Host "  net start $ServiceName"
    Write-Host "  net stop  $ServiceName"
} else {
    Write-Warning "sdset 已返回成功但复查未见 ACE，请人工核对：sc.exe sdshow $ServiceName"
}
