# CarroDesk 代码与产品审视报告（2026-11-09）

> 审查对象：`c:\Home\Projects\CarroDesk`（v1.4.x，net10.0-windows / WPF）
> 审查方式：先读 README 与 `docs/` 设计/使用/模块开发文档，再逐文件核对实现；对关键结论做了行号级复核。
> 基线事实：`dotnet build CarroDesk.slnx` **0 警告 0 错误**；`dotnet test` **411/411 通过，约 50s**；源码 228 个 `.cs/.xaml`，约 **42,115 行**（其中测试 9,312 行）。
> 本报告只记录问题与建议，**未修改任何源码/文档**。

---

## 0. 结论摘要

**整体判断**：工程底座（Core/Host/Modules 分层、`ModuleBase` 生命周期、IPC 命令内核、原子写配置、411 项测试）质量高于同类个人工具项目；上一轮 review 的 S1/S2/S3/S4/H1/H2/H3/M1–M5 基本可信落地。当前问题不是“架构烂”，而是**失败路径的处理**与**契约一致性**：

1. **任务调度器（TaskScheduler）是本项目风险最高的区域**：状态机计数成对性、配置读取失败处置、异步停止与槽位出册都存在能把“正常运行”推成“永久卡死/数据被覆盖”的路径。
2. **安全与可观测性存在“假象”**：锁屏失败、热键注册失败、配置保存失败大多静默；用户以为功能已生效。
3. **对外接口（MCP/CLI）在常见场景下直接不可用**：宿主未启动时 MCP 会永久缓存空工具表；`services.start/stop` 在能力表里声明 `requiresPin=false`，实际却要 PIN。
4. **中英双语只覆盖了一部分 UI**：任务管理与编辑窗口共 127 处硬编码中文，切到 en-US 仍是中文。
5. **文档与实现出现系统性漂移**（技术栈、打包方式、菜单项、触发器数量、PIN 位数等），会直接误导使用者和后续维护者。

### 最值得优先做的三条改进

| 优先级 | 改进 | 对应问题 |
|---|---|---|
| 1 | **修 TaskScheduler 的“数据安全 + 状态成对”**：读失败不重建空文件；管理窗口暴露 Errors/只读保护；`OnProcessStarted/OnProcessExited` 严格成对；WaitingRetry/异步停止统一收口 | P0-1、P0-2、P1-1~P1-5 |
| 2 | **让失败可见**：锁屏失败（含未配 PIN）、热键注册失败、配置保存失败必须有气泡/日志/菜单态反馈 | P0-3、P1-7、P1-8 |
| 3 | **修 MCP/CLI 契约**：工具表拉取失败不缓存、能力表声明 PIN 策略、客户端读超时、`--help/--version` 与 `--key=value` | P1-6、P1-7、Q35、U11 |

### 严重程度与处置口径

- **严重（P0）**：可能造成数据丢失、安全功能失效或核心功能长期不可用 → **必须修**
- **高（P1）**：明确的功能/体验缺陷或高风险边界 → **必须修**
- **中（P2）**：维护性、一致性、可观测性问题 → **建议优化**
- **低（P3）**：整洁度与锦上添花 → **可选**

---

## 1. 必须修（P0 / P1）

### P0-1【严重】等待模式重试让 `ActiveInstances` 漂移，任务永久卡在“运行中”
- 证据：`src/Modules/TaskScheduler/Services/TaskSchedulerService.cs:642`、`:670`；`Services/TaskStateMachine.cs:87`、`:171`
- 事实：重试循环里每次 attempt 都调用 `fsm.OnProcessStarted(...)`（每次 `ActiveInstances++`），但 `finally` 中整个循环结束只调一次 `OnProcessExited`（只 `--`）。`retry=1` 时首个失败再跑一次，计数变成 2 → 退出后仍为 1。
- 影响：进程已全部退出，FSM 仍为 `Running`（保留旧 PID）。后续所有自动触发被 `TryBeginStarting` 以“任务已在运行”拒绝（`TaskStateMachine.cs:44-47`），列表/托盘看不到进程，只能人工“停止”复位。属于**静默失效**。
- 建议：等待模式不要把每次 attempt 当作新实例；`OnProcessStarted/OnProcessExited` 必须严格成对（每个 attempt 结束即 `OnProcessExited`，或只在最终 attempt 计数）。加一条“retry>0 后进程退出，状态必须回到 Idle”的回归测试。

### P0-2【严重】任务配置存在三条静默数据丢失路径
- 证据：
  - `Services/TaskConfigService.cs:198-202`、`:253-274`：`catch (Exception)` → `RecoverFromCorruptFile` → `AtomicFile.WriteAllText(FilePath, "[]")`。**任何异常**（磁盘瞬时 IO、杀软占用、权限）都被当成“文件损坏”，直接重写为空数组。
  - `Services/TaskConfigService.cs:164-171`：`SchemaVersion > 当前` 时返回空 `Tasks`，文件本身保留但调用方不知道。
  - `Views/TaskManagerWindow.xaml.cs:200-202`、`:496`：`try { Load(); } catch { }` 丢弃 `Errors`，随后任何一次保存执行 `TaskConfigService.Save(_tasks)` **整表覆盖**（`SaveLocked` 无版本/只读保护，`TaskConfigService.cs:291-318`）。
- 影响：① 旧版本打开新版 tasks.json → 显示空列表 → 用户新建一条并保存 → 新版文件被覆盖成 1 条；② 单条任务校验失败被跳过 → 其余任务照常显示 → 保存后失败那条永久删除；③ 瞬时 IO 错误导致任务全没（虽有 `tasks.corrupt-*.json` 备份，但用户第一反应是重建）。
- 建议：
  1. 仅 `JsonException`/结构校验失败时进入恢复；IO 异常保留文件、只上报错误。
  2. `LoadResult` 的 `Errors`、`SchemaVersion`、`ReadOnly` 一路传到窗口：有错误/版本更高时显示横幅、禁用保存。
  3. `Save` 前校验磁盘 mtime/版本，发现外部变化则拒绝覆盖并提示。

### P0-3【严重】锁屏失败对用户完全静默（含“未配置 PIN”）
- 证据：`src/Modules/ScreenLock/ScreenLockModule.cs:306-317`、`:459-467`；`Services/LockController.cs:216-233`
- 事实：`LockSafe()` 在 `Config.Enabled=false`、`Controller==null`、`LockSafe()` 返回 false 时都只是 `return false`；未配置 PIN 时 `LockController` 直接返回 false（唯一出口）；托盘“立即锁定”无条件展示并丢弃返回值，热键路径同样丢弃。
- 影响：未配 PIN（首启向导可跳过）时点“立即锁定”毫无反应，空闲阈值到了也不锁屏；用户会误以为桌面已受保护。**这是安全功能的静默失效**。
- 建议：按原因给反馈——未配置 PIN → 气泡 + 打开设置；钩子/窗口创建失败 → 气泡 + Error 日志；菜单项在未配 PIN 时置灰或改写 Header（“立即锁定（需先设置 PIN）”）。

### P1-1【高】`CatchUpMissed` 在当天已正常运行后还会再补跑一次
- 证据：`TaskSchedulerService.cs:330-345`、`:502`
- 事实：补跑判定只看 `_firedStartupTasks`，而该集合只在 `reason` 以 `startup` 开头时写入；正常 daily 触发（`reason="daily"`）不写。09:00 的任务当天已跑过，10:00 保存任意任务触发 Reload，`now >= 09:00` 且集合里没有它 → 再跑一次，日志 reason 还是 `startup:catchup`。
- 影响：备份/清理/上传类任务被重复执行；日志误导排障。
- 建议：补跑判定与“今天是否已运行”联合（`fsm.LastFinishedAt` 或按天去重），正常触发也记入当日集合。

### P1-2【高】配置重载不清理已删除/已禁用任务的活动实例
- 证据：`TaskSchedulerService.cs:284-317`
- 事实：`ApplyCoreLocked` 只 `StopTriggers()`、替换 `_tasks`、`fsm.Reset()`，从不触达 `_running` 与 `_stateMachines`。
- 影响：手改 tasks.json 删除任务或禁用后重载，原常驻进程仍在跑并占槽；管理窗口没有该行，用户无从停止；同名任务重新添加会被判“已在运行”永久拒绝。
- 建议：Apply 时对不在新列表（或已禁用）的任务执行 `TryStop` 并移除 FSM。

### P1-3【高】等待模式保存时把 `KillWithHost` 强制改为 false，进程脱离宿主生命周期
- 证据：`Views/TaskEditDialog.xaml.cs:550`（`KillWithHost = isDetach && (...)`）；`Services/TaskRunner.cs:344-348`；`TaskSchedulerService.cs:1150`、`:1158-1165`
- 事实：wait 模式（非 detach）永远保存 `KillWithHost=false`，且该复选框在 wait 模式下被隐藏；`TaskRunner` 因此不建 JobObject，宿主退出时走“left running”分支并 `_running.Clear()`。
- 影响：所有经新编辑器保存的 wait 任务在宿主退出后继续存活但不再被跟踪；下次启动可再拉起一份，形成重复实例（wait 模式下单实例互斥也被关闭）。旧编辑器按复选框原值保存，属修复过程引入的回归。
- 建议：去掉 `isDetach &&`，wait 模式也按复选框持久化（默认 true）；或 wait 模式固定 true。

### P1-4【高】“重启”固定等 300ms + 异步 `OnStopCompleted` 与槽位不同步
- 证据：`TaskSchedulerService.cs:420-426`（`Task.Delay(300)` 后 `ExecuteAsync`，并已提前 `return true`）；`:1078`、`:1092-1106`（后台 taskkill 后 `fsmToFinish.OnStopCompleted()`）
- 影响：
  1. 进程树稍顽固（>300ms 很常见）时 FSM 仍为 `Stopping`，`TryBeginStarting` 拒绝，UI 不提示——“重启”点了没反应。
  2. 停止后 `_running` 仍保留带句柄的槽，而 FSM 已复位为 Idle：立刻“运行”会因占槽被拒（`RunManual` 已返回 true）；若新实例在后台回调前启动，回调把 `ActiveInstances` 清 0，进程在跑但 UI 显示空闲、停止按钮不可用。
- 建议：重启等待 `Stopping→Idle`（事件或上限 5s）后再执行，并把被拒结果回传 UI；`OnStopCompleted` 只作用于本次停止的实例（传 slot/代次），出册与状态复位在同一临界区完成。

### P1-5【高】单实例跳过/重启异常后 FSM 永久停在 `WaitingRetry`，且 UI 无法停止
- 证据：`TaskSchedulerService.cs:957-978`；`TaskStateMachine.cs:149`、`:290-301`；`TaskManagerWindow.xaml.cs:274-277`
- 事实：`OnAbandon()` 只处理 `Starting→Idle`，在 `WaitingRetry` 下是空操作；catch 分支只 `RemoveSlot`；`Reset()` 只处理 `MarkedFailed`；`IsRunning` 不含 `WaitingRetry` → 停止按钮永远置灰。
- 影响：槽已出册、没有待执行的重启，但状态是“守护重启延迟中”，所有非人工触发被永久拒绝（`:65-72`），只能手动运行/重启或重启宿主。
- 建议：两条放弃路径统一 `fsm.OnStopCompleted()`（或新增 `OnRestartAbandoned`）；`Reset()`/`TryStop` 能清理 `WaitingRetry`；UI 把该状态纳入可停止。

### P1-6【高】MCP 在宿主未启动时永久缓存“空工具表”
- 证据：`src/Host/Ipc/McpStdioServer.cs:141-150`
- 事实：`EnsureCapabilities()` 拉取失败后执行 `_capabilities = new JObject { ["capabilities"] = new JArray() }` 并缓存；后续 `tools/list` 直接返回缓存，不再重试。
- 影响：MCP 客户端先于宿主启动（很常见）时，模型看到 **0 个工具**，且整个会话不恢复；用户只能重启 MCP 客户端。`docs/USAGE.md:213` 说“客户端无需重启即可自动恢复”，对 MCP 不成立。
- 建议：失败不缓存（或缓存失败状态并带短 TTL），每次 `tools/list` 在缓存为空时重试；若宿主未运行，在工具表为空时返回一条说明性 error/notice 而不是静默空数组。

### P1-7【高】能力表声明 `requiresPin=false`，实际按服务策略要 PIN
- 证据：`src/Modules/ServiceControl/ServiceControlModule.cs:132-133`、`:157-158`；`McpStdioServer.cs:212-214`
- 事实：`services.start/stop` 的 `RequiresPin=false`，但 `RequiresPinFor = PinPolicyFor(...)` 动态要求；`host.capabilities.list` 与 MCP `inputSchema` 只读取 `RequiresPin`。
- 影响：模型/CLI 用户不知道要传 `pin`，调用被 `-32002` 拒绝；若模型反复猜 PIN，会触发 `PinGuard` 全局封锁（5 次后 30s 起），连累 `exit` 等其它需要 PIN 的能力。
- 建议：能力元数据增加 `pinPolicy: "none"|"always"|"per-service"`，`Describe` 与 MCP 描述同步输出；`inputSchema` 为动态 PIN 的能力加入可选 `pin` 字段说明。

### P1-8【高】热键注册失败全程静默，文档却让用户“查日志”
- 证据：`src/Core/ModuleBase.cs:262`（`out _` 丢弃错误）、`:265`（失败直接 return）；`src/App.xaml.cs:329-332`（浮窗热键 `out _`）；`src/Host/Services/HotkeyService.cs:192`（WndProc 吞异常）
- 事实：模块热键与宿主面板热键注册失败既不记日志也不提示；`docs/USAGE.md:208` 声称“任务日志记 `hotkey register failed`”。
- 影响：用户设置的热键被占用时不生效，无任何线索；配合“默认 Win+Alt+C”极易与 NVIDIA/输入法等冲突。
- 建议：失败时 `LogWarning` 并 `ShowBalloon("热键 X 注册失败（可能被占用）")`；配置编辑器保存热键前做一次试注册或至少校验。

### P1-9【高】AppAutoMute 白名单为空时静音所有非前台音频
- 证据：`src/Modules/AppAutoMute/AppAutoMuteModule.cs:167-171`（空名单保护只覆盖黑名单）、`:214-231`（白名单分支 `ContainsProcess(空)=false`）
- 影响：用户切到白名单但还没加应用（或删光）时，除当前前台外所有发声进程被静音（浏览器、会议、系统提示音），表现像“声卡坏了”；设置窗口保存时无任何校验。
- 建议：白名单为空直接 `return`（与黑名单对齐）；保存时提示/强制至少一项；`SetProcessMute` 连续失败限流上报一次。

### P1-10【高】首次运行默认开启锁屏（5 分钟）与开机自启，无知情同意
- 证据：`src/Models/AppSettings.cs:7`（`AutoStart = true`）；`src/Modules/ScreenLock/Models/ScreenLockConfig.cs`（`Enabled=true`、`AutoLockEnabled=true`、`IdleMinutes=5`）；`src/Views/FirstRunWindow.xaml.cs`（向导只设 PIN）
- 影响：新用户首次启动后 5 分钟闲置即被伪锁屏；若刚设的 PIN 记错/忘记，体验会非常糟。开机自启默认开启也属“未征询的持久化”。
- 建议：向导第二步用两个显式开关（“开机自启”“启用闲时锁定”，默认关闭或默认 30 分钟并写明后果）；向导中说明“忘记 PIN 的恢复方式”。

### P1-11【高】安装器切换数据模式时不迁移 `scripts/`，任务引用直接失效
- 证据：`scripts/installer/CarroDesk.iss:486-497`
- 事实：`MigrateDataIfNeeded` 复制 `config.json`/`tasks.json`，并**只创建**空的 `scripts` 目录，未复制其中文件。
- 影响：Roaming ↔ Portable 切换后 `tasks.json` 里的 `scripts/xxx.js` 全部找不到，任务启动失败；日志只报脚本不存在。
- 建议：`FileCopy` 整个 `scripts` 目录（含子目录）后再写标记文件；迁移失败时中止并提示，而不是静默半迁移。

### P1-12【高】修改 PIN 后直接关窗不会保存，但已提示“新 PIN 已生成”
- 证据：`src/Views/ConfigEditorWindow.xaml.cs:206-230`（只写入草稿 `_editing`，并弹“新 PIN 已生成”）、`:234`（关闭）、`:158-189`（`DoSave` 才写回目标并持久化）
- 影响：用户在配置编辑器点“修改 PIN” → 看到成功提示 → 点 X 关闭 → PIN 实际未变；与安全预期冲突（以为已换掉泄露的旧 PIN）。
- 建议：改 PIN 后立即持久化（或关闭时强制提示“PIN 修改未保存”）；至少把文案改为“已生成，保存后生效”。

---

## 2. 工程架构

### 2.1 分层与模块边界（总体合格，有 3 处越界）

| 编号 | 问题 | 证据 | 影响 | 建议 | 级别 |
|---|---|---|---|---|---|
| A1 | 模块依赖宿主命名空间：`CommandErrorCodes` 定义在 `Host/Commands`，模块 `using CarroDesk.Host.Commands` | `src/Host/Commands/CommandErrorCodes.cs:1`；`Modules/ServiceControl/ServiceControlModule.cs:6`；`Modules/Awake/AwakeModule.cs` | 依赖方向反转（模块 → 宿主），无法单独抽出 Core/能力协议程序集 | 把 `CommandErrorCodes`/`CommandDescriptor`/`CommandResult` 全部下沉到 `Core/Commands`，模块只依赖 Core | 中 |
| A2 | 视图命名空间与位置不一致：`LockWindow` 物理在 `Modules/ScreenLock/Views`，`x:Class="CarroDesk.Views.LockWindow"` | `src/Modules/ScreenLock/Views/LockWindow.xaml:1` | 搜索/边界判断困难，易与宿主 `Views` 混淆 | 改为 `CarroDesk.Modules.ScreenLock.Views`，或在 DESIGN 说明例外 | 低 |
| A3 | 抽象放置不统一：`IHotkeyService` 在 Core，`IPinService` 在 `Services/` | `src/Core/IHotkeyService.cs`、`src/Services/IPinService.cs` | 新人难以判断“接口该放哪” | 统一：跨模块契约进 Core，宿主实现留 Host/Services | 低 |

**做得好的部分**：`ModuleContext` 只暴露 `GetService/ShowNotification/RequestTrayRefresh`，模块无法注册服务；`ServiceContainer` 手写 DI 的解析/释放顺序正确；`ModuleManager` 用逐模块 try/catch 隔离故障；托盘与浮窗共用 `MenuProjectionEngine`（虽然入口仍有两份，见 4.1）。

### 2.2 生命周期与线程模型（主要风险集中区）

1. **热键回调在 UI 线程执行**：`HotkeyService` 的 `HwndSource` 在 UI 线程创建，`WM_HOTKEY` 回调同步执行模块 `ClickAction`。任何在回调里做的阻塞（锁屏探测、服务启停、任务启动）都会直接冻结界面。
2. **定时器/触发器大量落在 UI 线程**：Awake 每 5 秒全量 `Process.GetProcesses()`（`AwakeService.cs:673-677`、`:761-770`，上一轮 L13 认领未处理）；TaskScheduler 每次保存都在 UI 线程重建全部 `DispatcherTimer` 并逐个做补跑判定（`TaskSchedulerService.cs:278-282`、`:482`）；ScreenLock 空闲判定在 UI 线程做 ARP+Ping 同步探测（`IpPresenceDetector.cs:41-61`，`ScreenLockModule.cs:220`）。
3. **命令内核在 `Task.Run` 上执行 handler**（`CommandHost.cs`），但 handler 只允许操作非 UI 状态。目前 Awake/Services 都满足，但契约未文档化；未来模块若在 handler 里弹窗/改控件会立刻跨线程异常。
4. **Faulted 模块不可恢复**：`ModuleManager.ReloadAll` 只处理 `Running`；`ModuleStatus.Disabled` 从未被设置（`AboutWindow.xaml.cs:74` 有展示分支）。结果是“模块故障只能重启宿主”“无法禁用不用的模块”。
5. **启动/停止竞态**：`ModuleBase.Start/Stop` 用 `_isStarting/stopRequested` 处理了主要窗口期（H2 已修），但 `SyncManagedHotkeys` 在锁外执行，仍存在与 `Stop` 交叉后热键未注册的窄窗口（无害但状态不自洽）。

**建议**：给 `ModuleBase` 增加 `RestartModule(id)` 与“宿主级禁用”（持久化到 `AppSettings.DisabledModules`），把 `Disabled` 状态真正用起来；为热键回调提供 `RunOnBackground` 的默认封装；把 Awake 枚举、ScreenLock 探测、Task 重建移出 UI 线程。

### 2.3 配置与状态管理

| 编号 | 问题 | 证据 | 影响 | 建议 |
|---|---|---|---|---|
| A4 | 两套重载机制并存：`ConfigManager.ConfigReloaded` **无任何生产订阅者**，实际由 `App.ReloadConfig → Modules.ReloadAll` 驱动 | `Host/Services/ConfigManager.cs:26`、`:41`；`App.xaml.cs:369` | 读者会误以为订阅事件即可热重载；新增模块容易漏接 | 删除事件，或让 `App` 只订阅它并删除 `ReloadAll` 的显式调用；二选一 |
| A5 | 宿主与模块序列化设置不一致：`ConfigService` 用自定义 `SerializerSettings`，`ConfigManager.SaveModuleConfig` 用 `JToken.FromObject` 默认设置 | `Services/ConfigService.cs:171-186`、`:280`；`Host/Services/ConfigManager.cs:119-140` | 同一字段在宿主节与模块节可能序列化不同（枚举/日期/空值）；排查困难 | 统一走一个 `JsonSerializerSettings` 单例 |
| A6 | 模块 Id 与宿主属性名冲突会被静默丢弃（`HostSettingNames` 反射 + 跳过） | `Services/ConfigService.cs:166-169`、`:287` | 未来模块取名叫 `Ipc`/`Language` 时配置永远读不到，且无日志 | 启动时校验保留名并抛错/日志 |
| A7 | 配置无 `schemaVersion`，模块节解析失败返回默认值后可能被下一次保存覆盖 | `Host/Services/ConfigManager.cs:67-75`；`Services/ConfigService.cs:280` | 配置损坏 → 静默重置为默认 | 配置顶层加版本号；解析失败时置只读标记并提示 |

`AtomicFile` 用 `File.Replace` 原子替换（`Common/AtomicFile.cs`），方向正确；但正常路径不产生 `.bak`，且未 `Flush(true)`，断电场景仍有零长度风险（低，可选项）。

### 2.4 错误处理与可观测性

- 静态检索到 **233 处空 catch**（`catch { }` / `catch (Exception) { }`，分布在 62 个文件）。其中释放/清理类可以接受，但相当比例吞掉了用户可感知的失败（配置保存、热键注册、剪贴板监听启动、`SystemEvents` 订阅、文件监听重启等）。
- `LoggerService` 只有 `Info/Warn/Error` 三档，无模块/命令上下文字段；任务日志另有一套 `TaskLogger`。建议统一成 `ILogger` 带 `category`，并至少对“用户主动操作”打一条结果日志。
- IPC 审计只在命令层，UI 操作（保存任务、改 PIN、启停服务）没有等价的审计线索，排障时两条链路对不上。

### 2.5 可扩展性

- 新增模块需要：实现 `ModuleBase` → 在 `ModuleRegistry.RegisterStandardModules` 手工 `new` → 在 `App` 的模块列表里可见。文档 `MODULE-DEVELOPMENT-GUIDE.md §7.6` 写的是在 `App.xaml.cs` 的 `Modules.RegisterModule(new MyFeatureModule())`，**与实现不符**（实际在 `src/Host/Modules/ModuleRegistry.cs`）。
- IPC 能力目前只有 Awake/ServiceControl 两个模块暴露（与 README/DESIGN 一致），但 DESIGN 声称 `ModuleBase` 提供 `GetCommands()`（`DESIGN.md:60` 附近），实际没有该方法，能力注入靠 `ICommandProvider` 单独实现。
- `Rebuild()` 是全有或全无：任一能力重复/非法会让整个能力表回滚为空（`CommandRegistry.cs`），启动期只记日志。建议改为“跳过坏模块 + 汇总错误”，避免一个模块的 bug 让 MCP/CLI 全空。

### 2.6 测试与可测性

- 现有 411 项测试覆盖面不错：IPC 管道、MCP、并发/生命周期、ScreenLock 安全、PIN、配置持久化、UI 渲染冒烟、本地化 key 契约。
- 明显缺口：
  - 托盘/浮窗/配置编辑器/首次向导的交互测试（`ClearAllBindings`、菜单注入、脏检查、PIN 修改未保存）。
  - **MCP 宿主未启动 + 后续恢复**的用例（正好能抓住 P1-6）。
  - **TaskScheduler 数据保护**用例（损坏文件、IO 异常、schema 更新、保存前外部修改）。
  - **能力表 PIN 元数据**一致性测试（P1-7）。
  - XAML 硬编码 CJK 的静态检查（当前 `LocalizationContractTests` 只查 C# `Loc.T` 引用）。
  - `release.py` / 安装器迁移逻辑的无 UI 测试。
- 可测性障碍：`tests/CarroDesk.Tests.csproj` 用 `EnableDefaultCompileItems=false` + 显式 `Compile Include`，新增测试文件必须手动登记，容易漏。
- 技术建议：把 `TaskConfigService.Load/Save` 的“只读/版本”语义做成返回值对象并在纯单元测试覆盖；给 MCP 注入假的 `IPipeClient` 工厂。

### 2.7 构建与发布

- 构建干净（0 警告），版本号由 `Directory.Build.props` 生成（AssemblyVersion 1.4.69.x）。
- `Nullable` 全项目 `disable`，未启用任何 analyzer（`src/CarroDesk.csproj`、`cli`、`tests` 均为默认）。建议至少对 Core/Host 开 `Nullable=enable` + `<AnalysisLevel>latest</AnalysisLevel>`，先 `WarningsNotAsErrors` 渐进。
- Costura 已注释禁用，但 `src/FodyWeavers.xml` 仍留在仓库（死配置文件），`release.py:120` 与 `CarroDesk.Cli.csproj:21-22` 注释仍写 Costura（见第 5 节）。
- 发布为 **framework-dependent**（`--self-contained false`）：目标机必须预装 .NET 10 Desktop Runtime；安装器 `CarroDesk.iss` **没有运行时前置检查**。DESIGN 声称“免安装运行时”不成立。
- CI 未配置（仓库无 workflow）。建议至少加“build + test + 版本校验”三步。

---

## 3. 代码质量

### 3.1 死代码与遗留（可安全删除）

| 编号 | 内容 | 证据 | 说明 |
|---|---|---|---|
| Q1 | **整套旧任务编辑器**：`TaskEditorWindow.xaml.cs` **1,317 行** + `.xaml` 398 行 | `src/Modules/TaskScheduler/Views/TaskEditorWindow.*`；全仓检索仅命中自身定义 | 当前入口是 `TaskManagerWindow → TaskEditDialog`。旧编辑器还停留在旧字段集（无 Env/Encoding/RunAtStartup/CatchUpMissed 控件），一旦被重新挂回会静默丢弃这些字段 |
| Q2 | `ModuleManager.GetAllTrayMenuItems` 仅测试使用 | `Host/Services/ModuleManager.cs` | 生产死代码；其 `GetItemsGuarded` 返回惰性迭代器，try 包不住枚举期异常（潜在 bug） |
| Q3 | `ModuleBase.RequestRefreshTray()` 与 `RequestTrayRefresh()` 双 API | `Core/ModuleBase.cs:329-339` | 历史兼容别名，建议只保留一个 |
| Q4 | `TaskSchedulerService.GetSupervisionState` 私有且无调用；`notify` 参数恒 true | `TaskSchedulerService.cs:993`、`:578` | 死代码 |
| Q5 | `AwakeConfig.CustomPresets` 无读取方，托盘用另一份硬编码 6 档 | `Models/AwakeConfig.cs:33`；`AwakeModule.cs:598` | 配置项“看起来可配但不生效” |
| Q6 | `KeyboardBlocker` 一批未引用 VK 常量（含鼠标键） | `Services/KeyboardBlocker.cs:14-30` | 干扰维护 |
| Q7 | `IClipboardListener.IsListening` 定义+实现，但无消费者 | `Services/IClipboardListener.cs:9` | 监听启动失败无法被上层发现（见 Q9） |
| Q8 | `src/FodyWeavers.xml` | 仓库根 `src/` | Costura 已禁用，配置残留 |

### 3.2 重复代码与超长文件

- 最大文件：`TaskEditorWindow.xaml.cs` 1,317 / `TaskSchedulerService.cs` 1,172 / `TaskConfigService.cs` 778 / `AwakeService.cs` 778 / `AwakeModule.cs` 720 / `MonitorDdcService.cs` 676 / `TaskManagerWindow.xaml.cs` 671。
- **三份“手搓模态输入框”**（暂停分钟/定时目标）：`ScreenLockModule.cs:594-666`、`AwakeModule.cs`（同款）、`MonitorProfileSettingsWindow.xaml.cs:296`。都硬编码 `Brushes.White`、无 Owner、校验在对话框关闭后。建议抽 `PromptDialog`。
- **ServiceControl 的 start/stop 处理各约 35 行**，只有动作字符串不同（`ServiceControlModule.cs:217-253`、`:255-290`）。建议抽 `ExecuteServiceAction`。
- **托盘菜单与浮窗菜单两份宿主项实现**：`Views/TrayContextMenu.xaml.cs` 与 `Views/FloatingPanelWindow.xaml.cs:504-617`。已经分叉（见 4.1）。
- **AudioSwitch 默认模式串/图标判定重复 7 处**（`AudioSwitchModule.cs:88-89`、`:160-161`、`:203-204`、`:273-274`；`Models/AudioSwitchConfig.cs:8,14,15`）。

### 3.3 异常吞掉与静默失败（重点）

除 P0/P1 已列出的，以下也建议处理：

| 编号 | 位置 | 现象 | 级别 |
|---|---|---|---|
| Q9 | `Services/Win32ClipboardListener.cs:27-52`（`:50-52` 空 catch） | 剪贴板监听启动失败无日志/无提示，用户只看到“复制了但没历史” | 中 |
| Q10 | `Services/Triggers/SessionEventTrigger.cs` | `SystemEvents.SessionSwitch` 订阅失败为空 catch，锁屏/解锁触发永不生效 | 中 |
| Q11 | `Services/TaskLogger.cs:141-144`、`:163-179` | `_dirEnsured=true` 永不复位；logs 目录被删后本会话日志全部静默丢弃 | 中 |
| Q12 | `Views/FloatingPanelWindow.xaml.cs:492` | 某个模块 `GetTrayMenuItems` 抛异常 → 整个模块菜单静默消失 | 中 |
| Q13 | `Host/Commands/CommandAudit.cs`（写入路径） | 审计写失败仅内部吞掉，命令仍返回成功（可接受，但应至少 warning） | 低 |
| Q14 | `Modules/ServiceControl/ServiceControlModule.cs:217-290` | 服务不存在/被删/Paused 被映射成 `-32602 InvalidParams`，调用方无法区分“参数错”与“服务不可用” | 中 |

### 3.4 线程安全与并发（逐条复核结果）

| 编号 | 位置 | 问题 | 级别 |
|---|---|---|---|
| Q15 | `ScreenLockModule.cs:24-25`、`:186-189`；`LockController.cs:22`、`:86` | `_sessionLocked`/`_pauseUntil` 由 `SystemEvents` 线程写、UI/空闲线程读，非 volatile；`DateTime` 8 字节非原子 | 中 |
| Q16 | `IpPresenceDetector.cs:41-61`、`:111-120` | 首次探测持锁同步 ARP+Ping(500ms)；`Reset()` 每次用户活动清缓存 → 下一次空闲判定必然同步探测，UI 可卡 ~1s | 中 |
| Q17 | `MonitorDdcService.cs:161-176`、`:212-226` | `break` 判定与 `finally` 复位分处两个临界区；快速连按丢步 | 中 |
| Q18 | `AwakeService.cs:424-428`、`:452-456` | IPC 线程无锁写共享 `_config.Mode/ExpireAtLocal`，UI 线程读 | 中 |
| Q19 | `ProfileScheduleEngine.cs:103-107`、`:195`、`:218-221` | Stop 时 `Cancel()+Dispose()` CTS，任务体可能还没执行到 `Task.Delay(cts.Token)` → `ObjectDisposedException` 未被 catch 链接住 | 中 |
| Q20 | `MonitorDdcService.cs:269-287`、`:344-352` | 外层 `GetLastWin32Error` 取到中间调用残留值，可能误判为“休眠/未就绪”而跳过 WMI 回退 | 低 |
| Q21 | `ClipboardHistoryService.cs:95-104` vs `:114-124` | 敏感词过滤早于自环抑制，`_suppressedHash` 未被消费，2 秒内同内容真实复制被误抑制 | 低 |

### 3.5 资源释放

| 编号 | 位置 | 问题 | 级别 |
|---|---|---|---|
| Q22 | `TaskEditDialog.xaml.cs:375`、`:410-421` | detach“测试运行”句柄被覆盖且从不 `Dispose`；`Stop()` 在 UI 线程同步 `taskkill + WaitForExit(5000)`，界面可冻结 5s | 中 |
| Q23 | `TaskProcessHandle.cs:203` | 停止时在进程尚未确认退出就 `mutex.Dispose()`，`killWithHost=false` 的常驻任务存在短暂双实例窗口 | 低 |
| Q24 | `NamedPipeCommandServer.Dispose` | 只 `Cancel()`，不 `Dispose` CTS、不等待连接任务结束 | 低 |
| Q25 | `ClipboardHistoryModule.cs:114-128`；`JsonClipboardHistoryStorage.cs:190-200` | 退出时 `Flush()` 最多阻塞 5s；写线程可能在 `_idle` 释放后 `Set()` 抛未观察异常 | 低 |
| Q26 | `HostCommands.Status` | `Process.GetCurrentProcess()` 未 `Dispose`（有速率限制，影响很小） | 低 |

### 3.6 潜在缺陷与边界条件

| 编号 | 位置 | 问题 | 级别 |
|---|---|---|---|
| Q27 | `TaskDefinition.cs:388`；`TaskRunner.cs:211-215` | `Action.File="sub/a.js"` 时 `EffectiveWorkDir()` 返回相对 `"sub"`（非空），跳过“用解析后全路径兜底”，`Directory.Exists("sub")` 相对宿主 CWD 通常为 false → 必然启动失败 | 中 |
| Q28 | `TaskEditDialog.xaml.cs:500`、`:508`、`:547-554` | 数值非法静默回退（"30 秒"→5，"-5"→0=永不超时），保存成功但行为相反 | 中 |
| Q29 | `TaskEditDialog.xaml.cs:423-438` | 点 X 关闭不检查 `_isDirty`（只有“取消”检查），编辑内容静默丢失 | 中 |
| Q30 | `TaskManagerWindow.xaml.cs:308-313` | 总开关保存失败不回滚复选框，界面显示“已启用”但触发器未启动 | 中 |
| Q31 | `TaskManagerWindow.xaml.cs:587` | 间隔任务摘要用 `Trigger.Every`，`everySec` 形式显示为“间隔 ” | 低 |
| Q32 | `TaskConfigService.cs:301-314` | 保存白名单只写 6 个字段，手写文件里的自定义字段在 UI 保存后消失 | 低 |
| Q33 | `IPC / McpStdioServer.cs:104` | `initialize` 原样回显客户端请求的 `protocolVersion`，未做版本协商，可能声称支持不支持的版本 | 中 |
| Q34 | `Common/ControlArgs.cs:60-105` | 不支持 `--key=value`，会被解析成 `params["key=value"]=true`，服务端报 `unknown param`，用户难以理解 | 中 |
| Q35 | `Host/Ipc/PipeRpcClient.cs:37`、`:43` | `--timeout` 只作用于 Connect；`ReadFrame` 无超时/取消。宿主卡死时 CLI/MCP **永久挂起** | 中 |
| Q36 | `NamedPipeCommandServer` | `MaxAllowedServerInstances=-1` 且无空闲超时，同用户进程可无限开连接占任务/缓冲 | 低 |
| Q37 | `ScreenLockModule.cs:330-346`、`:472-490` | 选档位不置 `AutoLockEnabled=true`；自动锁定关闭时“5 分钟”仍显示勾选，与“启用自动锁定”未勾选自相矛盾 | 中 |
| Q38 | `AppAutoMuteSettingsWindow.xaml.cs:135` vs `:306` | 滑杆显示值四舍五入到 50 的整数倍，落盘用原始值（显示 1050 实存 1047） | 低 |
| Q39 | `MonitorProfileModule.cs:228-237` | 亮度预设硬编码对比度 70，覆盖用户设定，菜单却只写亮度 | 中 |
| Q40 | `src/Modules/Awake/AwakeModule.cs:644` | “下班”目标时刻 18/20/22 硬编码 | 低 |

### 3.7 命名与一致性

- `ModuleStatus.Created / Initialized / Running / Stopped / Faulted / Disabled` 中 `Disabled` 永远不出现；`GetModuleStatus(未知Id)` 返回 `Created`，CLI 会显示“创建”状态，语义奇怪。
- 布尔语义混用：`LockWithHost`/`KillWithHost`、`IsLocked`（浮窗“位置锁定”）与 `ScreenLock.IsLocked`（桌面锁屏）在不同上下文同名不同义。
- `docs/` 中 `TaskEditorWindow` 仍被描述为当前编辑器（`AUTORUN-DESIGN.md:217`），与实现（`TaskEditDialog`）相反。

---

## 4. 界面与人机交互 / 易用性

### 4.1 信息架构：托盘与浮窗是两套菜单

- 证据：`src/Views/TrayContextMenu.xaml:120-160`（有“关于”，无“语言”）；`src/Views/FloatingPanelWindow.xaml.cs:569-602`（有“语言”，无“关于”）。`docs/USAGE.md:29` 仍把“语言”写在托盘菜单里。
- 影响：同一功能在两处入口不一致；用户找不到切换语言/关于；文档与实物对不上。
- 建议：把宿主通用项抽成一份 `HostMenuItems` 供两处渲染（项目里已有 `HostMenuActions`，只差 UI 投影统一），或在两处都保留同一组项。

- 证据：`TrayContextMenu.xaml:145` 硬编码 `InputGestureText="Win+Alt+C"`。
- 影响：用户改了 `FloatingPanelHotkey` 后托盘仍显示旧组合，产生“按了没反应”的错觉。
- 建议：从 `ConfigManager.Current.FloatingPanelHotkey` 绑定，或干脆只显示 Tooltip。

### 4.2 各设置窗口的校验与反馈不一致

| 窗口 | 现状 | 建议 |
|---|---|---|
| Awake | 热键不做 `HotkeyHelper.Validate`（`AwakeSettingsWindow.xaml.cs:294`），其它模块都校验 | 统一校验 + 行内错误，而不是 MessageBox |
| ScreenLock | 数字输入非法静默沿用旧值/默认值（`ScreenLockSettingsWindow.xaml.cs:310-328`）；勾选设备感知但不填 IP 可保存 | 校验失败阻止保存；IP 必填联动 |
| AppAutoMute | 白名单可为空且保存成功（P1-9） | 保存时确认或阻止 |
| TaskScheduler | 数值静默回退、无脏检查、错误只弹 MessageBox | 统一 `Validate()` + 行内错误 + 脏提示 |
| ConfigEditor | 无脏检查；改 PIN 未保存即关闭（P1-12）；“恢复默认”只恢复自启 | 脏提示；PIN 即时生效；“恢复默认”覆盖全部可恢复项或改名 |

### 4.3 锁屏与口令流程

- 锁屏失败静默（P0-3）是首要问题。
- 锁屏窗口：低级键盘钩子只放行数字/退格/回车/Shift，符合“纯数字 PIN”设计；但钩子回调在 UI 线程，UI 卡顿超过 `LowLevelHooksTimeout` 时系统会跳过钩子（`KeyboardBlocker.cs:99-133`），Win/Alt 组合可落到覆盖层下方。建议缓存 Win/Alt 状态、减少回调内 P/Invoke，并补充 `ClipCursor`/任务栏禁用等防线。
- `KeyboardBlocker.Remove()` 忽略 `UnhookWindowsHookEx` 返回值（`KeyboardBlocker.cs:78-83`）——解除失败时用户表现为“键盘失灵”，日志空白。
- 多显示器：`LockWindow` 混用 DIP（WPF `Left/Width`）与物理像素（`EnumDisplayMonitors` + `SetWindowPos`），在混合缩放的多屏上可能首帧错位/漏盖（`LockWindow.xaml.cs:76-79`、`:123`）。建议统一用物理像素 `SetWindowPos`，并在 125%/150% 双屏实机验证。
- 无障碍：锁屏窗口的 PIN 框缺少 `AutomationProperties.Name`；错误提示只靠文本颜色/MessageBox。建议补 `AutomationProperties`、`LabeledBy`，并支持 Tab 顺序。

### 4.4 多语言与文案

- **硬编码中文**（静态统计，排除 `{loc:Loc ..., Default='…'}` 这类有本地化绑定的默认值）：
  - `Modules/TaskScheduler/Views/TaskEditDialog.xaml`：**83 处**
  - `Modules/TaskScheduler/Views/TaskManagerWindow.xaml`：**44 处**
  - `Views/AboutWindow.xaml`：3 处（部署模式等）
  - `Modules/AudioSwitch/Views/AudioSwitchSettingsWindow.xaml`：2 处（设计时默认值）
  - `Modules/ClipboardHistory/Views/ClipboardHistoryWindow.xaml`：1 处
- 代码层：`TaskManagerWindow.xaml.cs:378-509` 十余条 MessageBox 全中文；`AboutWindow.xaml.cs:127` 复制失败提示中文；`FloatingPanelWindow` 语言菜单项本身是硬编码 “Auto / zh-CN / en-US”（可接受）。
- IPC/MCP 元数据全中文：`CommandDescriptor.Summary/Params.Description`（如 `ServiceControlModule.cs:109-141`），英文用户与英文模型体验差。建议能力元数据用双语或英文 + 本地化展示层。
- CLI 输出为英文、GUI 为中文，属可接受约定，但 CLI 错误信息直接暴露异常文本（见 4.5）。
- 建议：加一条静态检查（CI 或单测）扫描 XAML 中“不含 `loc:` 的中文字面量”，把已存在的加入白名单，防止新增；然后分两批补齐 TaskScheduler 的 key。

### 4.5 CLI 与 MCP 的人机可用性

| 编号 | 问题 | 证据 | 影响 | 建议 |
|---|---|---|---|---|
| U11 | 无 `--help/-h/--version`；无参运行先输出 `not a ctl invocation` 再打 usage | `cli/CarroDesk.Cli/Program.cs:30-45` | 新用户第一印象差，无法查版本 | 识别 help/version，usage 带退出码与示例 |
| U12 | 宿主未运行时仅返回 `-32020 pipe transport failed: <原始异常>` | `Host/Ipc/ControlForwarder.cs`（错误映射） | 用户不知道“去启动 CarroDesk.exe” | `-32020` 专门提示“宿主未运行，请先启动托盘程序” |
| U13 | `--timeout` 语义与实现不符（只连不读，见 Q35） | `Common/ControlArgs.cs`、`PipeRpcClient.cs:37-43` | 用户以为设了整体超时 | 实现整体截止时间，或改名为 `--connect-timeout` |
| U14 | 能力发现靠记忆（隐藏的 `host.capabilities.list`） | CLI usage | 不读文档就无法用 | `ctl help` / `ctl list` 子命令 |
| U15 | MCP `instructions` 声称“锁屏/音频/唤醒/任务/服务”，实际 IPC 只有唤醒/服务 | `McpStdioServer.cs`（instructions 字符串） | 模型会尝试不存在的能力 | 文案改为“以 tools/list 为准”，或补齐能力 |
| U16 | `tools/call` 的 `pin` 不在 `inputSchema` 中（仅描述文字提到） | `McpStdioServer.cs:212-214` | 部分客户端会丢弃未声明参数 | schema 中声明可选 `pin`（配合 P1-7） |

### 4.6 首次使用与文档引导

- 向导只有一步“设 PIN”，没有模块/权限/自启说明；取消向导 → `Shutdown(0)`，无解释（`App.xaml.cs` 启动段）。
- 默认开启锁屏 + 自启（P1-10），却没有“为什么/如何关闭”的说明。
- 忘记 PIN 的恢复路径在 `docs/USAGE.md` 中没有明确入口（建议写“删除 config.json 的 PinSalt/PinHash 后重启”，并在向导中提示）。
- 配置编辑器暴露的是原始 `config.json` 字段（含 `Ipc`、`ModuleConfigs`），对普通用户不友好；建议提供“基础/高级”分区或直接指向各模块设置窗口。

### 4.7 无障碍与 DPI

- `app.manifest` 只声明系统级 DPI 感知（非 PerMonitorV2）。多屏不同缩放时 WPF 位图拉伸、托盘浮窗定位偏移；建议改为 `<dpiAwareness>PerMonitorV2</dpiAwareness>` 并逐窗口验证。
- 浮窗“Tray”定位固定用 `SystemParameters.WorkArea`（主屏工作区，`FloatingPanelWindow.xaml.cs:270`、`:322`）：任务栏在副屏时面板出现在主屏——上一轮 L22 仍存在。建议用 `MonitorFromPoint` + 托盘图标所在屏的 `rcWork`。
- 图标按钮（浮窗）只有 ToolTip，没有 `AutomationProperties.Name`；`🔒/🔓` emoji 在部分字体/高对比度主题下不可读。建议改用矢量图标 + 文本可访问名，并支持高对比度主题。
- 设置窗口普遍缺键盘焦点顺序与 Esc 关闭/Enter 保存。

---

## 5. 文档与实现一致性（逐条）

| 编号 | 文档位置 | 文档说法 | 实际实现 | 建议 |
|---|---|---|---|---|
| D1 | `README.md:12` | “11 种触发器” | `TaskTriggerType` 共 10 种（`TaskDefinition.cs:11-20`） | 改为 10，或补齐第 11 种 |
| D2 | `README.md:41` | CLI 为“Costura 单文件” | Costura 已注释；CLI 用 `PublishSingleFile` | 更正表述 |
| D3 | `README.md:42` | “NSIS 安装包（scoop install nsis）” | 实际 Inno Setup 6（`scripts/installer/CarroDesk.iss`），`CHANGES-20261003.md:47` 已记录替换 | 更正为 Inno Setup |
| D4 | `README.md:55` | “近期变更日志”指向 `CHANGES-20260926.md` | 最新为 `CHANGES-20261109.md` 等 | 改为指向 CHANGES 目录或最新一篇 |
| D5 | `docs/DESIGN.md:5` | “.NET Framework 4.8 + WPF（C# 7.3）” | `net10.0-windows` / C# latest | 更正（README 已在上一轮修正，DESIGN 漏改） |
| D6 | `docs/DESIGN.md:42` | `CarroDesk.Cli.exe`（Costura 单文件） | 同 D2 | 更正 |
| D7 | `docs/DESIGN.md:43` | “单元测试（291 通过）” | 411 通过 | 去掉硬编码数字或改为“见 CI” |
| D8 | `docs/DESIGN.md:81` | 防抖 250ms；顶层含“语言”；“退出（需验 PIN）” | `DebounceMs=150`（`DynamicTrayController.cs:25`）；语言项已移除；仅模块 guard 阻止时才验 PIN | 三条分别更正 |
| D9 | `docs/DESIGN.md:57` | `Disabled`（`config.Enabled=false` 跳过启动） | 没有任何代码把模块置为 `Disabled`；`config.Enabled` 是模块内部开关 | 要么实现宿主级禁用，要么更正描述 |
| D10 | `docs/DESIGN.md:175` 附近 | “Win10/11 免安装运行时” | framework-dependent 发布，需要 .NET 10 Desktop Runtime | 更正，并在安装器加前置检查 |
| D11 | `docs/DESIGN.md:60` 附近 | `ModuleBase` 提供 `GetCommands()` | 无此方法；能力靠实现 `ICommandProvider` | 更正 |
| D12 | `docs/USAGE.md:29-30` | 托盘含“语言”；“退出 ← 需验证应用 PIN” | 语言项已移除（`CHANGES-20261008.md:78`）；退出仅在模块 guard 阻止时验 PIN | 更正 |
| D13 | `docs/USAGE.md:204` | 首次运行设“6 位数字” PIN | `FirstRunWindow` 只要求 ≥4 位、≤16 位（纯数字） | 改为“4–16 位数字”，或实现固定 6 位 |
| D14 | `docs/USAGE.md:208` | 热键冲突“任务日志记 `hotkey register failed`” | 模块/宿主热键失败不记日志（P1-8） | 先修代码，或改文案 |
| D15 | `docs/USAGE.md:213` | “客户端无需重启即可自动恢复” | CLI 可恢复；MCP 缓存空表不恢复（P1-6） | 补充 MCP 需重启客户端 |
| D16 | `docs/MODULE-DEVELOPMENT-GUIDE.md §7.6` | 在 `App.xaml.cs` 调 `Modules.RegisterModule(new ...)` | 实际在 `src/Host/Modules/ModuleRegistry.cs` 的统一注册处 | 更正，并补“新增模块检查清单” |
| D17 | `scripts/release.py:120` | docstring 写 Costura | 同 D2 | 更正 |
| D18 | `cli/CarroDesk.Cli/CarroDesk.Cli.csproj:21-22` | 注释写 Costura 内嵌 | 同 D2 | 更正 |
| D19 | `scripts/installer/CarroDesk.iss:262-267`、`:297-301`、`:527-535` | `[Code]`/目录标签硬编码中文 | 安装器声明了中英双语，英文向导仍显示中文错误/标签 | 抽 `ExpandConstant('{cm:...}')` 或按 `ActiveLanguage` 分支 |

---

## 6. 测试覆盖建议（按投入产出排序）

1. **TaskScheduler 数据保护**（最高优先）：损坏 JSON、只读文件、`SchemaVersion` 更高、保存前外部修改、单条任务非法 → 断言不覆盖、不删除、错误可见。
2. **TaskScheduler 状态机不变量**：`ActiveInstances` 与真实句柄数一致；retry、restart、cancel、配置删除后均回到 Idle；`WaitingRetry` 可停止。
3. **MCP 恢复路径**：宿主未启动 → `tools/list` 为空 → 启动宿主 → 再次 `tools/list` 必须出现工具。
4. **能力元数据契约**：`RequiresPin`/`RequiresPinFor`、`pinPolicy`、`inputSchema` 三者一致。
5. **UI 冒烟扩展**：`ConfigEditorWindow` 的脏检查/PIN 修改、托盘与浮窗菜单项集合一致、热键输入校验。
6. **本地化静态检查**：XAML 中无 `loc:` 的 CJK 字面量白名单化。
7. **发布脚本**：`release.py` 的 zip 内容（不含 pdb/config）、安装器迁移函数纯逻辑化后单测。

---

## 7. 改进路线（建议分三批）

### 第一批（止血，优先于任何新功能）
1. P0-2 任务配置：只对 JSON 结构错误恢复；Errors/版本只读；保存前外部修改检测。
2. P0-1 / P1-4 / P1-5：状态机计数成对、异步停止收口、WaitingRetry 可停止/可清理。
3. P0-3 / P1-8：锁屏失败、热键注册失败 → 气泡 + 日志 + 菜单态。
4. P1-6 / P1-7：MCP 空表不缓存；能力表输出 PIN 策略。
5. P1-9 / P1-3：白名单空保护；wait 模式 `KillWithHost` 回退。
6. P1-12：PIN 修改即时生效。
7. P1-11：安装器迁移 `scripts/`。

### 第二批（体验与一致性）
1. 任务管理与编辑器全面本地化（127 处），加 XAML CJK 静态检查。
2. 托盘/浮窗菜单合并为一份宿主项；移除硬编码 `Win+Alt+C`。
3. 设置窗口统一校验/错误提示/脏检查；抽 `PromptDialog`。
4. 首次运行向导：自启、锁屏、忘记 PIN 说明。
5. CLI：`--help/--version`、`--key=value`、宿主未运行提示、`ctl list`。

### 第三批（架构与债务）
1. 删除 `TaskEditorWindow`（1,317 行 + 398 行 XAML）、`FodyWeavers.xml` 及 Q2–Q8 死代码。
2. `CommandErrorCodes` 等契约下沉 Core；`IPinService` 归位；配置序列化统一；删除 `ConfigReloaded` 死事件。
3. 启用 `Nullable` + analyzer（Core/Host 先行），逐步清理警告。
4. UI 线程减负：Awake 枚举、ScreenLock 探测、Task 重建移出 UI 线程。
5. 安装器 .NET 10 运行时前置检查；CI（build + test + 版本校验）。
6. 统一更正 D1–D19 文档漂移，并在 PR 模板中加“文档是否同步”检查项。

---

## 8. 与上一轮 review（2026-10-25）的关系

| 上一轮项 | 本轮结论 |
|---|---|
| S1/S2 PIN 链路、S3 四字段持久化、S4 特权名 | 已落地，字段读写对称 |
| H1 编辑器字段透传、H2 启动期停止竞态、H3 taskkill 转后台 | 已落地；H3 引出 P1-4 的 300ms 固定等待 |
| M1/M2/M5 状态机 | 已落地；`OnAbandon` 在 `WaitingRetry` 下无效（P1-5） |
| M3 启动补跑 | 已落地，但引入 P1-1 重复补跑 |
| M4 detach 测试句柄 | 部分落地：可停止但不 Dispose、可被覆盖（Q22） |
| M7 AppAutoMute 重载残留 | 仅设置窗口路径修复；`OnConfigReloaded` 仍残留（见 3.3/Q9 同类） |
| M8 剪贴板总量预算 | 仍存在：`MaxTextLength=1,000,000` × `MaxItems=1000`，无总字节上限 |
| L7 `StopTriggers` 同步 `Invoke` 无保护 | 仍存在（`TaskSchedulerService.cs:477`） |
| L13 Awake UI 线程枚举 / L14 Awake 热键不校验 | 仍存在 |
| L16 DDC TOCTOU / L17 LastError / L18 CTS / L19 默认静音 / L22 浮窗主屏定位 | 均仍存在（对应 Q17、Q20、Q19、P1-9、4.7） |
| L8/L9/L10 锁屏线程与钩子 | 仍存在（Q15、Q16、4.3） |
| L25 README 技术栈 | 已修正；DESIGN 漏改（D5） |

> 结论：上一轮“高/严重”项基本修复，但**修复过程引入了 2 个新问题**（P1-1 重复补跑、P1-3 `KillWithHost` 回归）；上一轮“中/低”遗留几乎全部仍在。建议以后每个修复项都配一条回归测试，并在 `docs/CHANGES-*` 中记录“未处理原因”。

---

## 附录 A：可直接落地的关键代码位置索引

| 主题 | 文件 | 关键行 |
|---|---|---|
| 任务重试计数漂移 | `src/Modules/TaskScheduler/Services/TaskSchedulerService.cs` | 642、670 |
| 任务加载恢复（重写 `[]`） | `src/Modules/TaskScheduler/Services/TaskConfigService.cs` | 198-202、253-274 |
| 管理窗口吞错误/整表覆盖 | `src/Modules/TaskScheduler/Views/TaskManagerWindow.xaml.cs` | 200-202、496 |
| 等待模式 `KillWithHost` 回归 | `src/Modules/TaskScheduler/Views/TaskEditDialog.xaml.cs` | 550 |
| 重启固定 300ms | `src/Modules/TaskScheduler/Services/TaskSchedulerService.cs` | 420-426 |
| 异步停止/槽位不一致 | 同上 | 1078、1092-1106 |
| 锁屏静默失败 | `src/Modules/ScreenLock/ScreenLockModule.cs` | 306-317、459-467 |
| 锁屏未配 PIN 出口 | `src/Modules/ScreenLock/Services/LockController.cs` | 216-233 |
| MCP 空表缓存 | `src/Host/Ipc/McpStdioServer.cs` | 141-150 |
| MCP PIN 契约 | `src/Host/Ipc/McpStdioServer.cs`；`src/Modules/ServiceControl/ServiceControlModule.cs` | 212-214；132-133、157-158 |
| 热键失败静默 | `src/Core/ModuleBase.cs`；`src/App.xaml.cs` | 262、265；329-332 |
| 白名单空全静音 | `src/Modules/AppAutoMute/AppAutoMuteModule.cs` | 167-171、214-231 |
| 安装器迁移丢脚本 | `scripts/installer/CarroDesk.iss` | 486-497 |
| 浮窗 Tray 定位主屏 | `src/Views/FloatingPanelWindow.xaml.cs` | 270、322 |
| 托盘/浮窗菜单分叉 | `src/Views/TrayContextMenu.xaml:145`；`src/Views/FloatingPanelWindow.xaml.cs:569-602` | — |
| 任务 UI 硬编码中文 | `src/Modules/TaskScheduler/Views/TaskEditDialog.xaml`（83）、`TaskManagerWindow.xaml`（44） | — |
| 旧任务编辑器死代码 | `src/Modules/TaskScheduler/Views/TaskEditorWindow.xaml.cs`（1,317 行）+ `.xaml`（398 行） | — |

## 附录 B：复现与验证方法（建议加入回归测试）

1. **重试计数**：创建 `wait` 模式任务，脚本首次失败、重试成功；等待进程退出后查询任务状态，应回到 Idle。当前会出现“运行中”且无 PID。
2. **配置保护**：把 `tasks.json` 设为只读或指向不可写目录 → 打开任务管理器 → 新建并保存；当前可能显示空列表并尝试覆盖（可用 `tasks.corrupt-*` 备份核对）。
3. **MCP 恢复**：先启动 MCP 客户端（宿主未运行），执行 `tools/list` → 启动宿主 → 再 `tools/list`。当前始终为空。
4. **锁屏反馈**：删除 `config.json` 中 `PinSalt/PinHash`（或跳过向导）→ 点托盘“立即锁定”。当前无任何提示。
5. **热键冲突**：把 `FloatingPanelHotkey` 改成已被占用的组合（如 `Win+Alt+C` 先被其它程序注册），重载配置。当前无提示、无日志。
6. **安装器迁移**：在 Roaming 模式创建 `scripts/hello.js` 并被任务引用 → 重装并选择 Portable。当前 `tasks.json` 迁移但脚本缺失。
