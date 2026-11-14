# 任务触发频率限制（TaskFireLimiter）设计

> 版本：v1 · 2026-11-14 · 基线 commit `cb4289b` · 基线测试 445/445 通过
> 适用模块：`src/Modules/TaskScheduler/`
> 状态：设计已定稿，按 §6 分批实施；本次范围**不含**附录 B 列出的其它触发器缺陷修复。

---

## 1. 背景与问题

用户诉求：`idle` 触发器需要「每天最多运行 N 次」与「两次运行最短间隔」两种约束。

代码实证（均已逐行核对，非推测）：

1. `Services/Triggers/IdleTrigger.cs:26,44-60` 只有一个 `_fired` 幂等标志，`OnUserActive()` 一到就 `Interlocked.Exchange(ref _fired, 0)` 重新武装。一天内「离开工位 → 回座 → 再离开」几次，任务就触发几次，不存在任何频次上限。
2. **所有触发器状态都是实例字段**，而 `TaskSchedulerService.ApplyCoreLocked():294,368-380` 在每次 `Start()` / `Reload()` / `SetGlobalEnabled(true)` / 模块配置热重载时执行 `StopTriggers()` 并整体重建触发器。因此 `DailyTrigger._lastFiredDate:13`、`CronTrigger._lastFiredMinute:30`、`IntervalTrigger` 计时相位、`IdleTrigger._fired:26` 都会清零。服务层仅为 daily/startup 用 `_lastDailyFire:29`、`_firedStartupTasks:22` 打了两个补丁。
   **结论：计数绝不能放在触发器实例里，否则用户保存一次任务即可绕过。**
3. `OnTriggerFired():583` 是绝大多数触发的统一入口，但 `RunManual():477`、`RestartTask():507` 直接调 `ExecuteAsync()`，detach 守护重启走 `RestartContinue()`，三者都是旁路。任何统一门都必须显式处理。
4. `Host/Commands/CommandRateLimiter.cs` 是 IPC 命令限流，维度是「能力名 + 来源」，与任务触发无关，不可复用其窗口语义；但 `Services/PinService.cs:107-316` 的 `PinGuard` 提供了本项目**既成的持久化限流范式**：`DefaultStatePath` + 延迟读回 + `AtomicFile.WriteAllText` + 落盘失败降级为进程内计数并留痕。本设计照搬该范式。

## 2. 目标与非目标

**目标**

- 任务级、与触发类型解耦的两种约束：`maxRunsPerDay`（本地自然日计数上限）、`minIntervalSec`（两次触发最短间隔）。
- 约束在**配置重载、全局开关切换、宿主重启**之后仍然有效。
- 对 `idle` 表现为「冷却/额度未恢复时不触发，恢复后若仍处于空闲则自动补触发」，而不是永久丢弃。
- 默认配置下零行为变化、零额外落盘。

**非目标**

- 不修复附录 B 列出的其它触发器缺陷（另立批次）。
- 不引入全局并发/速率限制、不改造 `ITrigger.Fired` 事件签名（属更大重构，见附录 B「可扩展」）。
- 不限制 `retry` 重试与 detach `restart=on-failure` 守护重启：二者是**同一实例的恢复**，不是新触发，不应消耗配额。

## 3. 配置

新增 3 个 `options` 字段（放 `options` 而非 `trigger`，因为限流是任务级执行策略，与触发类型无关，其它触发器可直接复用；且 `options` 已有 `ExtraOptions` 机制，手写字段不会被 GUI 保存抹掉）：

| 键 | 类型 | 默认 | 校验 | 说明 |
|---|---|---|---|---|
| `maxRunsPerDay` | int | `0` | 0..1000 | `0` = 不限；本地自然日 00:00 归零 |
| `minIntervalSec` | int | `0` | 0..86400 | `0` = 不限；支持 `"30m"` / `"1h30m"` 字符串（`TaskConfigService.GetInt():783-799` 已会走 `TaskDefinition.ParseDuration`），保存时归一为秒 |
| `limitScope` | string | `"auto"` | `auto` \| `all` | `auto`：人工类触发（manual / restart / hotkey）绕过限制；`all`：全部触发受限 |

配置示例：

```json
{
  "name": "idle-clean",
  "trigger": { "type": "idle", "afterMinutes": 10 },
  "action": { "file": "clean.ps1" },
  "options": { "maxRunsPerDay": 3, "minIntervalSec": 1800, "limitScope": "auto" }
}
```

行为：每次空闲周期最多触发一次；一天最多 3 次；两次间隔至少 30 分钟；手动运行不受限。

## 4. 设计

### 4.1 状态模型

`Services/Tasks/TaskFireLimiter.cs`，服务层单例持有 `Dictionary<string, FireState>`（`StringComparer.OrdinalIgnoreCase`）：

```csharp
FireState { string Date; int Count; DateTime LastFireUtc; }
```

- `Date` 用本地日期 `yyyy-MM-dd`，用于自然日归零。
- `LastFireUtc` 用 UTC，用于最短间隔判定，避免本地时间歧义（DST、时区变更）。
- 字典容量随任务名增长，`ApplyCoreLocked` 后调用 `ForgetRemovedTasks(activeNames)` 清理已删除任务的条目。

### 4.2 判定算法

两个公开方法共用同一个 `EvaluateNoLock()` 判定内核：

- `bool CanFire(taskName, policy, out refusal)`：**只读**，不改变状态。供 `IdleTrigger` 前移判定用。
- `bool TryAcquire(taskName, policy, reason, out refusal)`：在一把 `_sync` 锁内原子完成「加载状态 → 日期滚动 → 间隔检查 → 次数检查 → `Count++` / `LastFireUtc=UtcNow` → 落盘」。服务层统一消耗点。

`EvaluateNoLock` 顺序：

1. `policy` 两项均为 0（默认不限）→ 直接放行，**不创建状态条目、不落盘**（零开销兼容）。
2. `state.Date != 本地今天` → `Count = 0; Date = 今天`。
3. `UtcNow < state.LastFireUtc` → 判定为系统时钟回拨：重置该条状态、`TaskLogger.Warn` 留痕、放行（否则一次回拨会让最短间隔永久不满足）。
4. `UtcNow - LastFireUtc < minIntervalSec` → 拒绝，refusal 说明还需等待多久。
5. `Count >= maxRunsPerDay` → 拒绝，refusal 说明当日额度与次日归零。
6. 放行。

`limitScope=auto` 时，服务层对人工类 reason 直接跳过门（不消耗）；`all` 时照常判定。判定人工类采用**前缀匹配** `manual` / `restart` / `hotkey:`（见附录 B 对既有 `ExecuteAsync:616-618` 精确匹配偏差的说明；本次只在新门内部用正确判定，不改动 `ExecuteAsync` 既有语义，避免超范围行为变更）。

### 4.3 判定点与链路

```
触发器 Fired ──> OnTriggerFired ──> [TryAcquire 门] ──> Task.Run(ExecuteAsync)
                     ▲                    │
                     │                    └─ 拒绝：TaskLogger.Info + AddRecent("throttled") + return
                     └─ 现有 startup/daily 会话记录保持原顺序，门插在其后、Task.Run 之前
RunManual / RestartTask ──(limitScope=all 时)──> [TryAcquire 门]
```

- 门插在 `OnTriggerFired():583-607` 现有 `_firedStartupTasks` / `_lastDailyFire` 记录**之后**：被限流的 `startup:catchup` 仍记为「本会话已尝试补跑」，语义与现有会话集合一致，行为可预测。
- `startup:catchup` / daily 补跑走 `OnTriggerFired`，会被门拦，属正确行为（补跑也是一次运行）。
- 语义为**按触发次数计**（`TryAcquire` 一步式，最保守且并发安全）：被 `when` 条件、FSM、运行槽、单实例互斥拒绝也消耗一次。理由：实现简单、宁可少跑不会超跑、与 `DailyTrigger` 现有「触发即标记当天已跑」一致。若将来要求「只算真正启动」，把消耗点移到 `ExecuteAsync()` 占槽成功后、`TaskRunner.RunAsync()` 之前，并照 `:655-664` 的单实例跳过分支做 `OnAbandon()` + `RemoveSlot()`；代价是拆成 Check/Commit 两阶段。
- 落盘失败、状态文件损坏：**绝不阻断任务执行**，降级为进程内计数并 `TaskLogger.Warn` 留痕（与 `PinGuard` 一致）。

### 4.4 持久化

- 路径：`Path.Combine(ConfigService.DirPath, "task-fire-state.json")`，暴露静态 `DefaultStatePath`；构造函数 `statePath = null` 时完全不落盘，供单测与内存态注入使用。
- 形态：`{ "version": 1, "tasks": { "<name>": { "date", "count", "lastFireUtc" } } }`，Newtonsoft.Json 序列化。
- 写入：`AtomicFile.WriteAllText()`（`Common/AtomicFile.cs:10`），每次 `TryAcquire` 成功后写。触发本身是低频事件，且限流状态属关键状态，不做延迟合并写，避免「丢一次写 = 超跑一次」。
- 读取：首次使用延迟读回（`EnsureStateLoadedNoLock`），只接受 `version == 1`；未知版本/解析失败/IO 异常 → 备份原文件后重置为空状态并告警。日期已过期的条目在 `EvaluateNoLock` 里自然归零。
- `Apply` **不清空** limiter（与 `_lastDailyFire` 一致），只调 `ForgetRemovedTasks`，这是「重载不绕过」的关键。
- 风险：便携模式多宿主实例并发写同一状态文件时，以最后写入为准；`AtomicFile` 保证单次写入原子、不会写坏文件，但不提供跨进程互斥。如需严格配额，后续可加命名 Mutex（本次不做）。

### 4.5 idle 专项：冷却后自动补触发

只在统一门下，`IdleTrigger` 的 `_fired` 在 Fired 时已置 1，被门拒绝后本轮空闲不再触发，必须等下一次「活跃 → 空闲」周期；若用户频繁回座，`minIntervalSec` 会表现为「一直不跑」。

解决：把 limiter 注入 `IdleTrigger`，在 `OnIdleTick()` **置位 `_fired` 之前**调用只读 `CanFire()`：

```csharp
if (rawIdle < afterMinutes) return;
if (Volatile.Read(ref _fired) != 0) return;
if (!_limiter.CanFire(Task.Name, policy, out refusal)) {
    LogRejectionRateLimited(refusal);   // 见下
    return;                             // 保持 _fired == 0，下一秒继续问
}
if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0) return;
Fired?.Invoke(Task, "idle:" + afterMinutes + "m");
```

- `CanFire` **不消耗**，仍由服务层 `OnTriggerFired` 的 `TryAcquire` 统一消耗，因此**不存在重复计数**，服务层也无需为 idle 开特例。`CanFire → Fired → OnTriggerFired` 是同一线程同步链，窗口内不会有同任务的其它触发插入。
- `SystemIdleService` 每秒扇出一次 `IdleTick`，因此冷却/额度恢复后，只要用户仍处于空闲，最多 1 秒就会自动补触发；`_fired` 仍保证「每个空闲周期只跑一次」，用户活跃时照常重新武装。
- **拒绝日志必须限频**：按「refusal 文本变化」或「距上条 ≥ 60s」记一条 `TaskLogger.Info`，否则 1 秒一 tick 会刷爆 5MB 轮转日志。
- 未注入 limiter（旧构造/单测）时 `CanFire` 恒放行，保持现有行为不变。

### 4.6 可观测性

- 拒绝统一记 `TaskLogger.Info(taskName, "skipped(" + reason + ") fire limit: " + refusal)`，并 `AddRecent(taskName, "throttled")` 让「最近运行」可见。
- 任务管理窗口的触发器描述追加 `· 每天≤N次` / `· 间隔≥Xm` 摘要（`TaskManagerWindow.xaml.cs:912` 附近）。
- `TaskFireLimiter.GetSnapshot(taskName)` 暴露当日计数与下次可触发时刻，供 UI/排障/单测读取。

## 5. 兼容性与风险

| 项 | 处理 |
|---|---|
| 旧 `tasks.json` | 三字段缺省即「不限」，行为零变化；`KnownOptionKeys` 补新键，避免手写字段被 `ExtraOptions` 重复回写 |
| 旧状态文件不存在 | 视为空状态，首次触发时创建 |
| `Clone()` | `TaskDefinition.Clone()` 经 JSON 往返，新字段为可序列化 POCO 属性，自动带上；`ExtraOptions` 仍需单独搬运（现有逻辑不动） |
| 多触发器并存 | 状态按**任务名**聚合，与触发类型无关；同一任务被多种方式触发也共享同一额度 |
| 系统时间回拨 | UTC 倒退时重置该任务状态并放行，记 WARN |
| DST / 时区 | 每日计数按本地自然日，间隔按 UTC；跨 DST 的当日边界以本地 00:00 为准 |
| 落盘失败 | 降级内存 + WARN，不阻断任务 |
| 便携多实例 | 最后写入胜，配额可能宽松；已在 §4.4 记录 |

## 6. 分批实施与提交计划

每批结束均要求 `dotnet build` 0 警告 0 错误、`dotnet test` 全通过，并在 `docs/CHANGES-20261114.md` 顶部按秒追加变更摘要。commit message 用英文 conventional style，与现有 `master` 一致。

| 批次 | 模块 | 主要文件 | commit |
|---|---|---|---|
| 1 | 文档 | 本文件、`docs/USAGE.md`、`docs/AUTORUN-DESIGN.md`、`docs/CHANGES-20261114.md` | `docs(tasks): add fire limiter design and rollout plan` |
| 2 | 限流核心 | 新增 `Services/TaskFireLimiter.cs`、`tests/TaskFireLimiterTests.cs` | `feat(tasks): add persistent per-task fire limiter` |
| 3 | 配置层 | `Models/TaskDefinition.cs`、`Services/TaskConfigService.cs`、配置往返单测 | `feat(tasks): add throttle options to task config` |
| 4 | 服务层 | `Services/TaskSchedulerService.cs`、集成单测 | `feat(tasks): enforce fire limit in scheduler dispatch` |
| 5 | idle 触发器 | `Services/Triggers/IdleTrigger.cs`、idle 单测 | `feat(tasks): re-arm idle trigger after cooldown` |
| 6 | UI / i18n / 收尾 | `TaskEditDialog.xaml(.cs)`、`TaskManagerWindow.xaml.cs`、`Assets/Locales/*.json`、文档收尾 | `feat(tasks): expose throttle options in task editor` |

**批次 2 的解耦约定**：`TaskFireLimiter` 只依赖独立的 `FireLimitPolicy` 结构（策略值由调用方传入），**不直接读 `TaskOptions` 新字段**，因此批次 2 可独立编译通过；批次 3 加入字段后，批次 4 在服务层构造 policy 并接入。

## 7. 测试矩阵

复用 `tests/CarroDesk.Tests/ConcurrencyAndLifecycleTests.cs:98-129` 的 `FakeIdleService`；数据目录由 `TestEnvironment.AssemblyInitialize` 隔离，`ConfigService.DataDirOverride` 保证状态文件不落到真实用户目录。

| # | 用例 |
|---|---|
| 1 | 默认策略（两项 0）完全放行，且不创建状态条目、不落盘 |
| 2 | `maxRunsPerDay=N`：第 N 次放行、第 N+1 次拒绝 |
| 3 | 本地日期滚动后计数归零（注入 `localClock`） |
| 4 | `minIntervalSec`：边界前后各一次，间隔内拒绝、到点放行 |
| 5 | 系统时钟回拨：重置并放行，记 WARN |
| 6 | 并发 `TryAcquire` N 次，恰好放行配额数量（原子性） |
| 7 | 状态文件重启读回：新建 limiter 指向同一路径，额度继续生效 |
| 8 | 状态文件损坏 / IO 失败：降级内存、不抛异常、不阻断 |
| 9 | `ForgetRemovedTasks` 清理孤儿条目 |
| 10 | 配置往返：`Save` → `Load` 三字段及别名保留；未知值被 `Validate()` 拒绝 |
| 11 | 服务层：重载后额度不丢；`startup:catchup` 被限流仍记为已尝试 |
| 12 | `limitScope=auto` 人工触发绕过；`all` 时 `RunManual` 被拒 |
| 13 | idle：冷却内不触发、冷却结束且仍空闲自动补触发、`_fired` 不被误置位 |
| 14 | idle：拒绝日志限频（连续 120 次 tick 只产生 1~2 条日志） |
| 15 | 回归：既有 445 项全通过，本地化契约（两语言键集/占位符/XAML 裸中文）全通过 |

## 附录 A：关键代码位置索引（基线 `cb4289b`）

| 位置 | 作用 |
|---|---|
| `TaskSchedulerService.cs:583-608` | `OnTriggerFired` 统一入口，门插入点 |
| `TaskSchedulerService.cs:460-515` | `RunManual` / `RestartTask` 旁路 |
| `TaskSchedulerService.cs:271-418` | `Apply` / `ApplyCoreLocked`，触发器重建与会话集合 |
| `TaskSchedulerService.cs:610-650` | `ExecuteAsync` 条件 / FSM / 槽位拒绝链 |
| `Triggers/IdleTrigger.cs:26,44-60` | `_fired` 幂等与重新武装 |
| `Services/PinService.cs:107-316` | `PinGuard` 持久化限流范式 |
| `Common/AtomicFile.cs:10` | 原子写 |
| `Services/TaskConfigService.cs:492-525,669-702` | `KnownOptionKeys` / options 序列化 / 解析 |
| `Models/TaskDefinition.cs:66-129,241-325` | `TaskOptions` / `Validate()` |
| `Views/TaskEditDialog.xaml:242-248` / `.xaml.cs:85,617` | idle 面板与表单读写 |
| `Views/TaskManagerWindow.xaml.cs:895-916` | 触发器描述 |

## 附录 B：其它调度器问题清单（本次**不实施**，供后续立项）

### P0 缺陷

| 位置 | 问题 |
|---|---|
| `FileWatcherTrigger.cs:30-33,119-134` | `Start()` 无条件 `_stopped = false`，而 Error 自愈任务在 `if (_stopped) return;` 之后才调 `Start()`，检查与置位非原子 → Apply/Stop 期间旧 watcher 可能复活（幽灵触发 + 句柄泄漏）。建议实例 generation 计数或 lock 包住 Start/Stop。 |
| `FileWatcherTrigger.cs:155-163` | 在 `ConcurrentDictionary.AddOrUpdate` 的 valueFactory 里写外部局部变量 `debounced`；该 factory 可能并发多次调用且不在字典锁内，防抖判定有竞态。建议 `TryGetValue` + `TryUpdate` CAS 或 lock。 |
| `StartupTrigger.cs:19-37` | `delaySec` 随每次 Apply 重建重新起算，`_firedStartupTasks` 只在真正 Fired 后登记 → `delaySec=300` 时只要 5 分钟内有重载，启动任务被无限推迟。建议以宿主启动时刻算绝对触发时间。 |
| 全部触发器 | last-fired 状态都在实例字段、Apply 全丢。建议统一收进 §4 的按任务名持久化状态仓库，从根上治理而非继续打补丁。 |

### P1 健壮性

- `IntervalTrigger.cs:21-31`：计时从 `Start()` 起算、重载即重置相位；`DispatcherTimer` 依赖 UI 消息泵，`OnPowerModeChanged`（`TaskSchedulerService.cs:1403-1409`）只补偿 Daily/Cron，Interval 无补偿。
- `DailyTrigger.cs:50-60`：注释写「90s 窗口 / 至少 60s 后」，实现只是 `now >= todayAt && 未跑过`，注释与代码不符；且 `_lastFiredDate` 在 Fired 时置位，被条件或并发拒绝也算「今天已跑」。
- `ManualTrigger.TriggerManual()` 全仓库仅定义处出现（死代码）；`RunManual` / `RestartTask` 绕过 `OnTriggerFired`，任何统一门/审计/记录都会被旁路。建议收敛到单一 `Dispatch(task, reason, isUserDriven)`。
- `HotkeyTrigger.cs:44` 发出的 reason 是 `"hotkey:" + 热键`，而 `ExecuteAsync():616-618` 用 `string.Equals(reason, "hotkey")` 判 `userDriven`，恒为 false → 热键触发在熔断/等待重试期会被当作自动触发拒绝，也不能复位熔断。本次新门内部改用前缀判定，但**不改动 `ExecuteAsync` 既有语义**（避免超范围行为变更），留待后续统一修正。

### 可扩展

- `ITrigger.cs:11` 的 `Fired` 是 `void`，触发器拿不到「被接受 / 被限流 / 被条件拒绝」的反馈。改结构化 `TriggerFiredEventArgs { Type, Reason, IsUserDriven, IsCatchUp }` 并返回 `bool Accepted`，可同时替掉 `OnTriggerFired:587/594` 的 `reason.StartsWith(...)` 与 `ExecuteAsync` 用字符串判 `userDriven` 两处脆弱设计。
- 暴露 `NextFireAt`（`CronTrigger._scheduledFor:36` 已有）到管理窗口 / CLI；增加全局并发与触发速率上限（现仅单任务 `MaxConcurrentInstances=5`），防 watch 风暴打满线程池。
- `watch` 的 debounce 500ms / file-ready 3s / `IncludeSubdirectories`、`when.onlyIdle` 的 60s 阈值（`TaskConditionEvaluator.cs:36`）全硬编码，建议配置化；daily 只支持单时间点、session 只支持 Lock/Unlock。
- `TaskConditionEvaluator:35` 自己 P/Invoke `IdleDetector`，与 `IIdleService.RawIdle` 不同源，会出现「触发器判空闲、条件判不空闲」的窗口，建议统一注入 `IIdleService`。
- `CronHelper.GetNextOccurrence:296-337` 在 UI 线程按分钟步进最多 8 年，稀疏表达式可能卡顿；`CronTrigger` 的 `missed` 判定基于墙钟，系统时间前跳时有歧义（注释已承认 DST 限制）。
