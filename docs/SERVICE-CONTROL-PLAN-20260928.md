# 服务控制：授权清单配置化 + 按服务口令策略（实施方案 v1.0）

> 日期：2026-09-28
> 范围：回应 S6 实测发现（幽灵 host 模块 / 授权清单为空 / 按服务配置），并新增「按服务免 PIN」能力
> 状态：待确认后实施（本文档不含代码改动）

---

## 0. 结论摘要

1. **授权清单配置化**：`AllowedServices` 从字符串数组升级为对象清单 `{ name, desc, requiresPin }`，存放在运行时 `config.json` 的模块节，**不进代码、不进 git**；兼容旧的字符串数组写法。
2. **按服务口令策略**：每个服务可独立配置 `requiresPin`（默认 `true`，fail-safe）。`GameViewerService` 可设 `false` 免口令直启停；`TermService` 建议保持 `true`（停它会断 RDP 会话）。
3. **内核支持动态口令**：`CommandDescriptor` 新增按请求的口令判定（`RequiresPinFor`），PinGuard 失败限流与审计全部复用；审计新增 `pinUsed` 字段（只记是否用口令，不记口令本身）。
4. **幽灵 host 模块修复**：`host.modules.list` / `host.status` 补 `host` 伪模块条目；每模块新增 `capabilityCount`，让「6 个模块暂无能力」从谜团变成可见事实（分批开放，见 IPC 设计 §4.2）。
5. **token 层维持 S5 预留**：本地管道有 OS ACL（仅当前用户 SID），远程走微信账号绑定 + PIN；只有 S5 HTTP 异机场景才需要 token，不在本次范围。

---

## 1. 配置模型

### 1.1 新 schema

`ServiceControlConfig.AllowedServices`：`List<ServiceAllowlistEntry>`

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `name` | string | 必填 | Windows 服务短名（如 `GameViewerService`） |
| `desc` | string | 空 | 人类可读描述（如「UUYC 远程控制」），**会流入工具 schema 与 status 返回**，AI 靠它把「开 UU远程」映射到服务名 |
| `requiresPin` | bool | **true** | 该服务 start/stop 是否要求口令 |

- 去重：按 `name` 大小写不敏感去重；`name` 为空的条目忽略
- **兼容**：JSON 反序列化同时接受旧写法 `"GameViewerService"`（字符串 → desc 空、requiresPin=true）与新对象写法
- `services.status` / `services.start` / `services.stop` 的 `name` 参数 `AllowedValues` 仍为纯服务名（枚举精确匹配）

### 1.2 存放位置与生效方式

- **不进代码/git** ✓：模块配置存运行时 `config.json`（仓库里的 `config.sample.json` 只给形状示例）
- 位置二选一（取决于安装方式）：
  - 标准安装：`%AppData%\CarroDesk\config.json`
  - **便携模式（你当前部署）**：`C:\Home\Tools\CarroDesk\app_data\config.json`（exe 旁有 `portable.ini`）
- 生效：保存后托盘菜单「**重载配置**」即可（S6 已实现：重载时重建能力注册表，desc/pin 立即流进工具 schema）；重启宿主等效

### 1.3 配置示例（你的机器）

```json
"Services": {
  "AllowedServices": [
    { "name": "GameViewerService", "desc": "UUYC 远程控制（GameViewer）", "requiresPin": false },
    { "name": "TermService", "desc": "Remote Desktop Services（RDP 远程桌面）", "requiresPin": true }
  ]
}
```

> 放在 config.json 根级（与 `"Language"` 等宿主设置平级的一个键）。编辑建议在宿主退出后进行，避免宿主后续保存覆盖。

---

## 2. 按服务口令策略

### 2.1 威胁模型回顾（PIN 到底防什么）

| 层 | 机制 | 防什么 |
|---|---|---|
| **权限边界** | 服务对象 DACL（§11.2 路径 A，OS 内核执行） | 未授权的服务**永远**启不了，配置被篡改也拦得住 |
| **确认强度** | PIN（知识型确认，§9.6） | 微信账号被盗 / AI 误判时，给破坏性操作加一道"人知道口令"的闸 |
| **账号边界** | 微信账号绑定（iLink 通道） | 决定谁能说话 |

**免 PIN 的影响面**：微信账号被盗的攻击者可**免确认**启停该服务——仅此而已（拿不到 shell/文件，也碰不到清单外的服务）。所以免 PIN 适合「启停无害、高频使用、DACL 已授权」的服务；后果严重的保持 PIN。

**具体到你的两个服务**：
- `GameViewerService`（UUYC 远程）：启停只是开关远程控制功能，无害且高频 → **`requiresPin: false` 合理**
- `TermService`（RDP）：**停止它会直接断掉 RDP 会话**（包括你远程在用的那条）→ 建议 `requiresPin: true`

### 2.2 内核改动（实现要点，实施时落地）

1. `CommandDescriptor` 新增：

```csharp
/// <summary>按请求动态判定是否需要口令。null 时退回 RequiresPin 静态值。
/// 在参数规整之后调用（可见规整后的 Params），PinGuard/失败限流/审计照常生效。</summary>
public Func<CommandRequest, bool> RequiresPinFor { get; set; }
```

2. `CommandHost` 校验顺序不变（§4.3），仅第 5 步从「`RequiresPin`？」变为「`(RequiresPinFor != null ? RequiresPinFor(effective) : RequiresPin)`？」——**顺序、PinGuard、限流、审计全部复用，不引入新机制**。
3. `services.start` / `services.stop` 的 `RequiresPin` 静态值改为 `false`，改用 `RequiresPinFor`：`req => entryOf(name).requiresPin`。`services.status` 恒免。
4. 审计：`CommandAuditEntry` 新增 `pinUsed`（bool）。口令原文依旧绝不落盘（§9.6）。
5. 兜底：配置里 `requiresPin` 缺失/非法 → 按 `true` 处理（fail-safe 与现行为一致）。

### 2.3 明确不做的事

- **不做「本机免 / 远程要」的来源区分**：配置即最终策略，全局一致——多一个维度就多一份解释成本，且远程通道自带微信账号绑定这道门。真要更严，把 `requiresPin` 设回 `true` 即可。
- 不提供「AI 修改授权清单」的能力：清单是人的显式授权，只能改配置文件（AI 能改清单等于自我扩权，违背最小惊讶）。

---

## 3. 幽灵 host 模块修复与模块能力可见性（实测发现 1/2）

1. `host.modules.list` 与 `host.status` 的模块清单**首项补 `host` 伪模块**：`{ "id": "host", "name": "CarroDesk Host", "status": "Running", "isRunning": true, "isHost": true, "capabilityCount": 4 }`——客户端按 moduleId 反查模块状态不再落空。
2. 每个真实模块条目新增 `capabilityCount`（来自注册表按 moduleId 统计）：ScreenLock 等 6 个模块显示 `0`，「暂无能力」从谜团变成可见事实。
3. 「6 个模块暂无能力」的定性：**有意分批**。IPC 设计 §4.2 首批只含 host.* 与 services.*；锁屏/音频/唤醒/任务等按计划逐步接入 `ICommandProvider`，`tools/list` 会自动长出来，无需改手册。

---

## 4. desc 的 AI 可见性（描述怎么流进工具）

| 流向 | 机制 |
|---|---|
| 工具 schema | `services.start/stop` 的 `name` 参数 `Description` 动态拼接映射表：`服务名（仅限授权清单）：GameViewerService=UUYC 远程控制；TermService=RDP 远程桌面` —— MCP `inputSchema` 直接收到 |
| `services.status` 返回 | 每条 `{ name, desc, status }`，AI 把「开 UU远程」翻译成 `GameViewerService` |
| `host.capabilities.list` | 随 schema 自动带出 |

效果示例：微信发「把 UU远程 开一下」→ AI 从 schema 描述匹配到 `GameViewerService` → 该服务 `requiresPin:false` → **直接调用成功，全程不需要口令**。

---

## 5. 你机器上的操作步骤（实施并构建后）

1. 退出 CarroDesk 托盘 → 编辑 `C:\Home\Tools\CarroDesk\app_data\config.json`，按 §1.3 加 `Services` 节 → 启动 CarroDesk（或编辑前启动、改完用托盘「重载配置」）
2. **DACL 授权**（管理员终端，两个服务各一次；TermService 若 DACL 被系统硬化导致 sdset 失败，脚本会如实报错，届时该服务只能走 RDP 会话内管理）：

```powershell
pwsh -File docs\remote-admin\grant-service-control.ps1 -ServiceName "GameViewerService,TermService"
```

3. 验证清单（非提权终端）：

| # | 操作 | 预期 |
|---|---|---|
| 1 | `CarroDesk.Cli ctl services.status` | 两个服务带 desc 与实时状态 |
| 2 | `ctl services.start --name GameViewerService` | **无 PIN 直接成功**（前提 DACL 已授权） |
| 3 | `ctl services.stop --name GameViewerService` | 无 PIN 直接成功 |
| 4 | `ctl services.start --name TermService` | `-32002`（该服务要求口令） |
| 5 | `ctl services.start --name TermService --pin <口令>` | 成功 |
| 6 | 口令连错 5 次 | `-32002 blocked` |
| 7 | `ctl host.modules.list` | 首项为 host 伪模块，各模块带 capabilityCount |
| 8 | 微信端（配好 OpenClaw 后） | 「开一下 UU远程」免口令直达 |

> ⚠️ TermService 提醒：停止它会**立即断开 RDP 会话**；如果你正通过 RDP 操作本机，`services.stop --name TermService` 会把自己踢下线。这是它保留 PIN 的原因之一。

---

## 6. 实施清单（确认后动工）

| 文件 | 改动 |
|---|---|
| `src/Core/Commands/CommandDescriptor.cs` | + `RequiresPinFor` |
| `src/Host/Commands/CommandHost.cs` | 第 5 步改动态判定；注释同步 §4.3 |
| `src/Host/Commands/CommandAudit.cs` | + `PinUsed` 字段 |
| `src/Modules/ServiceControl/Models/ServiceControlConfig.cs` | 对象清单 schema + 字符串兼容 converter |
| `src/Modules/ServiceControl/ServiceControlModule.cs` | desc 拼接、`RequiresPinFor` 构造、status 带 desc |
| `src/Host/Commands/HostCommands.cs` | host 伪模块 + capabilityCount |
| `tests/*` | 新旧 schema 兼容、免 PIN/PIN 两态、封锁、host 伪模块、desc 流入 schema、审计 pinUsed |
| `docs/AI-AGENT-MANUAL.md`、IPC 设计 §9.5/§11.6 | 实施后同步 |

**验收**：全量测试绿（0 警告 0 错误）+ §5 表格 8 项全过。

---

## 7. 待你确认的三个点

1. **TermService `requiresPin: true`** —— 同意？（我的建议：是。停它断 RDP，值得一道口令）
2. **免 PIN 全局一致**（不区分本机/远程）—— 同意？（建议：是。远程另有微信账号绑定这道门）
3. **GameViewerService 免 PIN 后的残留风险确认**：微信账号被盗的攻击者可免确认启停 UUYC 服务，影响面=该服务开关本身——你能接受？（能接受就按本文档实施）
