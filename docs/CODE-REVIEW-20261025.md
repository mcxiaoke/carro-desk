# CarroDesk 代码审查报告（2026-10-25）

> 本报告是继 `CODE-REVIEW-20261003.md`（含四轮修复进展）之后的又一轮全面独立审查。
> 审查基线：master @ 7ae5097（feat(clipboard): keyword-based sensitive content filter）。
> 覆盖范围：src 全部 8 个模块 + 宿主核心（App/Core/Host/Services/Common）+ cli，共约 8.7 万行（含 XAML）；`obj/`、`bin/` 排除。
> 审查方式：三个独立视角（TaskScheduler 模块专审 / 其余七模块专审 / 宿主核心与 IPC 内核专审）逐文件通读，严重级别发现均经第二人复核原始代码确认；一项 P/Invoke 行为做了本机实测。此前已修复并记录于旧报告的问题不再重复列出。

---

## 一、总体评价

代码库整体质量**显著高于常见的同规模桌面工具项目**：模块化生命周期状态机严谨（`ModuleBase` 的启停幂等/逆序清理）、IPC 命令内核的校验链设计清晰（存在性→模块态→参数→口令→限流→执行，超时可取消）、`AtomicFile` 的临时文件+替换+回滚+自愈、`ConfigService` 的"候选快照→完整成功后提交"事务模型、`PinGuard` 的"锁内预留名额→锁外慢验证→锁内结算"并发模型、`ServiceControl` 的白名单+DACL+fail-safe 分层，都是可直接作为范本的实现。构建保持 0 警告 0 错误。

本轮发现的问题集中在三条主线：

1. **PIN 凭据链路存在两个独立的高危缺陷**（S1、S2）：一个让"修改 PIN"静默失效（旧 PIN 继续有效、新 PIN 反而无法解锁），一个让含字母的 PIN 在锁屏后永久无法输入。两者叠加"忘记 PIN"场景的恢复成本极高。
2. **TaskScheduler 的 Batch-2 功能端到端断裂**（S3、M3）：10 月新增的 encoding / runAtStartup / catchUpMissed / env 四个字段 UI 已可设置、模型已可消费，唯独持久化层没有接上——保存即丢；且补跑语义实现为"每次重载都重跑"。
3. **跨线程状态写入了缺少同步**（S4、H4、M6 等）：Awake 模块两处（特权名传参错误 + IPC 线程无锁写状态）、LockController 日志器构造期求值、TaskScheduler 启动窗口期停止竞态。

---

## 二、问题汇总

| 级别 | 编号 | 位置 | 问题 |
|---|---|---|---|
| 严重 | S1 | `Services/HostPinService.cs:55-58,83-88` | 修改 PIN 后 pending 状态分叉：新 PIN 验证失败、旧 PIN 继续有效，锁屏可能被新 PIN 锁死 |
| 严重 | S2 | `Modules/ScreenLock/Services/KeyboardBlocker.cs:85-97` | 键盘钩子白名单只放行数字，PIN 设置不限制字符集，含字母 PIN 锁屏后永久锁死 |
| 严重 | S3 | `Modules/TaskScheduler/Services/TaskConfigService.cs:365-384,463-572` | Batch-2 字段（encoding/runAtStartup/catchUpMissed/env）持久化断裂，保存即丢 |
| 严重 | S4 | `Modules/Awake/Services/AwakeService.cs:52,119` | LookupPrivilegeValue 传 GUID 字符串而非特权名，SE_SHUTDOWN_NAME 启用恒失败，延迟睡眠静默失效 |
| 高 | H1 | `Modules/TaskScheduler/Views/TaskEditDialog.xaml.cs:104-107,505-521` | 新编辑器编辑既有任务静默丢失 stableUptimeSec 与 options.workDir |
| 高 | H2 | `Modules/TaskScheduler/Services/TaskSchedulerService.cs:594-602,994-1008` | 启动窗口期停止竞态：进程照常启动且脱离注册表，不可见不可停 |
| 高 | H3 | `Modules/TaskScheduler/Services/TaskProcessHandle.cs:301-308` | UI 线程同步 taskkill（每实例最长 5s），停止/删除/退出冻结界面 |
| 高 | H4 | `Modules/Awake/Services/AwakeService.cs:406-407,461-464,600` | IPC 命令在线程池无锁改写 Awake 状态，与 1s tick 竞争，最坏立即睡眠/关机 |
| 中 | M1–M9 | 见第四节 | 状态机计数漂移、熔断计数冲刷、补跑语义、detach 测试句柄、死槽泄漏、锁屏日志丢失、静音残留、剪贴板无总量预算、宿主半初始化 |
| 低 | L1–L20 | 见第五节 | 清单式列出 |

---

## 三、严重问题（详述）

### [严重] S1. 修改 PIN 后凭据分叉：新 PIN 无效、旧 PIN 继续有效，锁屏可能被新 PIN 锁死

**位置**：`src\Services\HostPinService.cs:55-58, 83-88`；`src\Views\ConfigEditorWindow.xaml.cs:206-232`；`src\App.xaml.cs:117-121`

**证据**（三段代码合起来构成完整链条）：

```csharp
// HostPinService.VerifyCore —— pending 优先于磁盘配置：
if (!string.IsNullOrEmpty(_pendingSalt) && !string.IsNullOrEmpty(_pendingHash))
{
    _inner.SetFromConfig(_pendingSalt, _pendingHash);   // 只要有 pending，永远用 pending
}
else { BuildFromCurrent(); }

// HostPinService.SetNewPin —— 首次运行向导会走到这里：
public void SetNewPin(string pin)
{
    _inner.SetNewPin(pin);
    _pendingSalt = _inner.Salt;
    _pendingHash = _inner.Hash;      // pending 被置位后，全仓没有任何代码再清除它
}

// ConfigEditorWindow.OnPinChangeClick —— 修改 PIN 不经过 HostPinService：
byte[] salt = PinService.GenerateSalt();
_editing.PinSalt = Convert.ToBase64String(salt);
_editing.PinHash = PinService.ComputeHash(salt, newPin);   // 只写 UI 草稿 → Save 落盘
```

**触发条件**：首次运行向导设置过 PIN（pending 置位，且落盘后 pending 也从不清除）→ 之后在"配置编辑器"里修改 PIN 并保存。已用 rg 全仓核验：`HostPinService.SetFromConfig`（唯一能清 pending 的方法）没有任何调用方。

**影响**：宿主重启前，所有 PIN 验证（锁屏解锁 `LockController.VerifyForExit` → `Context.GetService<IPinService>()`、退出确认 `VerifyPinWindow`、IPC `pinGuard`）都拿 pending 的**旧**哈希验证：输入新 PIN 报错、输入旧 PIN 通过。用户以为换了 PIN，实际安全凭据未变；更糟的是若用户在换 PIN 后立刻锁屏并只记得新 PIN，将无法解锁——与 S2 叠加时连"输旧 PIN"都做不到（字母被钩子吞掉）。

**建议**：`VerifyCore` 改为"pending 仅在尚未落盘时优先"，落盘后即清（如在 `HostPinService` 暴露 `ClearPending()` 并在配置保存路径调用；或每次 Verify 比较_pending 与 `Current().PinHash` 不一致时以 Current 为准）。

### [严重] S2. 锁屏键盘钩子白名单与 PIN 字符集不匹配——含字母的 PIN 会导致永久锁死

**位置**：`src\Modules\ScreenLock\Services\KeyboardBlocker.cs:85-97`；`src\Views\FirstRunWindow.xaml.cs:59-67`

**证据**：

```csharp
// KeyboardBlocker.IsAllowedKey —— 锁屏期间仅放行数字/NumPad/Backspace/Enter/Shift：
if (vk >= 0x30 && vk <= 0x39) return true;              // 主键盘数字
if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) return true;  // 小键盘数字
case VK_BACK: case VK_RETURN: case VK_SHIFT: return true;
... return (IntPtr)1;                                    // 其余一律吞掉（HookProc:129）

// FirstRunWindow.OnOkClick —— PIN 设置只校验长度，无字符集限制：
if (string.IsNullOrEmpty(pin1) || pin1.Length < 4) { ... }
```

**触发条件**：用户设置含字母/符号的 PIN（如 `abcd1234`），任意路径触发锁屏。低级键盘钩子在 LockWindow 之前吞掉所有字母键，PinBox 收不到输入。

**影响**：唯一逃生通道"切换至 Windows 锁屏"（`LockWindow.xaml.cs:357-367` LockWorkStation）走完回到会话后 CarroDesk 锁屏仍在（UnlockOnResume 默认 false），依旧打不出字母——死循环，只能靠另一会话/安全模式杀进程或手改配置。把"忘记 PIN"这一可恢复故障放大为永久锁死。

**建议**：PIN 设置时强制纯数字（首启向导与修改 PIN 共用 FirstRunWindow，一处校验即可）；或在 UI 明确警示"锁屏期间仅能输入数字"。

### [严重] S3. Batch-2 新增任务字段在持久化层完全缺失——保存即丢、手改 JSON 无效

**位置**：`src\Modules\TaskScheduler\Services\TaskConfigService.cs:365-384`（SerializeOptionsToken）、`:463-572`（ParseTaskNode）

**证据**（rg 全模块交叉验证）：`SerializeOptionsToken` / `SerializeActionToken` / `ParseTaskNode` 均无 `encoding` / `runAtStartup` / `catchUpMissed` / `env` 的读写；而消费端真实存在：

```csharp
TaskRunner.cs:265        string encOpt = task.Options != null ? (task.Options.Encoding ?? "")...
IntervalTrigger.cs:28    if (Task.Options != null && Task.Options.RunAtStartup)
DailyTrigger.cs:25       bool catchUp = task.Options != null && task.Options.CatchUpMissed;
TaskEditDialog.xaml.cs:508-512   RunAtStartup = ..., CatchUpMissed = ..., Encoding = GetSelectedComboTag(EncodingBox)
```

**触发条件**：在新建/编辑任务对话框设置输出编码、启动即运行、错过补跑或环境变量 → 保存成功 → 落盘时静默丢弃 → 下次 Load/Reload 后字段回到默认值。

**影响**：四个功能端到端失效：`encoding` 永远按默认解码（GBK 脚本输出乱码且无解）、`runAtStartup`/`catchUpMissed` 永远 false、`env` 永远为空。用户配置被静默吞掉且无任何提示。

**建议**：在 Serialize/Parse 两端补齐四个字段的非缺省落盘与读取（与 `stableUptimeSec` 同模式）；并在 `TaskDefinition.Validate` 中对 `encoding` 做枚举校验（当前任意字符串静默按 UTF-8 处理）。

### [严重] S4. LookupPrivilegeValue 传入 GUID 字符串而非特权名——SE_SHUTDOWN_NAME 启用恒失败，延迟睡眠静默失效

**位置**：`src\Modules\Awake\Services\AwakeService.cs:52, 119`

**证据**：

```csharp
private static readonly Guid SeShutdownName = new Guid("6d43ee9d-ce70-11cf-b7d7-00aa004bd83b");
...
if (!LookupPrivilegeValue(null, SeShutdownName.ToString(), out luid))   // 传的是 GUID 字符串
```

本机实测（`temp\test_privilege_lookup.ps1`，审查过程中执行）：

```
GUID-string result: False lastError=1313   (ERROR_NO_SUCH_PRIVILEGE)
"SeShutdownPrivilege" result: True lastError=0
```

**触发条件**：任何 `awake.sleep` 延迟睡眠执行时。`TryEnableShutdownPrivilege` 必然返回 false 并记警告，随后的 `SetSuspendState`（:517）在不持有已启用特权的情况下失败（UAC 过滤令牌中该特权存在但禁用；没有 LUID 就无法启用）。

**影响**：延迟睡眠功能对多数账户形同虚设；IPC 已向调用方回"已调度"，用户到点发现机器根本没睡。此问题恰在旧报告修复"睡眠前启用 SE_SHUTDOWN_NAME"（第三批 M15）时引入——修复本身写错了 API 参数。

**建议**：`LookupPrivilegeValue(null, "SeShutdownPrivilege", out luid)`；特权启用失败时把错误冒泡到调度结果，而非仅记日志。

---

## 四、高危问题（详述）

### [高] H1. 新任务编辑对话框（TaskEditDialog）编辑既有任务时静默丢失 `StableUptimeSec` 与 `options.workDir`

**位置**：`src\Modules\TaskScheduler\Views\TaskEditDialog.xaml.cs:104-107, 505-521`；语义参照 `TaskDefinition.cs:374-377`

**证据**：

```csharp
WorkDirBox.Text = act.WorkDir ?? "";       // PopulateForm 只回填 action.workDir，无 opt.WorkDir 兜底
...
task.Options = new TaskOptions { ... };    // BuildTaskFromForm 新建 Options：
// 不含 StableUptimeSec（重置为默认 60）、不含 WorkDir（EffectiveWorkDir 的优先源直接丢失）
```

而旧编辑器专门为此打过补丁：`TaskEditorWindow.xaml.cs:247`（workDir 回填带 `task.Options.WorkDir` 兜底）、`:542-548`（"UI 未暴露的选项字段在编辑既有任务时保留原值，否则保存时会被 new TaskOptions() 的默认值静默覆盖"）。近期重构出的 TaskEditDialog（任务管理器编辑入口全部走它，`TaskManagerWindow.xaml.cs:217`）没有继承这两处防线。

**触发条件**：在任务管理器中"编辑"任何一个 `stableUptimeSec != 60` 或 `options.workDir` 非空的任务 → 确定 → 保存。

**影响**：`stableUptimeSec` 被重置为 60（`0=永不复位` 的配置也被改掉），直接改变守护熔断计数复位语义；`options.workDir` 丢失后 `EffectiveWorkDir()` 回退到脚本目录，依赖相对路径的脚本行为改变。均无提示。

**建议**：`PopulateForm` 的 WorkDirBox 回填加 `opt.WorkDir` 兜底；`BuildTaskFromForm` 接收原任务并对 UI 未暴露字段（StableUptimeSec 等）逐字段透传。

### [高] H2. 启动窗口期的停止竞态：用户"停止任务"后进程照常启动，且从此不可见、不可停

**位置**：`src\Modules\TaskScheduler\Services\TaskSchedulerService.cs:594-602`（onStarted 回调）、`:994-1008`（TryStop）

**证据**：

```csharp
// TryStop：Handle 尚未回填（进程还没 spawn 完）的槽被直接摘除，只置 StopRequested：
var h = s.Handle;
if (h == null) { slots.RemoveAt(i); continue; }

// 而 ExecuteAsync 的 onStarted 回调不检查任何条件，无条件回填并把状态覆写为 Running：
h => {
    slot.Handle = h;
    fsm.OnProcessStarted(h.Pid, h.StartedAt);
    NotifyStateChanged(task.Name);
}
```

**触发条件**：触发器已占槽但 `TaskRunner` 尚未 spawn 完进程的毫秒级窗口内，用户执行停止（管理窗口/托盘/编辑器的停止、删除、重启按钮）。

**影响**：槽已从 `_running` 移除，之后进程照常启动。wait 模式下日志误记"stopped by user request"；detach 模式下常驻进程脱离注册表——`GetRunning` 不再列出、`TryStop` 永远 false、守护重启链也因槽不在册被取消，只能等宿主退出由作业对象回收。

**建议**：onStarted 回调里检查 `slot.StopRequested || !IsSlotRegistered(...)`，为真则立即 `h.Stop()` 不登记；或 TryStop 对 null-Handle 槽只置位不摘除，由启动路径观察到 StopRequested 后自终止。

### [高] H3. 停止/重启/宿主退出链路在调用线程上同步 taskkill（每实例最长 5s），UI 线程调用时冻结界面

**位置**：`src\Modules\TaskScheduler\Services\TaskProcessHandle.cs:301-308`；调用链 `TaskManagerWindow.xaml.cs:267,311,329`、`TaskSchedulerModule.cs:217`、`TaskEditorWindow.xaml.cs:1206`

**证据**：

```csharp
using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }   // KillTree
```

`TryStop` 从 UI 事件同步调用，对每个句柄串行 `h.Stop()`（taskkill + 最多 5s 等待）；`StopRunningInstances`（宿主退出）同样逐个同步。

**影响**：被停进程的子树恰好无视 taskkill（挂起状态、驱动占用）或 taskkill 本身缓慢时，UI 冻结最长 5s/实例；N 个任务的退出等待叠加。

**建议**：`TryStop` 将句柄收集（持锁）与 `h.Stop()` 执行（`Task.Run`）分离，UI 立即返回"停止中"；宿主退出路径可保留同步但设总体预算。

### [高] H4. IPC 命令在线程池上无锁改写 AwakeService 状态，与 UI 线程 1 秒 tick 竞争，最坏可立即触发睡眠/关机

**位置**：`src\Modules\Awake\Services\AwakeService.cs:461-464`（SchedulePowerOff 两步写）、`:406-407`（SetTimed）、`:600`（tick 判定）；线程模型来源 `src\Host\Commands\CommandHost.cs:153-157`

**证据**：

```csharp
// SchedulePowerOff（命令 Handler 经 Task.Run 在线程池执行，ConfigureAwait(false)）：
_pendingAction = action;                       // 先写 action
_pendingFireAt = ... DateTime.Now.AddSeconds(delaySeconds);   // 后写时刻

// OnTimerTick（UI Dispatcher，每秒）：
if (_pendingAction != PendingPowerAction.None && DateTime.Now >= _pendingFireAt) FirePendingPowerAction();
```

无任何内存屏障/锁。x86-TSO 下读方完全可能看到新 `_pendingAction` 而读到旧 `_pendingFireAt == DateTime.MinValue`，`DateTime.Now >= MinValue` 成立 → **立即执行** SetSuspendState/shutdown。`SetTimed` 同理（`_mode` 先写、`_expireTime` 后写）可被观察为"已过期"，刚下达的 `awake.on` 被立即撤销。

**触发条件**：经 IPC/MCP 调用 `awake.sleep` / `awake.shutdown` / `awake.on` 与 1s tick 相撞。窗口极小但后果严重，且破坏"预留 30 秒给 agent 回复"的契约。

**建议**：状态变更统一经 `_dispatcher.BeginInvoke`（服务里已有 `ApplyExecutionState` 的封送先例）；或引入单一状态锁。

---

## 五、中危问题（要点）

### M1. `fsm.OnProcessExited(0,...)` 被复用为"放弃启动"的复位，导致并发实例计数漂移与状态谎报

`TaskSchedulerService.cs:506,518,533`（三处"条件不满足/占槽被拒/单实例互斥跳过"路径调用 `fsm.OnProcessExited(0, false, false, 0, 0, 0)`）；`TaskStateMachine.cs:144,170-178`（当作真实退出：`ActiveInstances--` 且 `exitCode==0 → Idle + ConsecutiveFailures=0`）。后果：allowConcurrent 任务已有实例 A 运行时第二次触发被拒，会把 FSM 的 ActiveInstances 减到 0、状态改写为 Idle 且 LastOutcome="成功 (0)"——A 仍在运行但管理器显示"空闲"。建议为"未启动即放弃"增加专用跃迁（如 `OnAbandon()`：仅 Starting→Idle，不动计数）。

### M2. `WaitingRetry` 未被 `TryBeginStarting` 拒绝，延迟窗口内的触发会把熔断计数清零，架空 RestartLimit

`TaskStateMachine.cs:44-53,66`：`TryBeginStarting` 只拒绝 Running/Starting/Stopping/MarkedFailed(非人工)，`WaitingRetry` 落到第 66 行被改为 Starting，随后的占槽/条件失败路径经 `OnProcessExited(0)` 把 `ConsecutiveFailures` 清零（见 M1），`HandleDetachFailure`（TaskSchedulerService.cs:829）再把 0 镜像回 `_supervision`。守护重启延迟期（默认 5s，可配至 3600s）内恰有触发命中（interval≈delay、watch 高频、连点）时，崩溃风暴任务的熔断预算实际变为"每延迟窗口内的新预算"，可能永不熔断。建议 `TryBeginStarting` 将非人工 `WaitingRetry` 一并拒绝。

### M3. `CatchUpMissed` / `RunAtStartup` 的实现语义是"每次触发器重建都立即补一发"，而非"本次会话只补一次"

`DailyTrigger.cs:25-29,38-42`；`IntervalTrigger.cs:28-31`。触发器在每次 `ApplyCoreLocked`（任何保存/重载/总开关切换）都会整体重建，构造期跳过防误触初始化 + `Start()` 立即 `OnTick` → 当天已过触发点后的**每一次**重载都补跑一次；连续保存 N 次执行 N 次。当前因 S3 该字段无法持久化而未暴露，**修复 S3 后立即显现**。建议把"本会话已补跑"标记放服务层（类似 `_firedStartupTasks` 的按名集合，Apply 时不清除），触发器只负责调度。

### M4. TaskEditDialog 的 detach 测试运行句柄未保存：无法停止、关窗不终止

`TaskEditDialog.xaml.cs:356-372,400-404`：`StartDetached` 返回的 handle 是局部变量仅用于显示 PID；`OnTestStopClick` 只取消 wait 模式的 CTS。测试进程成为脱管进程（"停止"按钮无效，关窗不终止；`killWithHost=false` 时连宿主退出兜底都没有）。旧编辑器 `TaskEditorWindow.xaml.cs:1137-1180,1393-1405` 有完整处理，重构时丢失。另 `_testCts` 在对话框生命周期内从不 Dispose。

### M5. `HandleDetachFailure` 熔断分支锁内提前 return，跳过 `RemoveSlot` 造成槽位永久泄漏

`TaskSchedulerService.cs:834-845`（已复核）：

```csharp
if (newState == TaskRuntimeState.MarkedFailed)
{
    lock (_lock)
    {
        SupervisionState sup;
        if (!_supervision.TryGetValue(task.Name, out sup)) return;   // ← 提前返回，slot 仍在 _running
        ...
    }
    RemoveSlot(task.Name, slot);   // 只有正常路径才执行
```

仅在前一临界区（:821-832 已确保条目存在）与本临界区之间并发 `ApplyCoreLocked`（`_supervision.Clear()`）时发生，概率极低；但一旦命中：死槽（Handle=null）永驻 `_running`，非并发任务此后每次触发都被 `TryOccupySlot` 拒绝——任务静默瘫痪直到宿主重启。建议把 `RemoveSlot` 移到分支之前，或去掉提前 return 改空对象兜底。

### M6. LockController 的日志器在构造期求值，永远为 null，锁定回滚等关键日志全部丢失

`ScreenLockModule.cs:46-52`：

```csharp
Controller = new LockController(
    () => Context?.GetService<IPinService>(),   // 惰性 lambda
    () => Config,                                // 惰性 lambda
    Context?.GetService<ILoggerService>());      // 构造期立即求值，此时 Context 必为 null
```

LockController 内 `_logger?.LogError(...)` 全部空操作（LockController.cs:38,197,232,288），安全模块最重要的故障场景（"锁定失败正在回滚"、"N 个锁屏窗口关闭失败"）不可诊断。改为惰性委托即可。

### M7. 经配置重载路径移除的 AppAutoMute 目标应用，其静音永远不会被解除

`AppAutoMuteModule.cs:261-276,68-84`：`UnmuteAllTargets` 只按**当前**（新）`Config.TargetApps` 解除。设置窗口保存路径已用 `RestoreAudioForConfig(previous)` 规避（AppAutoMuteSettingsWindow.xaml.cs:301-302），但手工编辑 config.json / 远程下发重载等直接触发 `OnConfigReloaded` 的路径没有该保护：应用 A 在后台被静音后从清单移除并重载，A 切回前台走非目标分支，静音持续到进程重启。建议 `OnConfigReloaded` 替换 Config 前先按旧清单解除静音。

### M8. 剪贴板历史无总量上限：MaxItems × MaxTextLength 组合可产生 GB 级文件，退出 Flush 5 秒超时后丢尾部数据

`ClipboardHistoryService.cs:107-110`（单条截断上限 1,000,000 字符）、`:260-275`（条数上限 1000，无字节总量约束）；`JsonClipboardHistoryStorage.cs:190-200`（Flush 固定 5s 上限）。复制大量长文本（日志/JSON/Base64）填满 1000 条 × 1M 字符后，启动 Load（UI 线程整文件读取 + 可能 DPAPI 解密）与退出 Flush 随之退化；进程退出时 WriteLoop 未写完的最后版快照丢失。建议增加总字节数预算（超限按最旧未置顶淘汰），Flush 超时可配置并告警。

### M9. OnStartup 无整体异常防线：早期初始化异常会留下"半初始化僵尸进程"

`App.xaml.cs:52-227`（无外层 try/catch）+ `:565-589`（`DispatcherUnhandledException` 对非致命异常一律 `e.Handled = true`）。`LoadOrCreate()`（数据目录创建失败/磁盘满/权限）或后续装配步骤抛出异常时，异常被标记 Handled，进程存活但无托盘、无模块、无 IPC——用户看到的是"点了没反应"，且日志只有一条全局异常记录。建议对 OnStartup 主体包裹 try/catch：初始化未完成阶段失败直接 `Environment.FailFast` 或 `Shutdown(非零)` 并落盘 fatal.log。

---

## 六、低危问题（清单）

**TaskScheduler**

| 编号 | 位置 | 问题 |
|---|---|---|
| L1 | TaskSchedulerService.cs:538-566 | detach 启动失败路径不同步 `_supervision` 镜像，启动失败型熔断后 UI 永远不显示"已标记失败" |
| L2 | Common/ProcessJob.cs:104-108 + TaskProcessHandle.cs:59 | `SetInformationJobObject` 失败仅 Debug.WriteLine，作业照常返回 → `KillWithHost` 谎报 true，宿主退出兜底 taskkill 被跳过（罕见：安全软件干扰时留下孤儿进程树） |
| L3 | TaskSchedulerModule.cs:49 | `StateChanged += 匿名lambda` 位于 `if (Scheduler == null)` 之外，每次启停循环累积一个不可退订的处理器，刷新请求成倍放大 |
| L4 | TaskManagerWindow.xaml.cs:274-286,192-198 | 启停失败后不回滚内存态/忽略 `SetGlobalEnabled` 返回值，UI 与磁盘分叉（对比编辑器 :1296-1301 有回滚） |
| L5 | TaskConfigService.cs:291-318 | `SaveLocked` 白名单重建 tasks.json，丢弃所有 schema 外字段（手改注释/自定义键在首次 UI 保存后消失；与 S3 同根） |
| L6 | TaskEditorWindow.xaml.cs:53-57,985-1002 | 关闭时未停 `_cronDebounceTimer`（Tick 仍会触碰已关窗口的控件） |
| L7 | TaskSchedulerService.cs:443-450,213-221 | `StopTriggers` 的 `dispatcher.Invoke` 无异常保护，退出时序中 Dispatcher 已关闭会从 `Dispose()` 冒泡 |

**ScreenLock**

| 编号 | 位置 | 问题 |
|---|---|---|
| L8 | ScreenLockModule.cs:25,39-41 | `_pauseUntil`/`_sessionLocked` 跨线程读写无同步（x64 上仅可见性延迟） |
| L9 | IpPresenceDetector.cs:46-61 + ScreenLockModule.cs:186,207 | 设备在场首次探测同步阻塞共享 IdleTick 线程最长约 1.5s（每次用户活动后 ResetIdleMachine 重置探测时间戳所致） |
| L10 | KeyboardBlocker.cs:74 | UI 线程卡顿超过 LowLevelHooksTimeout 时 LL 钩子被系统跳过（锁窗 TOPMOST 仍在，可利用性低） |
| L11 | LockController.cs:297-301 | `VerifyForExit` 是无限流校验死代码（唯一绕过 PinGuard 的入口，建议删除或改走 `_pinGuard.Try`） |
| L12 | LockController.cs:97-99 | `ApplyPinFromConfig()` 空方法仍被 `OnConfigReloaded` 调用，语义误导 |

**Awake**

| 编号 | 位置 | 问题 |
|---|---|---|
| L13 | AwakeService.cs:733,644 | 每 5s 在 UI 线程全量枚举系统进程（旧报告 M14 未处理项） |
| L14 | AwakeSettingsWindow.xaml.cs:294 | 热键不校验格式（其他模块窗口都校验），无效热键静默失败 |

**Clipboard / Monitor / AppAutoMute**

| 编号 | 位置 | 问题 |
|---|---|---|
| L15 | ClipboardHistoryService.cs:95-104 vs 116-124 | 敏感词过滤先于自环抑制执行，回写敏感条目会被误记一次过滤命中（日志语义失真） |
| L16 | MonitorDdcService.cs:212-226 | 防抖队列 TOCTOU：writer 收尾与 `_isApplying` 复位之间到达的请求可能被静默吞掉 |
| L17 | MonitorDdcService.cs:278,349 | `GetLastWin32Error` 取到中间成功调用的残留值，`IsDisplaySleepingOrNotReady` 可能误判 |
| L18 | ProfileScheduleEngine.cs:183-222 vs 103-108 | `ScheduleDelayedReapply` 的 CTS 在 Stop 时 Cancel+Dispose，任务体尚未执行到 `Task.Delay(token)` 时抛 ObjectDisposedException（未观察异常噪音） |
| L19 | AppAutoMuteConfig.cs:12 | 默认配置开箱即静音 chrome/QQMusic/cloudmusic，对不知情用户像"声音坏了" |
| L20 | Host/Commands/CommandHost.cs:310-313 | `uint→int` 收敛溢出时 `Convert.ToInt32` 抛 OverflowException，错误码折叠为 -32010 而非 -32602（语义错，不崩溃） |

**宿主/其他**

| 编号 | 位置 | 问题 |
|---|---|---|
| L21 | App.xaml.cs:508-518 | 退出守卫 3s 超时后，守卫内部已打开的 PIN 窗口可能仍挂着而宿主继续 ExitApp（SafeInvoker 只停止等待，不中止 action） |
| L22 | FloatingPanelWindow.xaml.cs:322-327 | "Tray" 定位只用主屏 `SystemParameters.WorkArea`，任务栏在副屏时面板不在托盘所在屏（旧 M19/M24 未处理项的残留面） |
| L23 | MemoryOptimizer.cs:46-48 | ScheduleTrim 取消路径的 CancellationTokenSource 不 Dispose（依赖下次调度兜底，最后一次泄漏一个 Timer 直到 GC） |
| L24 | I18nService.cs:162-182 | `LoadExternalLocales` 为公共方法可重入，若在后台线程调用与 `Get` 并发遍历 `Dictionary` 有竞态（当前无并发调用点，防御性记录） |
| L25 | README.md:40 | 构建说明仍写 ".NET Framework 4.8 / C# 7.3"，实际已升级 net10.0-windows（commit 7298bd7），新开发者会按错误文档搭建环境 |

---

## 七、旧报告遗留项现状核对（20261003 → 20261025）

| 遗留项 | 现状 |
|---|---|
| H1 锁屏钩子与焦点解耦（根因） | 未处理（与 L10 相关，需非 100% 缩放多屏实测） |
| M6 单实例互斥体跨宿主语义 | 未处理；`Program.cs` 的两阶段创建 + AbandonedMutexException 接手已消除原崩溃路径 |
| M14 Awake UI 线程全量枚举进程 | 未处理（本轮 L13 重申） |
| M16 AppAutoMute 白名单轮询周期 | 未处理 |
| M19/M24 多显示器 DPI 与副屏定位 | 部分缓解：Custom 定位已做多屏映射（DisplayMonitorHelper），Tray 模式仍仅主屏（L22） |
| M25/M27 编辑器脏检查、安装脚本 i18n | 未处理 |
| M28 测试质量（布局自检正则、时序脆弱用例） | 未处理 |
| S3 剪贴板明文存储 | **已解决**（DPAPI 加密存储 + 格式自动检测已落地，且本轮复核未发现新缺陷；总量预算缺口另见 M8） |

---

## 八、修复优先级建议

1. **S1 + S2（PIN 链路）**：两处改动都很小（清 pending / 限定字符集），但共同决定"忘记 PIN"场景的后果等级。建议同一批修复并加回归用例（含字母 PIN 的设置拦截、改 PIN 后立即验证新 PIN）。
2. **S4（特权名）**：一行修复 + 把失败冒泡到调用结果。
3. **S3 + M3 + H1（TaskScheduler 持久化与字段保全）**：同一批处理——Serialize/Parse 补齐四字段、编辑器透传不可见字段、补跑标记移到服务层；顺带评估 L5（schema 外字段丢失）是否按 Load 保留原始 JToken 方案一并解决。
4. **H4（Awake 状态封送）**：统一 `BeginInvoke` 封送，改动小、消除"立即睡眠"尾部风险。
5. **H2 + M1 + M2 + M5（TaskScheduler 状态机语义）**：建议一次性设计 `OnAbandon()` 跃迁并调整 `TryBeginStarting` 拒绝集，比分散修补更不易引入新回归。
6. **H3（UI 同步 taskkill）**：改异步停止交互（"停止中…"），需要用户可见行为变化，单独一批。
7. 其余 M6–M9 与低危清单按顺手程度处理；L25（README）随手改。

---

## 九、测试基线与审查方法

- **构建**：`dotnet build CarroDesk.slnx` 0 警告 0 错误（审查基线上执行）。
- **测试**：`dotnet test --no-build` 全量 **391 通过 / 0 失败 / 0 跳过**（51s，较旧报告基线 347 继续增长；本轮全部发现均为既有测试未覆盖的路径）。
- **方法**：三个独立视角逐文件通读（TaskScheduler 27 文件 / 其余七模块约 12000 行 / 宿主核心与 IPC 内核约 9000 行），严重级别条目均经第二视角回读原始代码复核（S1/S2/S3/S4/H1/H2/M3/M5 均已人工复核证据行）；S4 附带一次本机 P/Invoke 实测。
- **局限**：多显示器非 100% 缩放、远程会话、真实服务 DACL 拒绝、真机睡眠等场景无法在本环境复现，相关项（L10、L22、M16 类）以代码推演为准；未审计 `docs/old`、脚本与 NSIS 安装包逻辑。

---

## 十、修复进展（2026-10-25，7 批提交）

全部严重/高危与主要中危问题已在本日修复并分批提交（分支 `bugfix`，测试基线从 391 推进到 405 通过 / 0 失败，每批 `dotnet build` 0 警告 0 错误）：

| 批次 | 提交 | 修复项 | 关键改动 |
|---|---|---|---|
| 1 | f9f9cee | S1 + S2 | HostPinService stale pending 检测（配置更换后新 PIN 立即生效、旧 PIN 失效）；FirstRunWindow PIN 限定纯数字（`ValidatePin` 静态可测）+ 语言包 `FirstRun.ErrorDigitsOnly`；新增 `PinCredentialTests`（5 项） |
| 2 | 34800a7 | S4 | `LookupPrivilegeValue` 传真实特权名 `SeShutdownPrivilege`；`SetSuspendState` 失败时合并特权原因记 Error 并经新 `notifyFailure` 回调顶到用户气泡 |
| 3 | acd35c4 | S3 + M3 + H1 + M4 | TaskConfigService 补齐 encoding/runAtStartup/catchUpMissed/env 序列化与解析（env 兼容 `"K=V"` 数组）；encoding 枚举校验；补跑收口到 `ApplyCoreLocked` 会话集合（触发器重建不再自带补发）；TaskEditDialog 显示有效工作目录/透传 StableUptimeSec/清空 options.workDir；detach 测试句柄保存 + OnClosing 兜底；新增 `TaskBatch2FieldsTests`（4 项） |
| 4 | 10fbc6f | H4 + AW-2 + AW-3 | AwakeService 两步写改为"先写时刻后写闸门字段"（x86-TSO 读方看到闸门翻转必已见新值）+ `_mode`/`_pendingAction` volatile（封送方案经实测会在测试/部分宿主下死锁，已回退为最小修复）；设置窗口"关闭"不再复位用户抑制；"保持至指定时刻"严格 HH:mm[:ss] 解析；新增 `AwakeMarshallingTests`（3 项） |
| 5 | 94f1a88 | H2 + M1 + M2 + M5 | 新增 `TaskStateMachine.OnAbandon()`，四处放弃启动路径不再复用 `OnProcessExited(0)`；`TryBeginStarting` 拒绝延迟窗口内非人工触发（熔断预算不再被冲刷）；`AttachDetached`/wait 模式 onStarted 检查 `slot.StopRequested` 立即终止刚 spawn 的进程（停止竞态闭环）；`HandleDetachFailure` 熔断分支兜底重建 supervision 条目（死槽不再滞留）；新增 3 项状态机测试、修正 2 项按缺陷路径模拟的既有测试 |
| 6 | 76d64bd | H3 | `TryStop` 的句柄终止（taskkill 每实例最长 5s）转后台：状态置位与槽出册同步完成即返回"已受理"，UI 不再被顽固进程树冻结；返回值语义不变，宿主退出路径保持同步 |
| 7 | 05fc9ca | M6 + M9 + MP-2 + L3 + L12 + SL-3 + AM-1 + L25 | App.OnStartup 整体防线（fatal.log + 退出，不再留僵尸进程）；LockController 日志器惰性化；删除 `VerifyForExit`（绕过 PinGuard 的死代码）与空方法 `ApplyPinFromConfig`；MonitorProfile 时间输入严格 HH:mm[:ss]（与 AW-3 同根）；TaskSchedulerModule StateChanged 具名幂等订阅；AppAutoMute 托盘添加应用保存失败回滚 + 提示；README 构建说明更正 net10.0-windows |

**未处理遗留**（与第五/六节编号对应）：M7（AppAutoMute 配置重载静音残留）、M8（剪贴板总量预算）、L1/L2/L4/L5/L6/L7（TaskScheduler 低危）、L8–L10（ScreenLock，需实测）、L13–L20、L21–L24。修复前请先阅读对应条目的触发条件与建议修法。
