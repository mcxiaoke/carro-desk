#Requires -Version 5.1
<#
.SYNOPSIS
  RemoteAdminAgent：以 SYSTEM 身份运行的极小 HTTP 执行端。

.DESCRIPTION
  * 仅接受来自 Tailscale 网段（100.64.0.0/10）与回环地址的请求，其余一律 403。
  * 除 GET / 与 GET /api/ping 外，全部接口需要令牌（Header: X-Auth-Token）。
  * 所有请求与命令原文记录到 C:\ProgramData\RemoteAdmin\logs\。
  * 由“计划任务 RemoteAdminAgent”在开机时以 SYSTEM 启动，不要手动常驻。

  接口：
    GET  /                → 手机/电脑浏览器可用的简易控制台
    GET  /api/ping        → 免鉴权存活探测
    GET  /api/info        → 主机信息
    POST /api/exec        → {"cmd":"...","timeoutSec":120} 执行命令并返回 {exitCode,stdout,stderr}
#>
[CmdletBinding()]
param([int]$Port = 8443)

$ErrorActionPreference = 'Stop'
$DataRoot   = 'C:\ProgramData\RemoteAdmin'
$TokenFile  = Join-Path $DataRoot 'token.txt'
$LogDir     = Join-Path $DataRoot 'logs'
$ErrLog     = Join-Path $LogDir 'agent-error.log'
$MaxOutBytes = 200 * 1024
$MaxTimeoutSec = 1800

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-ErrLog([string]$m) {
  try { Add-Content -Path $ErrLog -Value ("{0} {1}" -f (Get-Date).ToString('s'), $m) -Encoding utf8 } catch { }
}
function Write-Audit([string]$m) {
  try {
    $f = Join-Path $LogDir ("agent-{0}.log" -f (Get-Date).ToString('yyyyMMdd'))
    Add-Content -Path $f -Value ("{0} {1}" -f (Get-Date).ToString('s'), $m) -Encoding utf8
  } catch { }
}

if (-not (Test-Path $TokenFile)) { Write-ErrLog 'token.txt 不存在，拒绝启动'; exit 1 }
$script:Token = (Get-Content $TokenFile -Raw -Encoding ascii).Trim()
if (-not $script:Token) { Write-ErrLog 'token.txt 为空，拒绝启动'; exit 1 }

function Test-TailnetAddress($ip) {
  if ($null -eq $ip) { return $false }
  if ([System.Net.IPAddress]::IsLoopback($ip)) { return $true }
  # Tailscale 客户端可能以 IPv4-mapped IPv6 形式接入，先归一化
  if ($ip.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6 -and $ip.IsIPv4MappedToIPv6) {
    $ip = $ip.MapToIPv4()
  }
  if ($ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { return $false }
  $b = $ip.GetAddressBytes()
  return ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127)
}

# 定长比较，避免计时侧信道
function Test-Token([string]$given) {
  if ([string]::IsNullOrEmpty($given)) { return $false }
  $a = [Text.Encoding]::UTF8.GetBytes($script:Token)
  $b = [Text.Encoding]::UTF8.GetBytes($given)
  if ($a.Length -ne $b.Length) { return $false }
  $diff = 0
  for ($i = 0; $i -lt $a.Length; $i++) { $diff = $diff -bor ($a[$i] -bxor $b[$i]) }
  return ($diff -eq 0)
}

function Get-RequestToken($ctx) {
  $t = $ctx.Request.Headers['X-Auth-Token']
  if ($t) { return $t }
  $auth = $ctx.Request.Headers['Authorization']
  if ($auth -and $auth.StartsWith('Bearer ')) { return $auth.Substring(7) }
  return $ctx.Request.QueryString['token']
}

function Send-Json($ctx, $obj, [int]$status = 200) {
  $json  = $obj | ConvertTo-Json -Depth 6 -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  $ctx.Response.StatusCode = $status
  $ctx.Response.ContentType = 'application/json; charset=utf-8'
  $ctx.Response.ContentLength64 = $bytes.Length
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $ctx.Response.Close()
}

function Send-Html($ctx, [string]$html) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($html)
  $ctx.Response.StatusCode = 200
  $ctx.Response.ContentType = 'text/html; charset=utf-8'
  $ctx.Response.ContentLength64 = $bytes.Length
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $ctx.Response.Close()
}

function Invoke-RemoteCommand([string]$cmd, [int]$timeoutSec) {
  if ($timeoutSec -lt 1) { $timeoutSec = 120 }
  if ($timeoutSec -gt $MaxTimeoutSec) { $timeoutSec = $MaxTimeoutSec }

  # 用 EncodedCommand 传递，彻底避开引号转义问题；强制子进程 UTF-8 输出
  $full = '$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8;' + $cmd
  $enc  = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($full))

  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName  = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $enc"
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  $psi.StandardErrorEncoding  = [Text.Encoding]::UTF8
  $psi.WorkingDirectory = $env:SystemRoot

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $p  = [System.Diagnostics.Process]::Start($psi)
  $outTask = $p.StandardOutput.ReadToEndAsync()
  $errTask = $p.StandardError.ReadToEndAsync()

  $timedOut = $false
  if (-not $p.WaitForExit($timeoutSec * 1000)) {
    $timedOut = $true
    try { $p.Kill() } catch { }
    $p.WaitForExit(5000) | Out-Null
  }
  $sw.Stop()

  $out = ''; $err = ''
  try { $out = $outTask.Result } catch { }
  try { $err = $errTask.Result } catch { }

  $truncated = $false
  if ($out.Length -gt $MaxOutBytes) { $out = $out.Substring(0, $MaxOutBytes); $truncated = $true }
  if ($err.Length -gt $MaxOutBytes) { $err = $err.Substring(0, $MaxOutBytes); $truncated = $true }

  return [ordered]@{
    exitCode  = if ($timedOut) { -1 } else { $p.ExitCode }
    stdout    = $out
    stderr    = $err
    timedOut  = $timedOut
    truncated = $truncated
    durationMs = [int]$sw.ElapsedMilliseconds
  }
}

$ConsoleHtml = @'
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>RemoteAdmin Console</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; padding:16px; font:14px/1.5 -apple-system,Segoe UI,Roboto,sans-serif; background:#14171c; color:#e6e6e6; }
  h1 { font-size:16px; margin:0 0 4px; }
  .sub { color:#8b949e; font-size:12px; margin-bottom:14px; }
  input, textarea, button { font:inherit; box-sizing:border-box; width:100%; border-radius:8px; border:1px solid #30363d; background:#0d1117; color:#e6e6e6; padding:10px; }
  textarea { min-height:96px; resize:vertical; }
  label { display:block; font-size:12px; color:#8b949e; margin:12px 0 4px; }
  button { background:#238636; border-color:#2ea043; color:#fff; font-weight:600; margin-top:12px; cursor:pointer; }
  button.q { background:#21262d; border-color:#30363d; font-weight:400; margin:9px 6px 0 0; width:auto; padding:6px 10px; font-size:12px; }
  .row { display:flex; flex-wrap:wrap; gap:6px; margin-top:10px; }
  pre { background:#0d1117; border:1px solid #30363d; border-radius:8px; padding:10px; overflow:auto; max-height:52vh; white-space:pre-wrap; word-break:break-all; }
  .meta { font-size:12px; color:#8b949e; margin:8px 0; }
  .err { color:#f85149; }
  .ok { color:#3fb950; }
</style>
</head>
<body>
<h1>RemoteAdmin Console</h1>
<div class="sub">命令以 SYSTEM 身份执行 · 仅 Tailscale 网段可访问 · 所有操作写审计日志</div>

<label>访问令牌</label>
<input id="tok" type="password" placeholder="粘贴 token.txt 内容" autocomplete="off">
<label>命令（PowerShell）</label>
<textarea id="cmd" placeholder='例如: Get-Service GameViewerService'></textarea>
<div class="row" id="quick"></div>
<button id="run">执行</button>
<div class="meta" id="meta"></div>
<pre id="out">就绪。</pre>

<script>
const $ = id => document.getElementById(id);
$('tok').value = localStorage.getItem('ra_token') || '';
$('tok').addEventListener('change', () => localStorage.setItem('ra_token', $('tok').value));

const quick = [
  ['系统信息', 'Get-ComputerInfo | Select-Object WindowsProductName,OsVersion,CsName | Format-List'],
  ['运行中的服务', 'Get-Service | Where-Object Status -eq "Running" | Select-Object Name,DisplayName | Format-Table -AutoSize'],
  ['启动 UU远程服务', 'Set-Service GameViewerService -StartupType Automatic; Start-Service GameViewerService; Get-Service GameViewerService | Format-List Name,Status,StartType'],
  ['监听端口', 'Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess | Sort-Object LocalPort | Format-Table -AutoSize'],
  ['Tailscale 状态', '& "C:\\Program Files\\Tailscale\\tailscale.exe" status']
];
quick.forEach(([name, c]) => {
  const b = document.createElement('button');
  b.className = 'q'; b.textContent = name;
  b.onclick = () => { $('cmd').value = c; };
  $('quick').appendChild(b);
});

$('run').onclick = async () => {
  const cmd = $('cmd').value.trim();
  if (!cmd) return;
  const token = $('tok').value.trim();
  localStorage.setItem('ra_token', token);
  $('meta').textContent = '执行中...';
  $('out').textContent = '';
  try {
    const r = await fetch('/api/exec', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Auth-Token': token },
      body: JSON.stringify({ cmd, timeoutSec: 300 })
    });
    const j = await r.json();
    if (!r.ok) { $('meta').innerHTML = '<span class="err">HTTP ' + r.status + '</span>'; $('out').textContent = JSON.stringify(j, null, 2); return; }
    const cls = j.exitCode === 0 ? 'ok' : 'err';
    $('meta').innerHTML = '<span class="' + cls + '">exitCode=' + j.exitCode + '</span> · ' + j.durationMs + 'ms' + (j.timedOut ? ' · 超时被杀' : '') + (j.truncated ? ' · 输出被截断' : '');
    $('out').textContent = (j.stdout || '') + (j.stderr ? '\n--- stderr ---\n' + j.stderr : '');
  } catch (e) {
    $('meta').innerHTML = '<span class="err">请求失败</span>';
    $('out').textContent = String(e);
  }
};
</script>
</body>
</html>
'@

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")
try {
  $listener.Start()
} catch {
  Write-ErrLog "HttpListener.Start 失败: $($_.Exception.Message)"
  exit 1
}
Write-Audit "agent started, port=$Port, pid=$PID"

while ($listener.IsListening) {
  $ctx = $null
  try {
    $ctx = $listener.GetContext()
  } catch {
    Write-ErrLog "GetContext 失败: $($_.Exception.Message)"
    Start-Sleep -Seconds 2
    continue
  }

  $remote = $ctx.Request.RemoteEndPoint.Address
  $path   = $ctx.Request.Url.AbsolutePath
  try {
    if (-not (Test-TailnetAddress $remote)) {
      Write-Audit "DENY(no-tailnet) $remote $path"
      Send-Json $ctx @{ ok = $false; error = 'forbidden: not a tailnet address' } 403
      continue
    }

    if ($path -eq '/' -and $ctx.Request.HttpMethod -eq 'GET') {
      Send-Html $ctx $ConsoleHtml
      continue
    }
    if ($path -eq '/api/ping') {
      Send-Json $ctx @{ ok = $true; host = $env:COMPUTERNAME; port = $Port }
      continue
    }

    if (-not (Test-Token (Get-RequestToken $ctx))) {
      Write-Audit "DENY(bad-token) $remote $path"
      Send-Json $ctx @{ ok = $false; error = 'unauthorized' } 401
      continue
    }

    switch ("$($ctx.Request.HttpMethod) $path") {
      'GET /api/info' {
        Send-Json $ctx @{
          ok = $true; host = $env:COMPUTERNAME; user = (whoami); os = (Get-CimInstance Win32_OperatingSystem).Caption
          boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; agentPid = $PID
        }
      }
      'POST /api/exec' {
        $reader = New-Object System.IO.StreamReader($ctx.Request.InputStream, [Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $reader.Dispose()
        $cmd = ''
        $timeoutSec = 120
        if ($body) {
          try {
            $req = $body | ConvertFrom-Json
            $cmd = [string]$req.cmd
            if ($null -ne $req.timeoutSec) { $timeoutSec = [int]$req.timeoutSec }
          } catch { }
        }
        if ([string]::IsNullOrWhiteSpace($cmd)) {
          Send-Json $ctx @{ ok = $false; error = 'empty cmd' } 400
          continue
        }
        Write-Audit "EXEC $remote :: $cmd"
        $result = Invoke-RemoteCommand $cmd $timeoutSec
        Write-Audit ("DONE exit={0} ms={1} timeout={2}" -f $result.exitCode, $result.durationMs, $result.timedOut)
        Send-Json $ctx $result
      }
      default {
        Send-Json $ctx @{ ok = $false; error = 'not found' } 404
      }
    }
  } catch {
    Write-ErrLog "处理 $path 出错: $($_.Exception.Message)"
    try { Send-Json $ctx @{ ok = $false; error = $_.Exception.Message } 500 } catch { }
  } finally {
    try { $ctx.Response.Close() } catch { }
  }
}
