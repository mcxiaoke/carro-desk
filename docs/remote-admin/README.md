# 开发机远程管理通道（MCPC）

用于「人不在电脑边，但仍要以管理员/SYSTEM 权限执行命令（启动服务、改配置）」的场景，不依赖 UU远程 / 向日葵那类第三方中转。

主控端：手机 + 另一台电脑都支持。

---

## 1. 现状实测（2026-09-26，本机）

| 项目 | 结果 |
|---|---|
| 系统 | Windows 10 **Pro** 22H2 / 19045（支持作 RDP 主机） |
| 远程桌面 | **未开**：`fDenyTSConnections=1`，3389 无监听 |
| WinRM | 服务 Stopped / Manual，未启用 |
| OpenSSH `sshd` | **已在运行**，Automatic，监听 `0.0.0.0:22`，默认 shell = PowerShell |
| SSH 公钥 | `~/.ssh/authorized_keys` 有旧密钥但**对管理员账户无效**（sshd 默认对 administrators 组走 `C:\ProgramData\ssh\administrators_authorized_keys`，该文件不存在） |
| UAC | `ConsentPromptBehaviorAdmin=5` + `PromptOnSecureDesktop=1` → 提示在安全桌面，脚本无法代点 |
| 账户 | `MCPC\mcxiaoke`，属 Administrators 组，当前进程为过滤令牌（`deny only`） |
| Tailscale | 在线，`tsmcpc` = `100.99.99.105`，服务 Automatic |

结论：**四个通道都缺一次性管理员初始化**，初始化后长期不再需要有人点 UAC。

---

## 2. 四条通道对比

| 通道 | 用起来是什么 | 权限 | 主控端 | 端口 |
|---|---|---|---|---|
| **RDP** | 完整图形桌面，UAC 提示就在会话里，可远程点「是」 | 交互式（管理员组本人） | 手机 RD 客户端 / 电脑 mstsc | 3389 |
| **WinRM** | `Invoke-Command` 纯命令行 | **完整管理员令牌，全程无 UAC** | 电脑（手机端不便） | 5985 |
| **SSH** | 交互式 shell（已在跑） | 会话权限需实测，提权交给 Agent | 手机 SSH App / 电脑 | 22 |
| **Agent** | 一个 HTTP 接口 + 手机可用网页 | **SYSTEM（最高）**，永不涉及 UAC | 手机浏览器 / 电脑 `rctl.ps1` | 8443 |

为什么没有用 Tailscale SSH：官方限制它作为服务端**只支持 Linux 与 macOS 开源版**，Windows 不在列。

---

## 3. 文件说明

| 文件 | 作用 |
|---|---|
| `setup-remote-access.ps1` | 一键/分通道初始化，自动提权、幂等，记录原始状态以便回滚 |
| `remote-admin-agent.ps1` | 以 SYSTEM 运行的 HTTP 执行端（由计划任务拉起，含手机网页控制台） |
| `rctl.ps1` | 电脑端命令行客户端，调 Agent 执行命令 |
| `grant-service-control.ps1` | 把指定服务（可批量，逗号分隔）的 启动/停止 权限授予当前用户（服务 DACL 追加 ACE，自动备份、幂等、写回前 .NET 预检）；`-DryRun` 免管理员预览 |
| `uninstall-remote-access.ps1` | 按 `state.json` 回滚全部改动 |
| `README.md` | 本文档 |

运行期数据目录：`C:\ProgramData\RemoteAdmin\`（`state.json`、`token.txt`、`logs\`）。

---

## 4. 你回来后怎么执行

### 4.1 准备

1. 确认 `mcxiaoke` 账户**有密码** —— 空白密码账户无法 RDP 登录。
2. 准备客户端 SSH 公钥（私钥永远留在客户端）：
   - 手机：Termius / JuiceSSH 里生成密钥，导出公钥文本
   - 电脑：`ssh-keygen -t ed25519 -C "phone"`，取 `id_ed25519.pub`
   - 存成文件，例如 `docs/remote-admin/phone.pub`

### 4.2 一键全开（推荐）

在**管理员**终端里执行（脚本会自己再提权一次，弹 1 个 UAC，点「是」）：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 `
  -All -TailscaleUnattended -SshPublicKeyPath .\phone.pub
```

结束时屏幕会打印 **Agent URL 与 Token**，请存到手机/密码管理器：

```
URL   : http://100.99.99.105:8443/
Token : <32 字节随机串>
```

### 4.3 只开其中几条

```powershell
pwsh -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 -Rdp
pwsh -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 -WinRm
pwsh -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 -Ssh -SshPublicKeyPath .\phone.pub
pwsh -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 -Agent
pwsh -ExecutionPolicy Bypass -File .\setup-remote-access.ps1 -TailscaleUnattended
```

`-Ssh -DisableSshPassword` 会关掉 SSH 密码登录（必须先提供可用公钥，脚本会挡住把自己锁死的操作）。

### 4.4 验证清单

```powershell
# RDP
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections   # 期望 0
Get-NetTCPConnection -LocalPort 3389 -State Listen                                                  # 期望有结果

# WinRM
Get-Service WinRM | Select-Object Status,StartType                                                  # Running / Automatic
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy  # 期望 1

# SSH
Get-Service sshd | Select-Object Status,StartType                                                    # Running / Automatic
Get-Content C:\ProgramData\ssh\administrators_authorized_keys                                       # 应包含你的公钥

# Agent
Get-ScheduledTask RemoteAdminAgent | Select-Object TaskName,State                                   # Ready / Running
Test-NetConnection 127.0.0.1 -Port 8443 -InformationLevel Quiet                                     # 期望 True
Get-Content C:\ProgramData\RemoteAdmin\logs\agent-error.log -Tail 20                                # 应为空或只有启动信息
```

跑完把结果发我，我逐条核对；手机端连通性我可以再帮你从日志侧确认。

---

## 5. 手机端配置

**远程桌面（RDP）**：装 Microsoft Remote Desktop → Add PC → `100.99.99.105` → 账户 `MCPC\mcxiaoke` + 密码。登录后就是完整桌面，UAC 提示直接点「是」。

**SSH**：Termius / JuiceSSH → Host `100.99.99.105`，Port 22，User `mcxiaoke`，Key 选刚生成的私钥。

**Agent 网页（最方便，建议收藏）**：浏览器打开 `http://100.99.99.105:8443/` → 粘贴 Token → 命令框里执行，内置快捷按钮（系统信息 / 运行中的服务 / 启动 UU远程服务 / 监听端口 / Tailscale 状态）。

前提：手机 Tailscale 已用同一账号登录并连接（这一点现在已具备）。

---

## 6. 电脑端

```powershell
# 把令牌存一次，后续免输入
'<你的 Token>' | Set-Content $env:USERPROFILE\.remote-admin-token -NoNewline

# 以 SYSTEM 执行
pwsh -File .\rctl.ps1 'Get-Service GameViewerService | Format-List Name,Status,StartType'
pwsh -File .\rctl.ps1 'Set-Service GameViewerService -StartupType Automatic; Start-Service GameViewerService'
```

WinRM 客户端侧（**在发起连接的电脑上**执行一次）：

```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value '100.99.99.105' -Concatenate -Force
Invoke-Command -ComputerName 100.99.99.105 -Credential MCPC\mcxiaoke -ScriptBlock {
  Get-Service TermService
}
```

---

## 7. 安全设计要点

- **只允许 Tailscale 网段入站**：所有新增防火墙规则都限制 `RemoteAddress = 100.64.0.0/10`，不做公网端口映射，不做 LAN 暴露。
- **Agent 双重门禁**：先校验来源 IP 必须是 Tailscale 网段或回环，再校验令牌（定长比较）。
- **令牌 = 这台机的最高权限**：泄漏等于 SYSTEM 被接管。Token 文件 ACL 只给 SYSTEM + Administrators；`C:\ProgramData\RemoteAdmin\logs\` 会记录每条命令原文，别在命令里写密码。
- **HTTP 明文但跑在 WireGuard 内**：Tailscale 自身加密，因此未再加 TLS。若你不接受这一点，就只用 RDP + WinRM 两条通道（`-Rdp -WinRm`），不装 Agent。
- **WinRM 收紧**：`Enable-PSRemoting` 默认放开的 `WINRM-HTTP-In-TCP-PUBLIC`（任意来源）已被禁用，改为仅 Tailscale 规则。
- **SSH 密钥文件权限**：用 SID 收紧 ACL（`S-1-5-18` / `S-1-5-32-544`），避免中文系统账户名差异导致 sshd 拒收。
- 建议再做的两件事：给 tailnet 配 ACL 只允许你自己的设备、给设备开启 key expiry。

---

## 8. 回滚

```powershell
pwsh -ExecutionPolicy Bypass -File .\uninstall-remote-access.ps1            # 全部回滚
pwsh -ExecutionPolicy Bypass -File .\uninstall-remote-access.ps1 -Agent     # 只卸 Agent
```

会恢复：RDP 开关与服务启动类型、`LocalAccountTokenFilterPolicy`、`sshd_config`、`administrators_authorized_keys`、删除计划任务与自建防火墙规则、删除 URL 保留。`state.json` 会保留（不含敏感信息）。

---

## 9. FAQ

**RDP 连不上？** 依次查：`fDenyTSConnections=0`？3389 有监听？账户有密码？Tailscale 里对方设备 online？防火墙规则是否只剩 Tailscale 网段。

**Agent 起不来？** 看 `C:\ProgramData\RemoteAdmin\logs\agent-error.log`；确认 `netsh http show urlacl | findstr 8443` 有记录；`Get-ScheduledTask RemoteAdminAgent` 状态应为 Running。

**SSH 会话是不是提权状态？** 连上后执行 `whoami /groups | findstr S-1-5-32-544`：出现 `Group used for deny only` 就是过滤令牌（非提权）。要提权就直接用 Agent，比 gsudo 可靠（gsudo 默认仍要弹 UAC，而你不在场）。

**为什么不用 gsudo？** 它依赖 UAC 弹窗或凭据缓存，无人值守时不可用。

**注销后 Tailscale 会掉吗？** 默认会，`-TailscaleUnattended` 已处理。

**这套东西属于项目代码吗？** 不属于。放在 `docs/remote-admin/` 只是为了留档，不影响 CarroDesk 构建。

---

## 10. 参考

- [PowerShell Remoting FAQ / LocalAccountTokenFilterPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_remote_troubleshooting)
- [OpenSSH Server for Windows 安装与密钥](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)
- [Tailscale SSH 平台限制](https://tailscale.com/docs/features/tailscale-ssh)
- [Tailscale Windows Unattended 模式](https://tailscale.com/docs/how-to/run-unattended)
