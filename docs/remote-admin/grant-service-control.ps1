<#
.SYNOPSIS
  把指定 Windows 服务的 启动/停止 权限授予某个账户（IPC 设计 §11.2 路径 A 的一次性授权）。
  支持一次传入多个服务，逐个独立授权、独立备份。

.DESCRIPTION
  原理：不改 CarroDesk 的权限，而是在服务对象的 DACL 上为目标 SID 追加一条
  ACE（RP=启动 / WP=停止 / DT=暂停继续 / LC / CC / CR / RC 查询控制）。
  之后 CarroDesk（含微信远程路径）以普通权限即可 Start-Service / Stop-Service。

  为什么粒度是"服务对象"而不是"授权 sc/net 命令"：sc/net 只是客户端，
  权限校验发生在 SCM 对目标服务对象的访问检查上，与用哪个工具无关；
  Windows 也不提供"某用户可启动任意服务"的合法授权点。
  需要任意服务的全权控制时，用 docs/remote-admin 的 SYSTEM Agent 通道（人打字）。

.PARAMETER ServiceName
  一个或多个 Windows 服务短名（逗号分隔，如 -ServiceName A,B,C）。

.PARAMETER AccountSid
  要授权的账户 SID。缺省 = 当前用户 SID（本机 CarroDesk 运行账户）。

.PARAMETER DryRun
  只解析、构造并预检新 SDDL，不写回服务（供预览；无需管理员）。

.EXAMPLE
  .\grant-service-control.ps1 -ServiceName GameViewerService
  .\grant-service-control.ps1 -ServiceName GameViewerService,Spooler -DryRun

.NOTES
  回滚：每服务备份在 C:\ProgramData\CarroDesk\service-dacl-backup\<服务名>.sddl.txt，
  用其中的原始 SDDL 执行 sc.exe sdset <服务名> "<原始串>" 即可恢复。
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$ServiceName,
    [string]$AccountSid,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# 写回需要管理员；-DryRun 只读可预览，无需提权
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DryRun) {
    Write-Error "修改服务 DACL 需要管理员终端；只看效果请加 -DryRun。"
    exit 1
}

if (-not $AccountSid) {
    $AccountSid = $identity.User.Value
}
# -File 模式下 `-ServiceName A,B,C` 会作为单个字符串传入，这里统一拆分 + 去重
$ServiceName = @($ServiceName |
    ForEach-Object { $_ -split '[,;\s]+' } |
    Where-Object { $_ } |
    Select-Object -Unique)
# 权限字母：RP=启动 WP=停止 DT=暂停/继续 LC=查状态 CC=查配置 CR=用户自定义控制 RC=读控制
$ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$AccountSid)"

function Get-ServiceSddl([string]$name) {
    $output = & sc.exe sdshow $name 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    return (($output | Where-Object { $_ -match '^(D:|G:|S:)' }) -join '')
}

function Test-SddlValid([string]$sddl) {
    # 预检：交给 .NET 解析，非法 SDDL 在这里就报错，不去碰服务
    try {
        New-Object System.Security.AccessControl.RawSecurityDescriptor -ArgumentList $sddl | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Grant-ServiceControl([string]$name) {
    # ---------- 1. 服务存在性 ----------
    if (-not (Get-Service -Name $name -ErrorAction SilentlyContinue)) {
        Write-Host "[$name] 跳过：服务不存在（请用服务短名，如 GameViewerService）" -ForegroundColor Yellow
        return 'NotFound'
    }

    # ---------- 2. 读取并备份当前 DACL ----------
    $rawSddl = Get-ServiceSddl $name
    if (-not $rawSddl) {
        Write-Host "[$name] 失败：无法读取服务 DACL" -ForegroundColor Red
        return 'Failed'
    }

    $backupDir = 'C:\ProgramData\CarroDesk\service-dacl-backup'
    $backupFile = Join-Path $backupDir "$name.sddl.txt"
    if (-not $DryRun) {
        # 真实写回前必须落备份（写失败则中止该服务——备份是误操作时的唯一恢复手段）；
        # DryRun 只是预览，不产生文件，避免与既有提权运行的备份文件冲突
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        try {
            "# $timestamp  service=$name  sid=$AccountSid" | Set-Content -Path $backupFile -Encoding UTF8
            $rawSddl | Add-Content -Path $backupFile -Encoding UTF8
        } catch {
            Write-Host "[$name] 失败：备份写入失败（$backupFile），未对服务做修改" -ForegroundColor Red
            return 'Failed'
        }
    }

    # ---------- 3. 幂等检查 ----------
    if ($rawSddl -match [regex]::Escape($AccountSid)) {
        Write-Host "[$name] 跳过：该 SID 已在 DACL 中。"
        return 'AlreadyGranted'
    }

    # ---------- 4. 构造新 SDDL ----------
    # ⚠️ SDDL 的 D: 段由多个独立括号组组成：D:(ACE1)(ACE2)(ACE3)，没有外层括号。
    # 插入点必须是「整个 D: 段的末尾」（下一个 S:/G: 段之前，或串尾）——
    # 插进第一条 ACE 内部会产生嵌套括号的非法 ACL（ConvertStringSecurityDescriptor 报错 1336）。
    $daclStart = $rawSddl.IndexOf('D:')
    if ($daclStart -lt 0) {
        $newSddl = "D:$ace" + $rawSddl
    } else {
        $sectionEnd = $rawSddl.Length
        foreach ($marker in @('G:', 'S:')) {
            $idx = $rawSddl.IndexOf($marker, $daclStart + 2)
            if ($idx -ge 0 -and $idx -lt $sectionEnd) { $sectionEnd = $idx }
        }
        $newSddl = $rawSddl.Substring(0, $sectionEnd) + $ace + $rawSddl.Substring($sectionEnd)
    }

    if (-not (Test-SddlValid $newSddl)) {
        Write-Host "[$name] 失败：构造的 SDDL 无法通过 .NET 解析（服务未被修改）: $newSddl" -ForegroundColor Red
        return 'Failed'
    }

    # ---------- 5. 写回 ----------
    if ($DryRun) {
        Write-Host "[$name] DryRun：未写回。"
        Write-Host "  原 SDDL: $rawSddl"
        Write-Host "  新 SDDL: $newSddl"
        return 'DryRun'
    }

    $sdset = & sc.exe sdset $name "$newSddl" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$name] 失败：sc.exe sdset 出错（原 DACL 未被破坏，可用备份恢复）: $($sdset -join ' ')" -ForegroundColor Red
        return 'Failed'
    }

    # ---------- 6. 验证 ----------
    $verify = Get-ServiceSddl $name
    if ($verify -match [regex]::Escape($AccountSid)) {
        Write-Host "[$name] 授权成功。备份: $backupFile"
        return 'Granted'
    }
    Write-Host "[$name] 警告：sdset 返回成功但复查未见 ACE，请人工核对：sc.exe sdshow $name" -ForegroundColor Yellow
    return 'Failed'
}

# ---------- 主流程：逐个服务授权 ----------
$summary = @{}
foreach ($name in $ServiceName) {
    $summary[$name] = Grant-ServiceControl $name
}

Write-Host ""
Write-Host "===== 结果汇总（SID: $AccountSid）====="
foreach ($entry in $summary.GetEnumerator()) {
    Write-Host ("{0,-30} {1}" -f $entry.Key, $entry.Value)
}
if (-not $DryRun) {
    Write-Host ""
    Write-Host "请用一个【非提权】终端验证（不要在管理员窗口里测）："
    foreach ($name in $ServiceName) {
        Write-Host "  net start $name ; net stop $name"
    }
}

if ($summary.Values.Contains('Failed')) { exit 1 } else { exit 0 }
