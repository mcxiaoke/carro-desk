# CarroDesk AI 助手接入手册

> 版本：2026-09-28（随宿主能力自动演进的部分以 `tools/list` 实时输出为准）
> 适用：OpenClaw / AstrBot / DSH / opencode / 任何 MCP 客户端；以及人肉命令行

---

## 0. 给 AI 的一段话（可直接粘进 system prompt / AGENTS.md / skill）

```text
你通过 CarroDesk MCP 服务器控制一台 Windows 工作机。工具清单用 tools/list 获取，
参数以 schema 为准，不要凭记忆猜。需要更详细的用法时调用 host.guide 获取完整手册。
涉及口令（pin）的工具：先看 schema 描述确认该服务是否需要口令，需要时问用户要 6 位
口令再调用，绝不猜测、最多重试一次（连错 5 次触发封锁）。services.* 只能操作授权
清单内的服务；宿主 CarroDesk.exe 未运行时所有调用报 -32020，提示用户先启动 CarroDesk。
本机没有任意 shell 能力；系统性管理需求让用户走 SSH/远控。
```

---

## 1. 接入形态

| 形态 | 命令 | 适用 |
|---|---|---|
| **MCP（推荐）** | `CarroDesk.Cli.exe --mcp` | OpenClaw/AstrBot/DSH 等助手运行时，自动发现工具 |
| CLI | `CarroDesk.Cli ctl <能力名> [--pin xx] [--key value]` | 人肉、脚本、opencode 的 Bash 工具 |
| HTTP | 未实现（S5，异机场景才需要） | — |

**前提**：宿主 `CarroDesk.exe`（托盘程序）必须在运行——MCP/CLI 只是无状态转发器，唯一状态在宿主。宿主未运行时所有调用报 `-32020`，宿主恢复后无需重启 MCP 会话即可自动恢复。

## 2. MCP 客户端配置

通用 JSON（各客户端字段名略有差异）：

```json
{
  "mcpServers": {
    "carrodesk": {
      "command": "C:\\Home\\Tools\\CarroDesk.Cli.exe",
      "args": ["--mcp"]
    }
  }
}
```

- exe 是单文件（依赖已内嵌），放 PATH 或任意目录均可
- stdio 传输；stdout 只有协议 JSON，日志在 stderr
- 客户端每条消息等一行 JSON 回应

## 3. 工具从哪来（AI 怎么"学会"用法）

1. `initialize` 响应的 `instructions` 字段：全局行为约定（口令、错误码、边界）
2. `tools/list`：**权威工具清单**——名称、描述、参数 schema（类型/必填/枚举/说明）全部自动下发
3. 本手册：背景与排错补充

工具清单来自宿主的能力注册表（`host.capabilities.list`），**随 CarroDesk 升级自动增长**，本手册不逐一维护调用细节——以 schema 为准。

## 4. 当前能力清单（2026-09-28，11 项）

| 工具 | 风险 | 口令 | 说明 |
|---|---|---|---|
| `host.hello` | 只读 | — | 握手：协议版本、宿主版本、能力表哈希 |
| `host.guide` | 只读 | — | 读取本手册全文（Markdown），详细用法/约定/边界都在里面 |
| `host.status` | 只读 | — | 版本、进程、运行时长、各模块状态（首项为 host 伪模块） |
| `host.modules.list` | 只读 | — | 模块清单（含每模块 capabilityCount，0=分批开放中） |
| `host.capabilities.list` | 只读 | — | 完整能力表 + 参数 schema |
| `services.status` | 只读 | — | 查询授权服务（含 desc 与各自的口令策略） |
| `services.start` | 特权 | **按服务** | 启动已授权服务；`name` 参数描述里带 服务名=描述 映射 |
| `services.stop` | 特权 | **按服务** | 停止已授权服务；同上 |
| `awake.status` | 只读 | — | 保持唤醒状态：模式/剩余分钟/是否激活/电池暂停/进程联动 |
| `awake.on` | 低 | — | 保持唤醒：缺省无限期；`minutes`（1-1440）=定时。运行时状态，重启后回到配置模式 |
| `awake.off` | 低 | — | 取消保持唤醒（回到 passive，并抑制进程联动自动重开） |

## 4.3 保持唤醒（awake.*）

```text
awake.on                    → 无限期保持唤醒
awake.on  {"minutes":120}   → 保持 2 小时（1-1440）
awake.off                   → 取消（回到 passive）
awake.status                → mode/isActive/remainingMinutes/expireAt/keepDisplayOn
```

- `awake.on`/`awake.off` **不需要口令**（低风险能力）
- 运行时状态：不持久化，宿主重启后回到 config.json 中 Awake 模块配置的模式
- 「机器睡眠远程就废了」的正解是让宿主的 Awake 模块保持唤醒（无限期，或进程联动盯住助手进程），而不是依赖单次调用

## 4.1 按服务口令策略（services.*）

每个服务的 `requiresPin` 由用户在 config.json 的 `Services.AllowedServices` 逐个配置（缺省 `true`）：

- `requiresPin: false` 的服务（如高频无害的 GameViewerService）**不需要 pin，直接调用**
- `requiresPin: true` 的服务（如 TermService——停它会断 RDP 会话）必须带 `pin`
- 拿不准时看 `services.status` 返回的 `requiresPin` 字段，或 `name` 参数描述
- 口令连错 5 次触发封锁（-32002 blocked），所以**先问用户、别硬试**

配置示例（config.json 根级，用户维护）：

```json
"Services": {
  "AllowedServices": [
    { "name": "GameViewerService", "desc": "UUYC 远程控制（GameViewer）", "requiresPin": false },
    { "name": "TermService", "desc": "Remote Desktop Services（RDP 远程桌面）", "requiresPin": true }
  ]
}
```

## 4.2 host 伪模块

`host.modules.list` 与 `host.status` 的模块清单首项是 `id: "host"`（`isHost: true`）——宿主自身，承载 host.* 五个能力。真实模块的 `capabilityCount: 0` 表示该模块还没开放能力（分批开放中），不是故障。

## 5. 口令（PIN）约定 —— AI 必读

- `pin` 放在 **`arguments.pin`**（它不是普通业务参数，会被单独提取，不进审计）
- **先问用户，不要猜**：`PinGuard` 连错 5 次触发封锁期，期间即使口令正确也返回 `-32002 blocked`
- 用户没给口令就调用了需要口令的工具 → 返回 `-32002 pin rejected`，此时应当**回问用户**而不是重试
- 口令 = CarroDesk 的应用 PIN（用户首次运行时设置的那个）

## 6. 错误码对照

| 码 | 含义 | AI 应对 |
|---|---|---|
| -32601 | 无此能力 | 用 tools/list 核对名称 |
| -32602 | 参数非法（缺必填/枚举外/类型错） | 按 schema 修正后重试 |
| -32001 | 模块未运行 | 提示用户检查 CarroDesk 模块状态 |
| -32002 | 口令缺失/错误/封锁 | **问用户要口令**；blocked 时告知稍后再试 |
| -32003 | 触发限流 | 稍等再试，勿连续调用 |
| -32004 | 执行超时 | 服务启停类可稍后用 status 查询实际状态 |
| -32010 | 内部错误 | 把 message 原样转告用户（含修复指引） |
| -32020 | 宿主未运行/管道不通 | 提示用户启动 CarroDesk.exe |

## 7. 对话示例（自然语言 → 工具调用）

- 「电脑什么情况」→ `host.status`（+ 需要时 `services.status`）
- 「UU远程那个服务开一下，口令 1234」→ `services.start { "name": "GameViewerService", "pin": "1234" }`
- 「帮我看看能干什么」→ `tools/list`（或 `host.capabilities.list` 拿完整 schema）
- 「把打印机服务停了」→ 若不在授权清单 → 返回 -32602，如实转告用户并指引授权

## 8. 边界（没有的东西，不要找）

- ❌ 任意 shell / PowerShell 执行 —— 系统性管理走 `docs/remote-admin/` 的 SSH/Agent 通道（Tailscale 内，人打字）
- ❌ 剪贴板内容读取、任意文件读写、配置整体覆写、远程退出宿主（`host.exit` 永不开放）
- ❌ 操作授权清单之外的服务（内核级 DACL 兜底，配置改了也没用）

## 9. 人肉速查（CLI）

```cmd
CarroDesk.Cli ctl host.status --json
CarroDesk.Cli ctl host.capabilities.list
CarroDesk.Cli ctl services.start --name GameViewerService --pin 1234
CarroDesk.Cli ctl services.status
```

`--json` 缩进输出；`--pipe` 指定管道名；`--timeout` 毫秒。退出码：0 成功 / 1 用法错误 / 2 业务错误 / 3 连不上宿主。
