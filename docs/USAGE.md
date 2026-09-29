# CarroDesk 使用说明

> 宿主：`CarroDesk.exe` 常驻托盘。所有模块配置集中在 `config.json`，任务在 `tasks.json`，托盘右键 **重载配置** 即时生效。
> CLI/助手接入方式见 §10；更详细的架构见 [DESIGN.md](DESIGN.md)。

## 1. 部署与数据目录

| 模式 | 判定 | 数据目录 |
|---|---|---|
| 便携（推荐） | exe 同目录存在 `portable.ini` | exe 同目录 `app_data\` |
| 漫游 | 默认 | `%AppData%\CarroDesk\` |
| 环境变量覆盖 | 设置 `CARRODESK_DATA_DIR` | 指定目录 |

数据目录内容：`config.json`（主配置）、`tasks.json`（任务）、`logs\`（日志）、`scripts\`（任务脚本）。首次启动自动生成示例配置。

升级：便携模式直接覆盖 exe/dll，`app_data\` 不受影响。

## 2. 托盘菜单

```
CarroDesk（标题）
├─ 各模块菜单（按模块注入：锁屏、自动化任务、音频切换、自动静音、
│   显示器配置档、保持唤醒、剪贴板历史…）
├─ 桌面控制面板...        (Win+Alt+C)
├─ 开机自启（勾选）
├─ 配置编辑器...
├─ 打开配置目录...
├─ 重载配置
├─ 语言 (Auto / zh-CN / en-US)
└─ 退出                    ← 需验证应用 PIN
```

## 3. 各模块配置速查

`config.json` 中每个模块一个配置节（`Enabled` 控制开关，热重载生效）。常用键：

### ScreenLock（锁屏）

| 键 | 默认 | 说明 |
|---|---|---|
| `IdleMinutes` | 5 | 空闲多久后锁屏 |
| `Hotkey` | `Ctrl+Alt+L` | 手动锁屏热键 |
| `ShowClock` / `OverlayOpacity` | true / 0.88 | 锁屏界面 |
| `UnlockOnResume` | true | 睡眠唤醒后立即锁定 |
| `ExcludeProcesses` | [] | 排除进程：列表内进程运行时暂停空闲计时（游戏挂机、下载器场景）。支持数组或逗号分隔字符串，大小写不敏感，可含路径 |

### Awake（保持唤醒）

托盘菜单可切换模式；`awake.on/off` IPC 同样可用（见 §10）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Mode` | Passive | Passive / Indefinite（无限期）/ Timed / UntilTime |
| `DefaultDurationMinutes` | 30 | Timed 模式缺省时长 |
| `KeepDisplayOn` | true | 是否同时阻止熄屏 |
| `DisableOnBattery` / `BatteryThreshold` | true / 20 | 电池供电且电量低于阈值时暂停 |
| `ProcessLinkEnabled` / `AutoAwakeProcesses` / `AutoAwakeExitDelaySeconds` | true / [] / 120 | 进程联动：目标进程前台运行自动保持唤醒，退出延迟后恢复 |

> 「机器睡眠导致远程失联」的正解是 Awake 无限期保持（或进程联动盯住远程助手进程），而不是依赖单次调用。

### AudioSwitch / AppAutoMute / MonitorProfile / ClipboardHistory

| 模块 | 功能 | 关键配置 |
|---|---|---|
| AudioSwitch | 热键切换默认音频设备（扬声器/耳机） | `Hotkey`（默认 `Ctrl+\``）、`SpeakerPattern`、`HeadphonePattern`、`ExcludedDevices` |
| AppAutoMute | 目标进程运行时自动静音、退出恢复 | `Mode`（Blacklist/Whitelist）、`TargetApps`、`Hotkey`（手动切换）、`MuteDelayMs/UnmuteDelayMs` |
| MonitorProfile | DDC/CI 按时间表切换显示器亮度/对比度 | `AutoSchedule`、`ActiveProfile`、`Profiles`（Daily/Game/Night…）、6 个切换热键 |
| ClipboardHistory | 剪贴板历史窗口 | `Hotkey`（默认 `Win+Alt+V`）、`MaxItems`、`RetentionDays`、`AutoRecord` |

## 4. 任务系统（tasks.json）

### 4.1 快速开始

1. 托盘 `自动化任务 → 任务编辑器...`
2. `新增`，名称填 `hello`（仅 `a-z0-9_-`），触发器选 `manual`，文件选 `scripts/hello.js`（裸名自动在 `scripts/` 中查找）
3. `保存并重载`，托盘 `自动化任务 → 手动运行 → hello` 即执行，日志在 `logs/task-hello.log`

裸文件名是推荐写法：`hello.js` / `sync.py` / `clean.ps1` / `backup.bat` 直接放 `scripts/`，JSON 中无需绝对路径，跨机器即拷即用。`action.file` 支持 `.exe .bat .cmd .ps1 .vbs .js .py/.pyw`，`.js` 用 PATH 中 `node.exe`，`.py` 用 `python/python3/py -u`，其余按 `.ps1→powershell -ExecutionPolicy Bypass`、`.bat→cmd /c` 包装；默认 `hidden:true` 全程隐藏黑窗口，输出进日志。

### 4.2 触发器

| 类型 | 配置示例 | 说明 |
|---|---|---|
| `startup` | `{"type":"startup","delaySec":10}` | 登录后延迟一次 |
| `interval` | `{"type":"interval","every":"1h30m"}` 或 `{"everySec":60}` | 间隔循环 |
| `daily` | `{"type":"daily","at":"02:30"}` | 每天固定时间，休眠错过 5s 补执行 |
| `cron` | `{"type":"cron","expr":"0 9 * * 1"}` | 5 字段 cron（分 时 日 月 周） |
| `sessionLock` / `sessionUnlock` | `{"type":"sessionLock"}` | 锁屏/解锁时 |
| `idle` | `{"type":"idle","afterMinutes":10}` | 空闲后 |
| `manual` | `{"type":"manual"}` | 托盘手动运行 |
| `hotkey` | `{"type":"hotkey","hotkey":"Ctrl+Alt+Q"}` | 全局热键（重启宿主生效） |
| `watch` | `{"type":"watch","path":"%USERPROFILE%/Downloads","filter":"*.zip","event":"created"}` | 文件监听（防抖 500ms） |

### 4.3 运行选项 options（重点：wait / detach）

```json
{
  "name": "frpc-daemon",
  "enabled": true,
  "trigger": { "type": "startup", "delaySec": 10 },
  "action": { "file": "frpc.exe", "args": "-c frpc.toml" },
  "options": {
    "mode": "detach",
    "killWithHost": true,
    "singleInstance": true,
    "restart": "on-failure",
    "restartDelaySec": 5,
    "restartLimit": 3,
    "stableUptimeSec": 60
  }
}
```

| 键 | 默认 | 适用 | 说明 |
|---|---|---|---|
| `mode` | `wait` | — | `wait`=等待退出（输出进日志）；`detach`=启动即返回，常驻后台 |
| `killWithHost` | true | detach | true：Job Object 随宿主退出一起结束；false：宿主退出后继续运行 |
| `singleInstance` | false | detach | 命名互斥体防重复拉起（同任务只跑一份） |
| `restart` | `none` | detach | `on-failure`=异常退出自动守护重启 |
| `restartDelaySec` | 5 | detach | 重启延迟（1-3600） |
| `restartLimit` | 3 | detach | 连续失败熔断上限（1-100）；熔断后保存任务/手动运行/宿主重启可复位 |
| `stableUptimeSec` | 60 | detach | 稳定运行满此时长即清零连续失败计数（0-86400） |
| `hidden` | true | wait | 隐藏窗口 |
| `timeoutSec` | 0 | wait | 超时强杀（0=不限） |
| `retry` | 0 | wait | 失败重试次数 |
| `allowConcurrent` | false | wait | 允许同一任务并发多实例 |
| `workDir` | "" | — | 工作目录（裸名任务默认 `scripts/`） |
| `notifyOnFailure` | true | — | 失败气泡通知 |

> detach 任务不支持 `timeoutSec/retry/allowConcurrent`（校验直接拒绝）。任务编辑器底部实时显示选中任务的运行状态（`运行中 pid=x`）并提供 **停止任务** 按钮；**测试运行** 对 detach 任务不等待、有 10s 兜底超时和独立停止按钮。

### 4.4 条件 when 与模板

```json
{
  "name": "heavy-sync",
  "trigger": { "type": "interval", "every": "30m" },
  "when": { "onlyIdle": true, "acPower": true, "networkAvailable": true, "fileExists": "C:\\Tools\\sync.exe" },
  "action": { "file": "sync.py" }
}
```

`when` 不满足则记 `skipped(condition)`：`onlyIdle`（空闲>60s）、`acPower`（交流供电）、`networkAvailable`、`fileExists`/`fileNotExists`。

`file/args/workDir` 支持模板：`{{date}}` `{{time}}` `{{datetime}}`、`{{yyyyMMdd}}` 等任意 `yMdHms` 格式、`{{task}}`、`{{scripts}}` `{{logs}}` `{{dir}}`、`{{ENVVAR}}`。

### 4.5 托盘与日志

`自动化任务` 菜单：启用任务调度（勾选，持久化）、手动运行（枚举全部已启用任务）、最近运行（只读）、任务编辑器...、打开任务日志目录...、重载任务。

日志：`logs/tasks.log` 汇总调度事件；`logs/task-<name>.log` 记录 `triggered → started pid → OUT/ERR → finished exitCode/duration`。单文件 5MB 轮转保留 3 档。

### 4.6 常见示例

```json
[
  { "name": "open-notepad", "trigger": { "type": "manual" }, "action": { "file": "notepad.exe" }},
  { "name": "hotkey-sync", "trigger": { "type": "hotkey", "hotkey": "Ctrl+Alt+S" }, "action": { "file": "sync.py" }},
  { "name": "auto-unzip", "trigger": { "type": "watch", "path": "%USERPROFILE%/Downloads", "filter": "*.zip" }, "action": { "file": "unzip.js" }},
  { "name": "idle-clean", "trigger": { "type": "idle", "afterMinutes": 15 }, "action": { "file": "clean.ps1" }, "when": { "onlyIdle": true }},
  { "name": "frpc-daemon", "trigger": { "type": "startup" }, "action": { "file": "frpc.exe" },
    "options": { "mode": "detach", "singleInstance": true, "restart": "on-failure", "killWithHost": false }}
]
```

## 5. Windows 服务控制（services.*）

用途：让远程助手/脚本按授权清单启停指定服务（如 UUYC 远控 `GameViewerService`、RDP `TermService`），无需管理员运行宿主。

### 5.1 配置授权清单

`config.json` 根级：

```json
"Services": {
  "AllowedServices": [
    { "name": "GameViewerService", "desc": "UUYC 远程控制（GameViewer）", "requiresPin": false },
    { "name": "TermService", "desc": "Remote Desktop Services（RDP）", "requiresPin": true }
  ]
}
```

- `requiresPin` 按服务决定是否需要口令：高频无害服务可免 PIN；停掉影响大的（如 TermService 断 RDP）必须 PIN
- 兼容旧字符串写法 `["服务名"]`（缺省 desc 空、requiresPin=true）

### 5.2 一次性授予服务权限（管理员）

普通用户启停 Windows 服务需要服务 DACL 放行。以管理员运行：

```powershell
docs/remote-admin/grant-service-control.ps1 -Services GameViewerService,TermService
docs/remote-admin/grant-service-control.ps1 -Services TermService -DryRun   # 免管理员预览
```

脚本对服务 SDDL 追加启动/停止 ACE，原 SDDL 备份到 `C:\ProgramData\CarroDesk\service-dacl-backup\` 可回滚。未授予时调用会收到 access denied 及脚本指引。`docs/remote-admin/` 另含 RDP/WinRM/SSH/Agent 远程通道初始化脚本（见其 README）。

### 5.3 调用

见 §10 CLI 示例。清单外服务一律 -32602；需要 PIN 的服务缺/错 PIN 返回 -32002（连错 5 次封锁）。

## 6. 应用 PIN

首次运行引导设置 PIN（6 位数字），用于：锁屏解锁、托盘退出确认、以及需要口令的 IPC 能力（如 `services.stop`）。防暴力：连错 5 次进入封锁期，第 n 次超限封锁 `(n-4)*30s` 递增。哈希 PBKDF2-SHA256 加盐存储，明文不落盘。

## 7. 常见问题

- **热键不生效？** 被其他软件占用，任务日志记 `hotkey register failed`，换组合后重载。
- **watch 不触发？** 确认 `path` 存在、`filter` 匹配；`event` 默认 `created`，修改文件需设 `changed`。
- **脚本找不到？** 裸名需放 `scripts/` 或写绝对路径；日志首行打印解析后的 `starting: ...`。
- **中文路径 / tasks.json 乱码？** 全程 UTF-8，请以 UTF-8 保存（编辑器保存为标准 JSON 数组，手写支持 `//` 注释）。
- **detach 任务重启太频繁？** 调大 `restartDelaySec`/`stableUptimeSec`，或检查脚本自身退出码；连续失败超过 `restartLimit` 会熔断，重新保存任务即可复位。
- **CLI 报 -32020？** 宿主 CarroDesk.exe 没在运行，先启动托盘程序（客户端无需重启即可自动恢复）。

## 8. 日志一览

| 文件 | 内容 |
|---|---|
| `logs/tasks.log` | 任务调度事件汇总 |
| `logs/task-<name>.log` | 单任务输出 |
| `logs/ipc-audit.log` | IPC 调用审计（参数仅 SHA-256 摘要，口令不落盘） |

## 9. AI 助手接入（MCP）

任意 MCP 客户端（OpenClaw / AstrBot / DSH / opencode…）：

```json
{
  "mcpServers": {
    "carrodesk": {
      "command": "C:\\Home\\Tools\\CarroDesk\\CarroDesk.Cli.exe",
      "args": ["--mcp"]
    }
  }
}
```

工具清单、参数 schema、口令约定、错误码应对全部自动下发；深度手册内嵌于宿主，AI 可 `host.guide` 自取，也可直接阅读 [AI-AGENT-MANUAL.md](AI-AGENT-MANUAL.md)。

## 10. CLI 速查

```cmd
CarroDesk.Cli ctl host.status --json          :: 版本、运行时长、各模块状态
CarroDesk.Cli ctl host.modules.list           :: 模块清单（capabilityCount=0 表示分批开放中）
CarroDesk.Cli ctl host.capabilities.list      :: 完整能力表 + 参数 schema（权威清单）
CarroDesk.Cli ctl awake.status                :: 保持唤醒状态
CarroDesk.Cli ctl awake.on --minutes 120      :: 保持唤醒 2 小时（缺省无限期）
CarroDesk.Cli ctl awake.off                   :: 取消
CarroDesk.Cli ctl services.status             :: 授权服务 + requiresPin + 实时状态
CarroDesk.Cli ctl services.start --name GameViewerService
CarroDesk.Cli ctl services.stop  --name TermService --pin 123456
```

通用参数：`--json` 缩进输出、`--pin` 口令、`--pipe` 管道名、`--timeout` 毫秒（默认 3000）。退出码：0 成功 / 1 用法错误 / 2 业务错误 / 3 连不上宿主。`CarroDesk.exe ctl ...` 亦可直接转发（不必找 CLI）。

错误码：-32601 无此能力 / -32602 参数非法 / -32001 模块未运行 / -32002 口令问题或封锁 / -32003 限流 / -32004 超时 / -32010 内部错误 / -32020 宿主未运行。

## 11. 能力边界

- ❌ 无任意 shell / PowerShell 执行 —— 系统性管理走 `docs/remote-admin/` 的 SSH/Agent 通道
- ❌ 仅能操作授权清单内的服务（内核 DACL 兜底，改配置也绕不过）
- ❌ 无剪贴板内容读取、任意文件读写、远程退出宿主
