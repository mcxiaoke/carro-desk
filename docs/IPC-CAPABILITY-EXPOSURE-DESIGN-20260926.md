# CarroDesk 能力暴露设计方案（本地 IPC / 可选 HTTP / 远程网关）

> 版本：v1.2（2026-09-28，修订远程特权可达性：口令即确认，见 §9.5/§9.6/§11.6）
> 版本：v1.1（2026-09-28，增补远程阶段设计：§9 重写为运行时选型 / 模型决议 / 部署分支 / 能力开放清单；§8、§10、§11.6 同步修订）
> 版本：v1.0（2026-09-26）
> 平台与栈：.NET Framework 4.8 + WPF + C# 7.3（不可用 record / switch 表达式 / 可空引用）
> 范围决议（用户确认）：首批覆盖「本机进程 + AI Agent」；权限采用**白名单能力制**；远程（微信 ClawBot）只做设计预留，不进入首批实现。

---

## 0. 结论摘要

1. **主通道 = Windows 命名管道**，协议形状对齐 JSON-RPC 2.0，帧为「4 字节长度前缀 + UTF-8 JSON」。
2. **唯一内核 = 能力注册表（CommandRegistry）+ 分发器（CommandHost）**，与传输完全解耦；HTTP / MCP / CLI 都只是薄适配器。
3. **适配器一律瘦进程**：MCP stdio 与 CLI 都是**被拉起的客户端**，经管道回连已运行的首实例，自身不持有业务状态。
4. **HTTP 可选且默认关闭**，仅供跨语言调试/Web UI，绑 127.0.0.1 + 强制 token；不做主通道。
5. **白名单制**：未在注册表声明的能力名一律拒绝；**不提供任意 shell 执行能力**。
6. 远程阶段（微信）必须走**出站长连接**，本机不监听任何公网端口。
7. （v1.1）微信通道（ClawBot/iLink）与智能体运行时**解耦**：通道是微信官方的纯消息插件，运行时（OpenClaw / AstrBot / DSH / opencode+桥）可插拔；CarroDesk 侧唯一承诺是 S4 的 MCP server 形态，选型对比见 §9.2。

---

## 1. 现状核对（全部以代码为依据）

| 现状项 | 位置 | 对本次设计的含义 |
|---|---|---|
| 单实例互斥，第二实例直接 `Shutdown(0)`，无参数转发 | `src/App.xaml.cs:21`、`:57-63` | 需改造此分支，才能实现 CLI 转发 |
| 模块契约 `IModule` / `IModuleContext` | `src/Core/IModule.cs`、`src/Core/IModuleContext.cs` | 能力注册沿用「模块 pull 暴露」形状，不破坏现有约定 |
| 模块清单集中装配 | `src/Host/Modules/ModuleRegistry.cs` | 命令注册表在同一处装配最自然 |
| 模块生命周期状态机 | `src/Core/ModuleBase.cs`（Created→Initialized→Running→Stopped→Faulted） | 只有 `Running` 模块的能力可被调用 |
| 手写服务容器（支持多注册 / `GetServices`） | `src/Host/Services/ServiceContainer.cs` | 无需引入 MEDI；容器只做宿主内部装配 |
| 带超时的受保护调用 | `src/Host/Services/SafeInvoker.cs`（`RunTimeoutAsync`） | 命令执行超时直接复用，不另写机制 |
| 模块与菜单项收集模式 | `src/Host/Services/ModuleManager.cs:143-171`（`GetAllTrayMenuItems` + 异常隔离） | **能力收集照抄此模式**（含逐个 try/catch 隔离） |
| 任务执行底座已具备超时/重试/条件/日志 | `src/Modules/TaskScheduler/Services/TaskRunner.cs`、`Models/TaskDefinition.cs`（`Options.TimeoutSec`、`Retry`、`NotifyOnFailure`） | 「远程执行命令」应映射为触发既有任务，而非新造 shell |
| 手动触发已存在 | `src/Modules/TaskScheduler/Services/Triggers/ManualTrigger.cs` | `tasks.run` 只需接上它 |
| PIN 与失败限流 | `src/Services/HostPinService.cs`、`PinGuard`（`App.xaml.cs:92-93` 注册为单例） | 高风险能力的二次确认复用同一限流，勿另建 |
| 宿主配置模型 | `src/Models/AppSettings.cs`（**显式** `CopyTo` / `Merge`，非反射） | 新增 `Ipc` 节必须同步改 `CopyTo`，否则 reload 丢配置 |
| 模块配置读写 | `src/Core/IConfigManager.cs`（`GetModuleConfig<T>` / `SaveModuleConfig<T>`） | 模块私有能力无需在宿主配置里重复声明 |
| 测试工程**逐文件** `<Compile Include>` | `tests/CarroDesk.Tests/CarroDesk.Tests.csproj` | 新增测试文件必须登记，否则**静默不参与编译** |
| 既有结论：进程外扩展走 IPC | `docs/CODE-REVIEW-20260918-bd.md` §7.4、`docs/FIX-PLAN-20260918.md` | 本方案与该结论一致，属于其落地 |

---

## 2. 方案选型

| 通道 | 采纳 | 理由 / 否决原因 |
|---|---|---|
| **Named Pipe + JSON-RPC** | ✅ 主通道 | 原生、零依赖；可用 `PipeSecurity` 精确限定到当前用户，这是 HTTP 做不到的 |
| **MCP（stdio）** | ✅ 首选对外形态 | AI Agent 事实标准；stdio 在 net48 可行 |
| HTTP 127.0.0.1 | ⚠️ 可选、默认关 | `HttpListener` 走 HTTP.SYS，非管理员注册前缀**通常**要 `netsh http add urlacl`；且任何本机进程乃至浏览器页面都能打 loopback |
| WCF `netNamedPipeBinding` | ❌ | 本质仍是管道，额外引入配置与服务契约包袱 |
| `WM_COPYDATA` / 窗口消息 | ❌ | 无请求-响应语义、无返回值、有长度限制 |
| COM 自动化 | ❌ | 需注册组件（要管理员），与「绿色单 exe」定位冲突 |
| 仅 CLI 参数转发 | ❌ 单独不够 | 只覆盖单次动作，查不了状态；但作为管道之上的糖是必要的 |

**关键取舍**：不把 HTTP 当唯一出口，因为它的安全边界做不到「只允许当前用户」；而 MCP 已能满足 AI 侧，HTTP 仅在需要跨语言/浏览器调试时按需开启。

---

## 3. 总体架构

```
调用方                     适配器（薄，只做传输与协议映射）                内核
────────────────────────────────────────────────────────────────────────────────
本机进程/脚本   ──▶  [Named Pipe 服务]                          ┐
AI Agent        ──▶  [MCP stdio 瘦进程]  ──┐                    ├─▶  CommandHost
（可加）浏览器  ──▶  [HTTP 127.0.0.1]     ──┴── 管道回连首实例 ──┘    ├─ CommandRegistry（白名单）
（暂缓）微信    ──▶  [出站网关]（S6）                                ├─ 鉴权/风险分级/限流/审计
                                                                    └─▶  业务模块（ICommandProvider）
```

三条硬约束：

1. **内核不认识传输**：`CommandHost` 的输入是「能力名 + 参数字典」，输出是 `CommandResult`；不得出现 HTTP/管道类型的参数。
2. **模块只 pull 不 push**：模块通过实现 `ICommandProvider` **返回**自己的能力描述，禁止向容器注册任何东西（遵守 `IModuleContext` 的只读约定）。
3. **单实例唯一持有业务状态**：所有适配器都是瘦客户端；WPF 宿主是唯一状态持有者。

---

## 4. 能力模型

### 4.1 契约（新增于 `src/Core/Commands/`）

```csharp
public enum CommandRisk
{
    ReadOnly = 0,     // 只读查询
    Low = 1,          // 低危动作（锁屏、切音频、暂停计时）
    TaskExec = 2,     // 触发既有任务
    Privileged = 3    // 需要管理员/SYSTEM 权限的动作；实现与可达性见 §11
}

public sealed class CommandParam
{
    public string Name { get; set; }
    public string Type { get; set; }          // "string" | "int" | "bool"
    public bool Required { get; set; }
    public string Description { get; set; }
    public string[] AllowedValues { get; set; } // 非空即枚举白名单
}

public sealed class CommandDescriptor
{
    public string Name { get; set; }          // 全局唯一，形如 "screenlock.lock"
    public string ModuleId { get; set; }      // 由注册表填充，模块无需自填
    public string Summary { get; set; }
    public CommandRisk Risk { get; set; }
    public bool RequiresPin { get; set; }
    public IReadOnlyList<CommandParam> Params { get; set; }
    public Func<CommandRequest, CommandResult> Handler { get; set; }
}

/// <summary>可选能力出口。与 IModule.GetTrayMenuItems() 同构：pull 模式，宿主管收集与异常隔离。</summary>
public interface ICommandProvider
{
    IEnumerable<CommandDescriptor> GetCommands();
}
```

`CommandResult`：`{ bool Ok; object Data; CommandError Error; }`，`CommandError`：`{ int Code; string Message; }`。内部错误码与 RPC 错误码共用一张表（见 §5.2）。

### 4.2 能力命名与首批清单

命名规则：`<模块前缀>.<动作>`，全小写，点分两段；动作用动词或名词（`list` / `get` / `set` / `run` / `on` / `off`）。

| 能力名 | 模块 | 风险级 | 说明 |
|---|---|---|---|
| `host.status` | 宿主 | ReadOnly | 版本、运行时长、所有模块状态、当前配置摘要 |
| `host.modules.list` | 宿主 | ReadOnly | 模块 id/名称/状态/是否启停 |
| `host.capabilities.list` | 宿主 | ReadOnly | 能力表 + 参数 schema（供客户端缓存/MCP 映射） |
| `screenlock.status` | ScreenLock | ReadOnly | 是否已锁、空闲阈值、暂停截止时间 |
| `screenlock.lock` | ScreenLock | Low | 立即锁定 |
| `screenlock.pause` / `screenlock.resume` | ScreenLock | Low | 暂停/恢复空闲计时（参数：分钟） |
| `audio.output.list` | AudioSwitch | ReadOnly | 输出设备列表 + 当前项 |
| `audio.output.set` | AudioSwitch | Low | 切换输出设备（参数：设备名/id，枚举白名单） |
| `awake.on` / `awake.off` / `awake.status` | Awake | Low / ReadOnly | 唤醒模式开关与状态 |
| `automute.toggle` | AppAutoMute | Low | 自动静音开关 |
| `monitor.profile.list` / `monitor.profile.apply` | MonitorProfile | ReadOnly / Low | 显示器情景列表与应用 |
| `clipboard.history.count` | ClipboardHistory | ReadOnly | **仅条数**，不返回内容（隐私边界） |
| `clipboard.history.search` | ClipboardHistory | ReadOnly | 需配置显式放开，返回内容并全量审计 |
| `tasks.list` | TaskScheduler | ReadOnly | 任务清单 + 触发类型 + 启用态 |
| `tasks.run` | TaskScheduler | TaskExec | 参数为 `name`，**仅允许 tasks.json 中已存在的任务名** |

**明确不提供**（写进文档作为长期边界）：`shell.exec`、任意文件读写、任意 URL 打开、配置整体覆写、退出应用（`host.exit` 需 PIN 且默认关闭）。远程阶段的例外与开放细则见 §9.5（新增 `services.*`，仅限 DACL 已授权服务）。

### 4.3 分发校验顺序（不可调换）

1. 传输层鉴权（管道 ACL 已保证；HTTP 校验 token + Host 头）
2. 能力名存在性 → 不存在返回 `-32601`（**白名单制在此生效**）
3. 所属模块状态必须为 `Running` → 否则 `-32001`
4. 参数校验（类型、必填、枚举范围）→ 否则 `-32602`
5. `RequiresPin` → 走 `PinGuard` 挑战，用户拒绝返回 `-32002`
6. 限流（按能力名 + 来源维度）
7. 执行：涉及 UI/Com 的经 `Context.Dispatcher` 封送，统一包 `SafeInvoker.RunTimeoutAsync`（默认 5s，任务类 30s）
8. 审计落盘（成功与失败都记）

---

## 5. 传输与协议

### 5.1 命名管道服务

- 管道名：`\\.\pipe\CarroDesk.ctl.<sid或URL安全用户名>`，避免多用户/多会话撞名；配置可覆盖。
- **安全描述符**：显式传入 `PipeSecurity`，仅允许当前用户 SID + `SYSTEM`（拒绝 `Everyone`/`Authenticated Users`）。
  - .NET Framework 使用构造重载：`new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBuf, outBuf, pipeSecurity)`。
  - ⚠️ **已核对**：net48 参考程序集（`System.Core.dll`）中存在 `PipeSecurity`，**不存在** `NamedPipeServerStreamAcl` —— 后者是 .NET Core 3.0+ 的 API，本方案不可使用。
- 实例数用 `NamedPipeServerStream.MaxAllowedServerInstances`，异步 `WaitForConnectionAsync` 循环，每连接独立任务；连接断开/异常不得影响 accept 循环（照 `GetItemsGuarded` 的隔离风格）。
- 帧协议：`[int32 LE 长度][UTF-8 JSON]`，单帧上限 1 MB，超限直接断开并记审计；不支持跨帧粘包以外的任何扩展（保持简单）。
- ⚠️ **实测坑（S2 实施发现）**：`NamedPipeServerStream` 的 in/out 缓冲区参数**必须显式给足**（实现取 64 KB）——传 0（系统默认）时，「客户端流水线连发请求 + 服务端同时回写响应」会双向写阻塞死锁：字节模式下写操作要等对端读完才返回，0 缓冲下两端同时写即互相等死。单请求逐次收发不会触发，因此单测/首次联调都测不出来；MCP 瘦进程若做请求流水线必然踩中。已由 keep-alive 端到端测试（`PipeServer_E2E_Step4_KeepAliveTwoRequests`）固化为回归。

### 5.2 RPC 形状（对齐 JSON-RPC 2.0）

```json
{"jsonrpc":"2.0","id":"7f3a","method":"screenlock.lock","params":{}}
{"jsonrpc":"2.0","id":"7f3a","result":{"locked":true}}
{"jsonrpc":"2.0","id":"7f3a","error":{"code":-32601,"message":"capability not found","data":{"method":"shell.exec"}}}
```

| 错误码 | 含义 |
|---|---|
| -32700 | 帧/JSON 解析失败 |
| -32601 | 能力不存在（白名单未命中） |
| -32602 | 参数非法 |
| -32001 | 模块不可用（未运行/故障） |
| -32002 | 需要 PIN 或确认被拒 |
| -32003 | 触发限流 |
| -32004 | 执行超时 |
| -32010 | 内部错误（含异常摘要，不含堆栈） |

握手（首帧，可选但推荐）：`host.hello` 返回 `{protocolVersion, hostVersion, capabilitiesHash, requiresToken}`；客户端据此缓存能力表，`capabilitiesHash` 变化时重取。协议版本升级策略：`protocolVersion` 主版本不同直接拒绝并提示升级。

### 5.3 单实例参数转发与 CLI

改造 `App.OnStartup` 的 `!createdNew` 分支：不再直接 `Shutdown(0)`，改为尝试连接管道 → 发送 `host.invoke` → 写结果 → 退出（连接失败则保留现有行为）。

```
CarroDesk.exe ctl host.status --json
CarroDesk.exe ctl screenlock.lock
CarroDesk.exe ctl tasks.run --name backup
```

**实现方式二选一（推荐 A）**：

- **A. 独立控制台工程** `CarroDesk.Cli`（推荐）：`OutputType=Exe`，引用 `CarroDesk` 的 Core 契约或自带轻量客户端；天然有控制台、stdout 干净、无 WPF/STA 负担，**MCP 模式对 stdout 独占的要求可以零风险满足**。
- B. 复用主 exe 的 `--mcp` / `ctl` 模式：`WinExe` 无控制台，需 `AttachConsole(ATTACH_PARENT_PROCESS)` 后再写 stdout；MCP 模式下**严禁**任何日志/异常走 stdout（必须走 stderr 或文件），否则污染 JSON-RPC 流。

### 5.4 HTTP 适配器（可选，S5）

- 默认 `Enabled=false`；启用后仅绑 `http://127.0.0.1:<port>/`。
- 优先**手写 `TcpListener` 迷你 HTTP**（零依赖，避免 HTTP.SYS 的 URL ACL 提权步骤）；若用 `HttpListener`，需在文档中明确安装步骤 `netsh http add urlacl`，且失败时降级。
- 强制 `Authorization: Bearer <token>`；token 只在配置里存哈希；校验 `Host` 头为 `127.0.0.1`/`localhost`；**不打任何 CORS 头**；仅暴露 `POST /rpc`。

### 5.5 MCP 适配器（S4，优先于 HTTP）

- 由 AI 客户端孵化（stdio），映射关系：能力表 → tools（`Name`→tool name、`Summary`→description、`Params`→`inputSchema`），调用 → 管道 RPC 转发。
- **必须瘦进程**：stdio MCP server 是客户端拉起的一次性子进程，不能是第二个 WPF 宿主（会被现有 Mutex 掐掉）。
- SDK 取舍：官方 `ModelContextProtocol` 有 `netstandard2.0` 目标，理论可在 net48 引用，但会带入 `Microsoft.Extensions.*` 依赖链，**与 Costura 单 exe 打包需实测**；若打包失败，手写 stdio JSON-RPC（协议很薄，约一个文件的工作量，且无依赖风险）。

---

## 6. 安全模型

### 6.1 威胁 → 对策

| 威胁 | 对策 |
|---|---|
| 本机其它用户/会话进程调用 | 管道 ACL 限定当前用户 SID；管道名带用户标识 |
| 恶意本机进程或网页经 loopback 打 HTTP | HTTP 默认关闭；启用时 token + Host 校验 + 无 CORS 头 |
| 误调用破坏性能力 | 白名单 + 风险分级 + 高风险需 PIN |
| 剪贴板等隐私外泄 | 默认只暴露条数；内容类能力需显式配置放开并全量审计 |
| 远程重放 | 远程阶段强制 nonce + 时间戳 + HMAC 校验 |
| 提权扩散 | 宿主与全部适配器以普通用户权限运行，manifest 不声明 `requireAdministrator` |

### 6.2 审计

- 落盘 `%AppData%\CarroDesk\ipc-audit.log`，单行 JSON：`{ts, source, method, paramsDigest, ok, code, elapsedMs}`。
- 参数只记**摘要**（哈希 + 长度），原文不落盘；必要时按能力名配置白名单字段。
- 轮转策略与 `log.txt` 一致（超 1 MB 轮转），避免审计文件无限增长。

---

## 7. 配置

`config.json` 新增 `Ipc` 节（宿主级）。**S2 实际实现为最小集**（Http/Mcp 子节随 S4/S5 分期再扩展，避免死配置）：

```json
"Ipc": {
  "Enabled": true,
  "PipeName": "",
  "AuditEnabled": true
}
```

`PipeName` 空 = 默认 `\\.\pipe\CarroDesk.ctl.<当前用户SID>`；`AuditEnabled=false` 时命令审计关闭（`NullCommandAuditSink`）。

⚠️ `AppSettings` 使用**显式** `CopyTo` / `Merge`（见 `src/Models/AppSettings.cs:53-77`），新增字段必须同步改 `CopyTo`，否则一次配置 reload 就会静默丢值。此项必须补单测。

---

## 8. 分期实施与验收

| 期 | 内容 | 验收标准 |
|---|---|---|
| S1 | `Core/Commands/` 契约 + `CommandRegistry` + `CommandHost`（**无任何传输**） | 单测覆盖：白名单拒绝、参数校验、模块非 Running 拒绝、超时、异常不逃逸 |
| S2 | `Host/Ipc/` 管道服务 + 帧编解码 + `PipeSecurity` + 审计 | PowerShell 客户端脚本可调通 `host.status`；非当前用户连接被拒（可用另一账户验证） |
| S3 | 单实例参数转发 + CLI 工程 | `CarroDesk.exe ctl host.status --json` 输出并退出码正确 |
| S4 | MCP 瘦进程适配器 | 桌面 AI 能列出全部能力并成功调用 `screenlock.lock` |
| S5（可选） | HTTP 适配器（默认关） | 带 token 的 curl 可调通；无 token 返回 401；Host 头异常被拒 |
| S6（暂缓） | 微信 ClawBot 接入：运行时按 §9.2 选型 → §9.5 能力开放 → §9.6 安全项落地（异机分支先做 S5） | 指定微信消息可触发白名单能力并收到结果回推；非白名单能力被拒且留审计 |

每期要求：全量回归保持 195/195 通过，构建 0 警告 0 错误；新增测试文件须在 `tests/CarroDesk.Tests/CarroDesk.Tests.csproj` 登记。

---

## 9. 远程阶段（S6）：微信 ClawBot 接入设计

> v1.1（2026-09-28）重写。依据 2026-09 公开资料：ClawBot 是微信官方的 AI 助手连接插件，底层为 iLink（智联）协议（微信官方开放的个人 bot 通信协议）；它**只是消息通道**，背后的智能体运行时可插拔。

### 9.1 链路（更新）

```
微信（你本人） → 微信 ClawBot / iLink（官方通道，扫码绑定，纯消息）
             → 智能体运行时（自选，见 §9.2；LLM 建议配 DeepSeek API，见 §9.3）
             → MCP client → CarroDesk MCP server（S4）→ 管道 → CommandHost
```

- ClawBot/iLink 定位为**纯消息通道**：无微信支付/账号操作权限；仅私聊；bot 须在用户最后一条消息后 **24 小时**内回复，超出窗口需用户先发消息。
- **通道与运行时解耦**是本节核心结论：OpenClaw 不是必需品，任何「能当 MCP client + 能接 iLink 通道」的运行时都可用；CarroDesk 侧的唯一承诺就是 S4 的 MCP server 形态。
- 远程模型必须是**异步**：下发 → 本机执行 → 完成后在窗口内回推；长任务映射 TaskScheduler + 完成通知。
- 本机**不监听任何公网端口**；一切连接为出站（唯一例外：§9.4 异机分支的 S5 只绑 Tailscale 网段）。

### 9.2 智能体运行时选型（候选对比，2026-09 检索）

| 运行时 | 微信 iLink/ClawBot 接入 | MCP client | 模型灵活性 | Windows 同机部署 | 备注 / 风险 |
|---|---|---|---|---|---|
| **OpenClaw** | 官方默认搭档，教程与生态最成熟 | 原生（插件/skills 生态庞大） | DeepSeek/千问/Kimi 等均支持 | Node 系，Win10+ 部署教程成熟 | 功能最全（自主任务执行）；体量偏重；需收紧配置（发送方允许列表、最小权限工作区、防 prompt injection） |
| **AstrBot** | 多平台消息见长（QQ/TG/微信 iLink 等） | 支持 MCP 客户端 | Python，多模型 | 可 | 偏「消息机器人」形态，自主任务编排弱于 OpenClaw；插件体系成熟 |
| **DSH（DeepSeek Harness）** | dsh-clawbot 插件接 iLink，扫码绑定 | 有（插件生态） | 深度绑定 DeepSeek（模型已定 DeepSeek 时最顺） | 有 | 生态比 OpenClaw 小 |
| **opencode + 微信薄桥** | 无内置网关；配 x-cmd weixin CLI（基于 ClawBot 官方 API）或自写桥接脚本 | 成熟 | 支持 DeepSeek | 有 | 胜在与现有工具链一致、运行时可控；代价是自己维护常驻桥接（收微信→喂 opencode→回微信） |
| chatgpt-on-wechat（CoW） | 个人微信多走 hook/RPA（GeWe 等），有**封号风险** | 弱（需插件） | 多模型 | 可 | 对话机器人形态，工具调用/自主执行弱；**不推荐本场景** |

**推荐顺序**：OpenClaw > AstrBot / DSH > opencode+微信薄桥 > CoW。
**决议状态：待拍板**（§10 第 7 项）。**任何选择都不影响 CarroDesk 侧工作**——S1/S2/S4 照做，运行时是纯外部配置。

### 9.3 模型选型（决议）

1. **CarroDesk 内不内置任何模型**。自然语言解析发生在运行时侧 LLM，CarroDesk 只消费结构化 MCP 调用。
2. **否决「本地小模型」方案**：function calling 恰是小模型可靠性短板，解析错轻则拒绝、重则调错能力；运行时若坚持零云，可自行把模型换成 Ollama 本地模型，属运行时配置，与本项目无关。
3. **推荐 DeepSeek API（deepseek-chat）**：function calling 成熟、OpenAI 兼容接口；能力表 schema 约 1–2K token/次，按次计费几乎可忽略。
4. **CarroDesk 侧配合点**：能力描述必须规整——`Summary` 写清楚、参数尽量给 `AllowedValues` 枚举（如设备列表、任务名、服务名）。约束输出空间是便宜模型解析可靠性的第一保障。

### 9.4 部署位置（分支）

| 分支 | 传输 | 前置工作 |
|---|---|---|
| **同机（推荐）** | 运行时装在本机，stdio MCP（S4）直连 | 无额外传输工作；机器不开机远程本就无从执行，同机无额外损失 |
| 异机 | S5 HTTP 适配器，绑 Tailscale IP（100.99.99.105）+ Bearer token + Host 校验，防火墙仅放行 Tailscale 网段（与 docs/remote-admin 同一规矩）；运行时用 MCP over HTTP 接入 | 需先实现 S5 |

### 9.5 远程能力开放清单（S6 期生效）

| 能力 | 远程开放 | 说明 |
|---|---|---|
| `host.status` / `host.capabilities.list` | ✅ | 状态查询 |
| `screenlock.*` / `audio.output.set` / `awake.*` / `automute.toggle` / `monitor.profile.apply` | ✅ | 白名单内常规能力 |
| `tasks.run` | ✅ | 仅限 tasks.json 已有任务 |
| `services.status` / `services.start` / `services.stop`（**S6 新增**） | ✅（受限） | 仅限 §11.2 路径 A 中 DACL 已授权的服务；**远程下 `start`/`stop` 均须随命令附口令（PIN）**；安全边界=服务对象 DACL + 口令知识型确认 |
| `clipboard.history.search` | ❌ | 隐私边界，默认不开 |
| `host.exit` | ❌ | 永不远程开放 |
| 任意 shell / 真 SYSTEM 动作 | ❌ | 走 docs/remote-admin 通道（SSH/WinRM/Agent，Tailscale 内，人打字） |

### 9.6 远程安全要求（进入 S6 前必须补齐）

1. **来源白名单**：仅你本人的微信账号/设备；首次配对生成 device secret，仅存哈希。
2. **口令即确认（v1.2 决议）**：远程通道下，`Privileged` 级能力与 `services.start`/`stop` **必须随命令内联附口令（PIN）**——知识型确认替代「人在桌面点弹窗」，无人值守场景可用。校验复用 `PinGuard`（含失败限流）；缺口令/错口令一律拒绝（`-32002`）并审计。
3. **防重放**：nonce + timestamp + HMAC；若经官方 iLink 通道（自带账号绑定与传输安全）可降级为审计加强，落地前按当期通道文档复核。
4. **风险级开放以 §9.5 清单为准**；远程口令校验通过即视同本机 PIN 确认（§11.6 矩阵 v1.2 修订）。
5. **全量审计**：按 §6.2 + §11.7，远程来源单独标记通道与账号；**口令原文绝不落审计/日志**（摘要化或直接略过该字段）。

---

## 10. 待定项（需产品决策，实现前确认）

1. **无参数重复启动的行为**：是否维持现状（唤起/静默退出），还是改为「显示已有实例状态」。
2. **剪贴板内容类能力**是否默认开放（当前设计：默认只给条数）。
3. **CLI 工程形态**：独立 `CarroDesk.Cli` 工程 vs 主 exe `AttachConsole`（推荐前者）。
4. **MCP SDK 取舍**：官方包（依赖多、需实测 Costura 打包）vs 手写 stdio JSON-RPC（零依赖）。
5. **`host.exit`** 是否提供（涉及远程退出宿主，默认不提供）。
6. **是否引入特权代理**（§11.3）：2026-09-28 倾向——先走 §11.2 路径 A（DACL 授权）覆盖「启停指定服务」，仅当确需无人值守 SYSTEM 时再议。
7. **智能体运行时选型**（§9.2）：候选 OpenClaw / AstrBot / DSH / opencode+微信薄桥；结论：**不阻塞 S1–S4**，任何选型不影响 CarroDesk 侧设计。
8. **需要远程启停的服务清单**：确定后逐个执行 §11.2 DACL 授权（`sdshow` 备份 → `sdset` 追加 ACE → 非提权会话验证 `net start/stop`）。

---

## 11. 特权操作能力（需管理员 / SYSTEM 权限的动作）

> 典型需求：远程启动/停止/重启某个 Windows 服务。本节结论适用于所有需要提权的动作（改 `HKLM` 注册表、装驱动、改防火墙规则、写 `Program Files` 等）。

### 11.1 本机实测环境（决定方案前提）

| 项 | 实测结果 | 含义 |
|---|---|---|
| 当前用户 SID | `S-1-5-21-698639162-2475943860-3509766075-1001` | 授权命令里要用的主体 |
| 是否在 `BUILTIN\Administrators` | 是（该组标记为「只用于拒绝的组」） | 是管理员账户，但当前进程用的是 **UAC 过滤令牌** |
| 令牌完整性级别 | `Medium Mandatory Level` | **当前进程未提权**，`sc sdset` 之类操作会失败，需一次提权会话 |
| 应用清单提权声明 | `src/app.manifest` 中**无** `requestedExecutionLevel` | 默认 `asInvoker`，CarroDesk 一直以普通权限运行 ✅ 不要改这一条 |

**结论**：因为账户本身是管理员，路径 A 只需**一次性**提权配置，之后 CarroDesk 全程普通权限即可；不必为了"启动服务"把整个应用提权，也不必默认引入 SYSTEM 组件。

### 11.2 路径 A（首选，零提权）：把权限授予到「服务对象」上

思路：不改 CarroDesk 的权限，而是让 Windows 允许**当前用户在特定服务上**执行启动/停止。这是最小权限方案，也是唯一"即使远程通道被攻破，攻击者也只能动你显式授权的这几个服务"的方案——**用内核的对象权限做兜底，比在代码里写白名单更可靠**。

服务 DACL 的权限字母（SDDL）：`RP`=SERVICE_START、`WP`=SERVICE_STOP、`DT`=SERVICE_PAUSE_CONTINUE、`LC`=SERVICE_QUERY_STATUS、`CC`=SERVICE_QUERY_CONFIG、`CR`=SERVICE_USER_DEFINED_CONTROL、`RC`=READ_CONTROL。

操作步骤（**必须在提权会话中执行一次**，且**不要照抄示例，必须以本机输出为基准**）：

1. 备份当前 DACL（务必先做，SDDL 写错可能锁死该服务的管理）：
   ```
   sc.exe sdshow <服务名>          :: 把输出原样保存到 docs/ 或笔记中
   ```
2. 在返回串的 `S:` 段**之前**追加一条 ACE（现有 ACE 一律保留，只追不改）：
   ```
   (A;;CCLCSWRPWPDTLOCRRC;;;S-1-5-21-698639162-2475943860-3509766075-1001)
   ```
   说明：这条的形状与系统默认给 `PU`（Power Users）的 ACE 一致，额外含 `RP/WP/DT`，可避免 `.NET ServiceController` 打开句柄时因缺少某个查询权限而 `Access denied`。最简化写法是 `(A;;RPWPCR;;;<SID>)`，是否够用需自行实测。
3. 写回（`S:` 段原样保留）：
   ```
   sc.exe sdset <服务名> "D:( ...原串 + 新 ACE... )S:( ...原样保留... )"
   ```
4. 用**非提权**会话验证：`net start <服务名>` 应成功，`net stop` 同样可用。

之后 CarroDesk 侧代码（放 `src/Modules/…/Services/`）无需任何提权：

```csharp
using (var svc = new ServiceController(serviceName))
{
    if (svc.Status != ServiceControllerStatus.Running)
    {
        svc.Start();
        svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
    }
}
```

已知限制：

- **部分系统服务 DACL 被硬化**，连管理员都无法修改（例如 Task Scheduler），这类服务本路径走不通；
- 目标服务的启动类型须为「手动/自动」且依赖项就绪，否则 `Start()` 抛 `InvalidOperationException`；
- 授权**按服务对象**生效，新增服务需重新执行一次 —— 只适合「固定几个服务」，不适合"任意服务名"。

### 11.3 路径 B（确需 SYSTEM 时）：特权代理服务

当动作**即便管理员也不够**（需要 `LocalSystem`），或需要在**无人值守/远程**下执行时，引入独立组件 `CarroDesk.Agent`（Windows 服务）。

**五条硬性设计约束（缺任何一条都会变成提权漏洞）：**

1. **白名单必须放在普通用户不可写的位置**。若"允许操作的服务清单"仍来自 `%AppData%\CarroDesk\config.json`（用户可写），则改一行配置即可让代理启动任意服务 —— 这是实打实的本地提权漏洞。白名单只能放 `HKLM\SOFTWARE\CarroDesk\Agent` 或 `%ProgramData%\CarroDesk\` 并 ACL 限定管理员可写。
2. **只接受结构化参数**：`{op: "start" | "stop", service: "<枚举内取值>"}`。**永不接受命令行字符串**，永不做 `cmd /c` 转发。
3. **最小账户**：能用 `LocalService` 就不用 `LocalSystem` —— 以 LocalService 作为服务账户，再按 §11.2 只给目标服务授予 `RP/WP`，等于把代理能力锁死在特定对象上。确需 SYSTEM 时才用 SYSTEM。
4. **管道双向校验**：代理以 SYSTEM 运行时，其命名管道必须用 `PipeSecurity` 显式授予交互用户 SID 读写；同时在服务端以 `GetImpersonationUserName()` 校验连接者身份做纵深防御（不要只信 ACL）。
5. **Session 0 无 UI**：服务不能显示任何界面。UI 全部留在用户态 WPF 进程，服务只做特权动作并经 IPC 回结果 —— 与 §3 的分层一致。

补充：服务与 WPF 主程序可以是同一 exe 加 `--service` 模式，但该模式下必须**完全跳过 WPF 初始化与 UI，且不得触碰 `Global\CarroDesk_SingleInstance_*` 互斥体**，否则会与用户态实例互相踢掉。

### 11.4 路径 C（不装服务）：以 SYSTEM 运行的计划任务

```
schtasks /create /tn CarroDesk.Priv /tr "<helper.exe> <固定参数>" /sc ONCE /ru SYSTEM /rl HIGHEST /f
```

一次性创建需要提权，之后按需触发。**坑**：任务对象默认只允许管理员触发；要让普通权限的 CarroDesk 触发它，需用 Task Scheduler COM API 改写任务的安全描述符 —— 比路径 B 更绕、更易错。只适合低频、固定的特权动作。

### 11.5 路径 D（不推荐用于远程）：UAC 提权子进程

`ProcessStartInfo { Verb = "runas" }` 拉起一次性提权 helper。**远程场景下不可用**：UAC 同意提示出现在安全桌面上，无人点击会一直挂起到超时。仅适用于"人就在电脑前"的交互式操作。

### 11.6 远程可达性矩阵（**本节最关键**）

| 风险级 | 本机（交互） | 远程（微信通道） |
|---|---|---|
| ReadOnly / Low | 允许 | 允许（受白名单约束） |
| TaskExec | 允许 | 允许（仅限 tasks.json 已有任务） |
| `services.start/stop`（DACL 已授权服务，§9.5） | 允许（start 直执行；stop 需 PIN 弹窗） | **允许**（须随命令内联附口令，PinGuard 校验+限流） |
| **Privileged** | 允许（需 PIN 且 §11.2 授权到位） | **允许（v1.2）**：须随请求附口令，PinGuard 校验+失败限流，全量审计 |

理由：一旦让远程通道直达特权能力，整机安全性就等价于**微信账号的安全性** —— 账号被盗即等于本机管理员权限外泄。v1.2 修订：远程特权采用**「口令即确认」**——口令本身即授权凭据（知识型），替代「人在桌面点弹窗」，因此无人值守可用；残余风险=口令强度与消息通道泄露，缓解手段：PinGuard 失败限流、口令原文绝不落审计、口令泄露即换。同时 `services.*` 的授权粒度仍是**服务对象 DACL**（§11.2 路径 A），内核对象权限兜底不变。若口令模型不可接受，退回「人在回路」方案：

```
微信下发指令 → 本机弹出 PIN/确认框 → 用户确认 → 代理执行 → 结果回推
```

这实际上把"人必须在电脑前"变成必要条件，因此并未真正降低安全门槛。若目标是**完全无人值守的远程特权操作**，需先接受"这台机器的管理员权限挂在微信账号上"这一事实，并以 §11.3 全套约束 + 独立高强度凭据（不依赖微信账号体系）来兜底。

### 11.7 审计增强

特权动作在 §6.2 通用字段之外，额外记录：请求来源（本机/远程 + 具体通道）、审批方式（无 / PIN / 人在回路）、目标服务名、执行前后状态、命中的白名单规则。审计文件对普通用户**只读**（可写仅管理员），防止事后篡改。

### 11.8 分期建议

- 只需管理**少量指定服务**：直接走 §11.2，`S2` 即可完成，不引入新组件；
- 需要 SYSTEM 或无人值守：`S6` 实现 `CarroDesk.Agent`，并**先落实 §11.3 五条约束与渗透自查**，再开通远程；
- 任何情况下都**不要**改成 `requireAdministrator` 清单 —— 会让开机自启被 UAC 拦住，并把整个应用的攻击面放大到管理员级别。
