# 特权执行桥（Elevated Bridge）实现设计

- 日期：2026-11-14 20:34:19
- 状态：v1.0 设计稿，未实施（代码未改动）
- 关联：`docs/IPC-CAPABILITY-EXPOSURE-DESIGN-20260926.md` §11（路径 A/B/C/D、五条硬约束、§11.8 红线）、`docs/remote-admin/README.md`（一次性管理员初始化）、`docs/TASKS-PERSISTENT-RUN-DESIGN-20260928.md`（wait/detach 语义）
- 代码定位：`src/Modules/TaskScheduler/`、`src/Host/Ipc/`、`src/Program.cs`、`src/Common/`

---

## 1. 目标与范围

让 `tasks.json` 里的某个任务能执行**需要管理员权限**的动作，满足：

1. 一次管理员确认（UAC）完成初始化，之后每次触发都不弹 UAC，无人值守可用；
2. `tasks.json` 是普通用户可写文件，**不能**因为加了个字段就变成任意命令提权入口；
3. 现有 `wait/detach`、超时、重试、停止、失败通知、任务日志、守护重启等语义尽量不改；
4. 不改变宿主权限模型：`src/app.manifest` 保持 `asInvoker`（§11.8 红线）。

**非目标**：不做通用远程 shell、不做任意命令提权、不替代 `docs/remote-admin/` 现有通道。

---

## 2. 现状实证（决定设计取舍）

| 事实 | 位置 | 对设计的约束 |
|---|---|---|
| `net10.0-windows` / `WinExe` / `UseWPF=true` / `StartupObject=CarroDesk.Program` | `src/CarroDesk.csproj` | 同一 exe 可以承载 `--agent` 无 UI 模式，但必须避开 WPF 初始化 |
| `--mcp` 与 `ctl` 两个瘦进程拦截，位于单实例互斥体**之前** | `src/Program.cs:34-52` | 新的 `--bridge-run` 必须插在同一拦截段，且不得触碰 `Local\CarroDesk_*` 互斥体（§11.3 补充） |
| `app.manifest` 无 `requestedExecutionLevel` → `asInvoker`，宿主为 Medium 过滤令牌 | `src/app.manifest` | 不要改，改了开机自启会被 UAC 拦且放大攻击面 |
| 任务进程 `UseShellExecute=false` + `RedirectStandardOutput/Error=true` | `src/Modules/TaskScheduler/Services/TaskRunner.cs:283-295` | `Verb="runas"` 与之互斥；且 `runas` 是每次弹窗，不是一次授权 |
| `TaskProcessHandle` 为 `sealed`，直接持有 `Process`，对外提供 `WaitAsync/Stop/BeginExitWatch/Exited/Pid` | `.../TaskProcessHandle.cs` | 若新增"桥接句柄"要替换它，得改 `TaskRunner`、`TaskSchedulerService`、UI、测试全部引用点，改动面大 |
| wait 走 `TaskRunner.RunAsync(..., onStarted)`，detach 走 `StartDetached` + `AttachDetached` | `.../TaskSchedulerService.cs:820`、`:794` | 句柄类型必须保持一致，否则调度器、状态机、运行注册表全要动 |
| 帧格式 `[int32 LE 长度][UTF-8 JSON]`，1 MB 上限 | `src/Host/Ipc/FrameCodec.cs` | Agent 管道直接复用 |
| 管道名带用户 SID；`PipeSecurity` 仅当前用户 + SYSTEM；`NamedPipeServerStreamAcl.Create`；64 KB 缓冲；accept 循环 + 每连接独立 Task | `src/Host/Ipc/NamedPipeCommandServer.cs:43-82,114-162` | Agent 服务端照抄这套，不要另起炉灶 |
| 能力注册表 = 白名单本体；校验链含 PIN、限流、审计 | `src/Host/Commands/CommandHost.cs`、`CommandRegistry.cs`、`src/Core/Commands/CommandDescriptor.cs` | 桥的 capability 模型与之同构，审计复用 `FileCommandAuditSink` |
| `ConfigService.DirPath` = `%AppData%\CarroDesk` 或便携 `app_data` | `src/Services/ConfigService.cs:46-54` | **用户可写**，白名单绝不能放这里 |
| 自提权模板、`icacls *SID` 收紧 ACL、SYSTEM/Highest 计划任务模板 | `docs/remote-admin/setup-remote-access.ps1:66-90,250,292,304-315` | 安装脚本直接沿用 |
| 真实进程级探针测试（写 .bat 起进程读日志） | `tests/CarroDesk.Tests/TaskRunnerTests.cs` | 桥的集成测试沿用同一模式 |

---

## 3. 总体架构与核心决策

### 3.1 核心决策：薄客户端代理进程（TaskRunner 零抽象改动）

不新写 `BridgeProcessHandle`、不改 `TaskProcessHandle` 的 `sealed` 结构，而是让 `TaskRunner` **仍然启动一个本地普通权限进程**：

```
tasks.json (action.bridge)
      │
      ▼
TaskRunner.TryStart  ── 展开为 ──►  CarroDesk.exe --bridge-run <capability> [--param k=v ...]
      │                                   │  （普通权限瘦进程，stdout/stderr 被 TaskRunner 重定向）
      │                                   │
   TaskProcessHandle                命名管道（带 SID + 显式 ACL）
   （wait/detach/超时/Stop            │
     /日志/retry/守护 全部原样）          ▼
                              CarroDeskAgent（LocalService / SYSTEM，或 Highest 常驻 helper）
                                   │  查白名单 → 校验参数 → 起特权进程（挂 ProcessJob）
                                   ▼
                              真实脚本 / 服务操作
```

瘦进程把 Agent 返回的事件翻译成自己的 **stdout / stderr / 退出码**：

- `stdout`/`stderr` 事件 → `Console.Out` / `Console.Error` → 被 `TaskRunner` 的输出回调捕获 → `RunContext.Output`（写入该次 run 文件）；
- `exit{code}` 事件 → `Environment.Exit(code)` → `TaskProcessHandle.WaitAsync` 拿到真实退出码；
- **连接断开 = 取消**：Agent 检测到管道断开即 `Dispose` 该请求对应的 `ProcessJob`（kill-on-close），终止整棵特权进程树。

于是 `timeoutSec` 超时、`TryStop`、宿主退出 `killWithHost=true` 这三条路径本来就是"杀瘦进程"，现在自动级联到 Agent 侧，无需新增任何取消协议。这是本方案最大收益。

### 3.2 为什么不用"配置时给 task 盖章授权"

UAC 提升是进程创建时按次发放的令牌，Windows 没有"给某个脚本永久授权"的对象。配置界面那一次 UAC 的唯一用途是 **创建已提权的执行端**（§8 安装脚本），之后 task 只是通过管道请求一个白名单能力。

### 3.3 时序（wait 模式）

```
用户点「启用特权执行」 ──一次 UAC──► install-elevated-bridge.ps1
      ├ 建 AgentDir + 收紧 ACL
      ├ 写 allowlist.json（管理员可写 / 普通用户只读）
      ├ 复制 Agent exe，创建并启动服务
      └ 探测管道，写安装报告

任务触发 → TaskRunner 起瘦进程 → 连管道 → hello/校验
        → 请求 {capability, params} → Agent 查白名单 → 起进程（ProcessJob）
        → started/stdout/stderr/exit 事件流 → 瘦进程回显并退出
        → TaskProcessHandle 收退出码 → recent / 失败通知 / retry 照常
```

---

## 4. 组件与文件清单

**新增（建议）**

| 路径 | 内容 |
|---|---|
| `src/Host/Bridge/BridgeContracts.cs` | 请求/事件 DTO、`BridgeErrorCodes` |
| `src/Host/Bridge/BridgePipeSecurity.cs` | 管道名（带 SID）、PipeSecurity、服务端/客户端身份校验 |
| `src/Host/Bridge/BridgeThinClient.cs` | `--bridge-run` 瘦进程主逻辑 |
| `src/Host/Bridge/Agent/BridgeAgentServer.cs` | 服务端 accept 循环、连接隔离、白名单查表、事件回写、断开取消 |
| `src/Host/Bridge/Agent/AllowlistStore.cs` | 白名单加载、ACL 自校验、capability 查找与参数校验 |
| `src/Host/Bridge/Agent/BridgeExecutor.cs` | 按 capability 起进程（挂 `ProcessJob`）、输出/超时/字节上限 |
| `src/Host/Bridge/Agent/BridgeAudit.cs` | 审计（普通用户只读），复用 `CommandAuditEntry` 形状 |
| `scripts/install-elevated-bridge.ps1` | 一次 UAC 安装 |
| `scripts/uninstall-elevated-bridge.ps1` | 回滚（保留 allowlist 备份） |
| `tests/CarroDesk.Tests/BridgeProtocolTests.cs` 等 | 见 §12 |

**修改**

| 路径 | 改动 |
|---|---|
| `src/Program.cs` | 在 CLI 拦截段加 `--bridge-run` 与 `--agent` 两个分支（都在单实例互斥体之前） |
| `src/Modules/TaskScheduler/Models/TaskDefinition.cs` | `TaskAction` 加 `Bridge`、`BridgeParams`；`Validate()` 加校验 |
| `src/Modules/TaskScheduler/Services/TaskRunner.cs` | `TryStart` 在脚本包装前插入 bridge 展开分支 |
| `src/Modules/TaskScheduler/Views/TaskEditDialog.xaml(.cs)` | 「特权执行」区 + 「启用特权执行」按钮 + capability 下拉 |
| `src/Samples/tasks.sample.json` | 加 bridge 任务示例 |
| `docs/USAGE.md` / `docs/AI-AGENT-MANUAL.md` | 使用说明与能力清单 |

> 若决定 Agent 独立成 exe（见 §14 开放问题），需再加 `src/CarroDesk.Agent/CarroDesk.Agent.csproj` 并登记到 `CarroDesk.slnx`。

---

## 5. 管道协议

### 5.1 管道与身份

- 管道名：`\\.\pipe\CarroDesk.agent.v1.<sha8(allowedUserSid)>`，SID 由安装脚本写入 `agent.json`，客户端用自身 SID 计算（参考 `NamedPipeCommandServer.BuildDefaultPipeName`）。带 SID 是为了避免多用户/多会话撞名。
- 服务端 ACL：`NamedPipeServerStreamAcl.Create(..., PipeDirection.InOut, MaxAllowedServerInstances, Byte, Asynchronous, 64*1024, 64*1024, security)`；`PipeSecurity` 显式 `Allow(allowedUserSid, ReadWrite)` + `SYSTEM` + `Administrators`，**取代默认 ACL**。
- 服务端纵深防御：连接建立后 `server.GetImpersonationUserName()` → 解析 SID 必须等于 `allowedUserSid`，否则断开并记审计拒绝。
- 客户端防管道劫持：连上后用 `GetNamedPipeServerProcessId`（Win32 P/Invoke）取服务端 pid → 查其映像路径必须等于 `AgentDir\CarroDesk.Agent.exe`（或约定的同 exe 路径），否则立即断开。仅靠"能连上"不足以证明对端是特权端。
- 帧：复用 `FrameCodec`（1 MB 上限，超限断开）。

### 5.2 报文

单连接单请求（每请求新建连接，服务端 accept 循环天然支持并发）。

请求（瘦进程 → Agent）：

```json
{"v":1,"id":"7f3c...","capability":"restart-spooler","params":{"force":"true"},"label":"task:nightly-clean"}
```

- `capability` 必须是白名单里的名字；`params` 只允许**标量**（string/bool/int），且必须命中该 capability 声明的参数 schema；
- `label` 只作审计标签（如任务名），**不参与任何执行决策**，防止伪造来源；
- 协议里**不存在** `commandLine` / `file` / `args` 这类字段，服务端永不 `cmd /c` 转发。

事件（Agent → 瘦进程）：

```json
{"type":"hello","v":1,"agentPid":4212,"hostMode":"service"}
{"type":"started","pid":8890}
{"type":"stdout","line":"..."}
{"type":"stderr","line":"..."}
{"type":"exit","code":0}
{"type":"error","code":77,"message":"capability not allowlisted"}
```

`error.code` 建议固定集合：`74`=Agent 不可达/未安装、`75`=身份校验失败、`76`=协议/参数非法、`77`=capability 未授权、`78`=目标脚本 ACL/路径校验失败、`79`=Agent 内部错误。瘦进程用这些码退出，TaskLogger 里能直接看出原因。

### 5.3 断开即取消（实现要点）

- Agent 为每个请求创建 `ProcessJob`（复用 `src/Common/ProcessJob.cs`，kill-on-close）并 `TryAssign` 目标进程；
- 连接处理任务在 `finally` 中 `job.Dispose()` → 整棵特权进程树被内核回收；
- **断开检测不能只靠写失败**（脚本可能长时间无输出）：为每个连接起一个 250 ms 间隔的 `server.IsConnected` 轮询任务，false 即触发取消；这一点需在实机验证服务进程下管道断开的即时性；
- 输出限速：单请求 `maxOutputBytes`（默认 1 MiB）、单行长度上限、超限截断并记 `stderr` 提示，防止刷爆管道与任务日志。

---

## 6. 白名单（本方案的安全核心）

### 6.1 位置与 ACL

```
C:\ProgramData\CarroDesk\Agent\
├── CarroDesk.Agent.exe        （或 CarroDesk.exe，普通用户不可写）
├── agent.json                 （hostMode / allowedUserSid / pipeSalt）
├── allowlist.json             （能力白名单）
├── scripts\                   （固定脚本，普通用户不可写）
└── audit.log                  （普通用户只读）
```

安装脚本用 SID 写法收紧（避免中文系统账户名差异，参考 `setup-remote-access.ps1:250,292`）：

```powershell
icacls $AgentDir /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-32-545:RX' | Out-Null
icacls $allowList /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-32-545:R'  | Out-Null
icacls $auditLog  /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-32-545:R'  | Out-Null
```

> 绝对不能放 `ConfigService.DirPath`（`%AppData%\CarroDesk`）——用户可写。否则改一行 allowlist 即可以 SYSTEM 跑任意程序，是实打实的本地提权漏洞（§11.3 约束 1）。

### 6.2 schema

```json
{
  "version": 1,
  "capabilities": [
    {
      "name": "restart-spooler",
      "kind": "service",
      "op": "restart",
      "target": "Spooler",
      "timeoutSec": 30
    },
    {
      "name": "clean-temp",
      "kind": "script",
      "file": "scripts\\clean-temp.ps1",
      "args": ["-Days", "{{days}}"],
      "workDir": "",
      "timeoutSec": 60,
      "maxOutputBytes": 1048576,
      "params": {
        "days": { "type": "int", "min": 1, "max": 365, "required": true }
      }
    }
  ]
}
```

### 6.3 校验规则（`AllowlistStore`，启动期 + 每次请求）

1. `kind` 属封闭集合（首期 `script` / `service`），未知值整条拒绝并记日志；
2. `file` 用 `Path.GetFullPath(Path.Combine(AgentDir, file))` 解析后必须落在 `AgentDir` 内；拒绝绝对路径、`..`、`%VAR%`、含 `:` 的驱动器路径；
3. **脚本文件与 Agent exe 的 ACL 不得含普通用户写权限**，启动前逐条校验，失败则该 capability 标记不可用（防替换后服务重启执行任意代码）；
4. `args` 是白名单里的固定数组，只允许 `{{param}}` 占位符；占位符值按 `params` schema 校验后，用 `CarroDesk.Common.CommandLine.QuoteArgument` 引号化再注入（与 `TaskRunner` 现有 .bat/.ps1 包装同一套转义），禁止拼接 shell 元字符；
5. 服务端不接受任何来自客户端的路径、命令行、环境变量覆盖；`workDir` 固定为 `AgentDir`（或白名单条目内的固定目录）；
6. `timeoutSec`、`maxOutputBytes` 取白名单值，客户端不可覆盖（防绕过限流）。

---

## 7. Agent 后端两种形态

协议层、白名单、瘦进程完全共用，只换宿主，通过 `agent.json` 的 `hostMode` 选择。

| | 形态 1（推荐，B 方案） | 形态 2（P3 备选） |
|---|---|---|
| 宿主 | Windows 服务 `CarroDeskAgent`，`--agent` 模式，SCM 管理 | 以**交互用户 + RunLevel Highest** 的常驻 helper（计划任务 `/sc ONSTART`） |
| 账户 | `LocalService`；确需 SYSTEM 时才升 | 当前用户，但持**完整管理员令牌** |
| 开机自启 | `start= auto` | 计划任务触发器 |
| 用户环境 | Session 0：无 UI；`HKCU` / DPAPI masterkey / `%APPDATA%` / 映射驱动器 / 网络凭据都是服务账户视图 | 与交互用户一致，DPAPI、凭据、会话内资源可用 |
| 适用 | 服务启停、注册表/系统目录写入、固定脚本 | 依赖用户凭据/DPAPI/交互会话的脚本 |
| 主要风险 | 服务 `binPath` 与 AgentDir 若普通用户可写 → 替换 exe 提权；需用 `sc.exe sdset` 收紧服务 DACL | 高完整性进程与低完整性桌面同会话，UIPI 攻击面更大；SCM 不管生命周期，需计划任务 `RestartCount` + 看门狗 |

**首期只实现形态 1**。注意 §11.4 提到的"任务对象默认只允许管理员触发"的坑，在形态 2 里因为我们不与任务对象交互（helper 常驻、走管道）而不成立。

无论哪种形态：Agent 进程**永不显示 UI**，只做执行与事件回写（§11.3 约束 5）。

---

## 8. 一次性 UAC 安装

### 8.1 `scripts/install-elevated-bridge.ps1`

自提权部分直接沿用 `setup-remote-access.ps1:66-90`：

```powershell
function Test-Admin {
  ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) {
  $exe = if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { 'pwsh.exe' } else { 'powershell.exe' }
  Start-Process -FilePath $exe -Verb RunAs -ArgumentList @(
    '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-StatusFile',"`"$StatusFile`"")
  exit 0
}
```

提权后按序执行，全部幂等并写状态文件供 UI 回显：

1. 计算 `allowedUserSid`（交互用户 SID；服务里不能靠 `WindowsIdentity.GetCurrent()`，必须由安装脚本写入 `agent.json`）；
2. `New-Item $AgentDir -Force` → 按 §6.1 收紧 ACL；
3. 复制 Agent exe 与 `scripts\`；**只在不存**在时写 `allowlist.json` 模板（绝不覆盖用户已编辑的白名单，遵守覆盖防护）；
4. 创建/更新服务：
   ```powershell
   & sc.exe create CarroDeskAgent binPath= "`"$agentExe`" --agent" start= auto obj= "NT AUTHORITY\LocalService" DisplayName= "CarroDesk Elevated Bridge"
   & sc.exe sdset CarroDeskAgent "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWRPWPDTLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;AU)"
   & sc.exe start CarroDeskAgent
   ```
   （SDDL **必须以本机 `sc.exe sdshow` 输出为基准**再追加，不要照抄示例；参考 §11.2 的备份—写回—非提权会话验证流程）
5. 探测管道（客户端 hello 握手）+ 输出安装报告；失败回滚并保留日志。

### 8.2 UI 入口

任务编辑器「特权执行」区放一个按钮「启用特权执行（需一次管理员确认）」，点击时：

```csharp
Process.Start(new ProcessStartInfo {
    FileName = "pwsh.exe",
    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + installScript + "\" -StatusFile \"" + status + "\"",
    UseShellExecute = true,   // 参考 src/Host/Services/HostMenuActions.cs:77-92
    Verb = "runas"
});
```

**硬约束**：安装脚本只能由用户显式点击触发，绝不能由 `tasks.json`、watch 触发、远程通道触发；否则"一次 UAC"会被自动化路径滥用。安装状态（已安装/版本/白名单条数）经 `bridge.status` 能力只读暴露给 UI。

### 8.3 卸载

`stop` → `sc.exe delete` → 记录 `allowlist.json` 备份到 `temp/backups/` → 删除 AgentDir（或保留配置供重装）。遵守 SOUL.md 的备份先行。

---

## 9. TaskScheduler 接入

### 9.1 模型

```csharp
// src/Modules/TaskScheduler/Models/TaskDefinition.cs
public class TaskAction
{
    public string File { get; set; } = "";
    public string Args { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = ...;

    /// <summary>非空时走特权桥：填 Agent 白名单里的 capability 名，不再本地 spawn File。</summary>
    public string Bridge { get; set; } = "";

    /// <summary>传给桥的标量参数；键必须命中该 capability 声明的 params schema。</summary>
    public Dictionary<string, string> BridgeParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

`Validate()` 新增：

- `Bridge` 与 `File` **二选一**（都空 → `action.file or action.bridge required`；都填 → 拒绝，避免歧义）；
- `Bridge` 字符集同任务名 `[A-Za-z0-9_-]`，长度 ≤ 64；
- `BridgeParams` 键同上，值长度 ≤ 512，禁止 `\r\n` 与控制字符；
- `Bridge` 与 `Options.Mode=detach`、`killWithHost`、`restart` 组合**允许**（语义由瘦进程承接，见 §9.3）。

**兼容性**：旧版本宿主加载含 `bridge` 的任务会在 `Validate()` 报 `action.file required` → 任务被跳过（fail closed，不会误以普通权限执行）。因此 Agent 安装与宿主升级必须同版本，文档需显著提示。

### 9.2 `TaskRunner.TryStart` 展开

在 `// script wrapping` 之前插入分支（骨架）：

```csharp
string realFile = file;
string realArgs = args;
bool isBridge = task.Action != null && !string.IsNullOrWhiteSpace(task.Action.Bridge);

if (isBridge)
{
    // 复用现有 CommandLine 引号化：路径含空格/引号、参数含 & | 都不会被再解析
    realFile = Environment.ProcessPath;          // CarroDesk.exe 绝对路径，便携模式同样正确
    realArgs = BridgeArgs.Build(task.Action);    // "--bridge-run " + QuoteArgument(name) + " --param k=" + QuoteArgument(v) ...
    workDir = AppDomain.CurrentDomain.BaseDirectory;  // 必须存在，TaskRunner 会校验
}
else if (ext == ".ps1" || ext == ".psm1") { /* 现有脚本包装，原样不动 */ }
// ... 其余分支不动

// 后续 psi / Env / TaskLogger / ProcessJob / BeginOutputReadLine 全部原样复用
```

要点：

- `Env` 只作用于瘦进程，**不会**传给 Agent 侧特权脚本（其环境由白名单固定）；文档明示，避免用户误以为 `action.env` 能改特权环境。
- 不要让 `ScriptResolver` 处理 bridge（`ext` 为空即可走到新分支之前）。
- 日志行会显示 `starting: <exe> --bridge-run clean-temp ...`，与现有任务日志格式一致。

### 9.3 语义映射表

| 现有机制 | 桥接后行为 |
|---|---|
| stdout/stderr → `RunContext.Output` | Agent `stdout/stderr` 事件 → 瘦进程 `Console.Out/Err` → 现有重定向回调，**`options.encoding` 照常生效** |
| 退出码 → `WaitAsync` / retry / 失败通知 | `exit{code}` → `Environment.Exit(code)` → `TaskProcessHandle` 原样拿到 |
| `timeoutSec` 超时 | `Stop()` 杀瘦进程 → 管道断开 → Agent `ProcessJob.Dispose()` 杀整棵特权树 |
| `TryStop` / 宿主退出 `killWithHost=true` | 同上，级联终止 |
| `killWithHost=false` + detach | 瘦进程不挂作业对象、宿主退出后存活，管道不断 → Agent 侧继续运行，语义一致 |
| `BeginExitWatch` / `Exited` / 守护重启 | 瘦进程退出即触发，无改动 |
| `Pid` 显示 | UI 显示的是**瘦进程 pid**；真实特权进程 pid 由 `started` 事件写入任务日志 `[bridge] agent pid=8890` |
| `allowConcurrent` / `singleInstance` | 由瘦进程与现有槽位/互斥体机制保证，无改动 |
| Agent 未安装 / 能力未授权 | 瘦进程 `stderr` 写明确提示并按 §5.2 固定码退出 → 记 fail + 失败通知，**不影响普通任务** |

---

## 10. 安全约束（逐条可验收）

沿用 `docs/IPC-CAPABILITY-EXPOSURE-DESIGN-20260926.md` §11.3 五条硬约束，并补齐本方案特有项：

1. 白名单只放 `C:\ProgramData\CarroDesk\Agent\`，普通用户**只读**；
2. 只接受 `{capability, params}` 结构化请求，永不接受命令行字符串，永不 `cmd /c` 转发；
3. 最小账户 `LocalService`，确实需要 SYSTEM 才升级；
4. 管道双向校验（ACL + `GetImpersonationUserName()`），客户端校验服务端进程路径；
5. Session 0 无 UI；
6. AgentDir / exe / 脚本 ACL 普通用户不可写，启动期逐条校验（防替换）；
7. 服务 DACL 收紧，普通用户不能 `sc config CarroDeskAgent binPath=`；
8. 审计文件普通用户只读，记录 capability、参数摘要、连接者 SID、结果、耗时；`label`/PIN 不落明文口令；
9. 每 capability 超时、并发数、输出字节上限由白名单锁定，客户端不可覆盖；
10. 不把 token / 凭据 / PIN 写进 `tasks.json`；`tasks.json` 里只有 capability 名与标量参数。

---

## 11. 失败与降级

| 场景 | 行为 |
|---|---|
| Agent 服务未安装/未启动 | 瘦进程连管道失败 → `stderr` 提示"运行一次安装脚本" → exit 74 → 任务记 fail |
| capability 不在白名单 | `error 77` → exit 77，审计记拒绝，不做任何降级执行 |
| 参数非法 / 模板占位符注入 | `error 76` → exit 76 |
| 目标脚本 ACL 被放宽 | 启动期标记该 capability 不可用，请求返回 `error 78`，绝不执行 |
| 身份校验失败 | 立即断开 + 审计拒绝，不回任何事件 |
| Agent 崩溃 | SCM 配置 `sc.exe failure` 自动重启；期间瘦进程连接断开 → exit 74，retry/守护按现有语义处理 |
| 管道断开检测不及时 | Agent 侧 250 ms 轮询 + 请求侧 `timeoutSec` 双保险；该点需实机验证 |

---

## 12. 测试计划

- **单元（无需 UAC）**
  - `FrameCodec` 事件编解码、超长帧/坏帧断开；
  - `AllowlistStore`：路径逃逸（`..`、绝对路径、`%VAR%`）、未知 kind、ACL 放宽、参数 schema 越界全部拒绝；
  - `BridgeArgs.Build`：空格、引号、`& | < >`、`^` 的引号化（沿用 `TaskRunnerTests` 的探针断言风格）；
  - `TaskDefinition.Validate()`：bridge/file 互斥、字符与长度、旧配置零迁移、`ExtraOptions` 回写不丢字段；
  - 断开取消：`ProcessJob` 挂上后断开连接，特权探针进程及其子进程确实被回收。
- **集成（需一次 UAC，人工在场）**
  - 安装 → 非提权会话跑 `CarroDesk.exe --bridge-run <capability>` → 验证输出、退出码、审计；
  - 真实任务：`wait` / `detach` / `timeoutSec` / `TryStop` / `killWithHost=false` 各一条，逐项对照 §9.3 映射表；
  - 特权实证：探针脚本写一个只有管理员能写的路径（如 `HKLM\SOFTWARE\CarroDesk\BridgeProbe`），非提权直接跑失败、经桥成功。
- **安全自查**
  - 普通用户尝试改写 `allowlist.json` / 替换 `CarroDesk.Agent.exe` / `sc config binPath` / 用未知 capability / 超长参数 / 冒充其他 SID 连管道，全部应被拒绝；
  - 审计文件普通用户不可写。
- **回归**：现有全量测试（286→291+ 项）保持全绿，普通任务行为零变化。

---

## 13. 分期落地

| 期 | 内容 | 验收 |
|---|---|---|
| P0 | 协议 + `BridgeAgentServer` + `AllowlistStore` + 安装/卸载脚本 + `--bridge-run` 瘦进程（仅 `kind=script`） | 非提权命令行直连桥成功；白名单拒绝用例全绿 |
| P1 | `action.bridge` 模型与 TryStart 展开 + 编辑器「启用特权执行」按钮 + 审计 + 文档/`tasks.sample.json` | §9.3 映射表逐项通过；全量回归绿 |
| P2 | capability 扩展（`service` / 注册表 / `urlacl`）、`bridge.status` 能力（MCP/CLI）、编辑器 capability 下拉 | 能力可对账，PIN 策略按 §11.6 评估 |
| P3 | 后端形态 2（交互用户 + Highest 常驻 helper）+ 无人值守/远程验收 | 服务形态与 helper 形态共用协议，切换只改 `hostMode` |

每期独立可发布；P0/P1 不改变任何现有普通任务的可见行为。

---

## 14. 开放问题（实施前需拍板）

1. **Agent 用同一个 `CarroDesk.exe --agent` 还是独立 `CarroDesk.Agent.exe`？**
   同 exe 省一个项目，`Program.cs` 已证明可在不加载 WPF/不碰互斥体下跑瘦进程；独立 exe 攻击面更小、不依赖 WPF 程序集，但要加 csproj + `slnx` 条目 + 发布打包。倾向**独立 exe**（特权组件应最小化）。
2. `LocalService` 对首批目标脚本是否够用？若脚本依赖用户 DPAPI/HKCU/网络凭据，直接上形态 2。
3. 桥接能力是否需要 PIN 二次确认（`CommandDescriptor.RequiresPin`，§11.6）？本机触发可不加，若将来经远程通道触发必须加。
4. 便携模式（`portable.ini`）下 AgentDir 仍用 `C:\ProgramData\CarroDesk\Agent\`，与便携数据目录的关系需在文档写明：白名单是**机器级**配置，不随便携目录迁移。
5. 服务进程下管道断开的即时性（§5.3）需实测，必要时给瘦进程加显式 `cancel` 帧 + 父进程死亡监视。

---

> 本文档为设计稿，未修改任何代码文件。实施前请确认 §14 的 5 个开放问题与分期范围。
