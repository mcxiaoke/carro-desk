# 任务调度：常驻（持久化运行）任务支持设计

- 日期：2026-09-28（v1.0 草案）/ 2026-09-29（v1.1 决策拍板，B1 已实施）
- 状态：v1.1 — 开放问题已全部拍板（见 §15），B1 已实施并全量测试通过
- 关联：`docs/AUTORUN-DESIGN.md`（现有任务模型）、`docs/CHANGES-20260926.md`（A 修复与 B1 实施记录）
- 代码定位：`src/Modules/TaskScheduler/`

---

## 1. 背景与问题

任务调度模块（TaskScheduler）当前只有一种执行模型：**启动进程并等待其退出**。

| # | 问题 | 位置 |
|---|------|------|
| P1 | `timeoutSec=0`（默认）时无限等待进程退出；被测/被调度程序一旦常驻，执行流永不结束 | `TaskRunner.cs` 等待段 |
| P2 | `TaskSchedulerService._running[task.Name]` 在进程退出前一直占用：常驻任务永久占槽，Interval/Cron 再次触发永远 "skipped concurrent execution not allowed"；改 `allowConcurrent=true` 则每次触发叠加新实例 | `TaskSchedulerService.ExecuteAsync` |
| P3 | 任务进程挂 kill-on-close 作业对象：宿主退出/崩溃/更新会连带杀死"本应一直活着"的常驻进程（与常驻语义相反） | `ProcessJob.cs`（注释已明示代价） |
| P4 | 无停止手段：UI / 调度器 / CLI / MCP 都不能停止运行中的任务进程；作业对象句柄在 `finally` 中释放，事后无从回收 | `TaskRunner.RunAsync` finally |
| P5 | 模型层无运行模式概念：`TaskOptions` 只有 Hidden/TimeoutSec/AllowConcurrent/Retry/WorkDir/NotifyOnFailure | `TaskDefinition.cs` |
| P6 | 宿主重启后无存活检查、无重新拉起（仅 Startup 触发碰巧能重拉一次） | — |

**本期已落地的 A 修复**（不属本设计范围，但界定残余问题）：编辑器"测试运行"加了 10s 兜底超时、停止按钮（`CancellationToken` + `TaskRunOutcome`）、防重入与关窗取消。这只解决了**编辑器手工试跑**路径；调度器对常驻任务的 P2~P6 依旧。

## 2. 目标 / 非目标

**目标**

- G1 任务可声明为常驻（detach）：触发即启动、不等待退出、不阻塞触发循环、不占执行等待。
- G2 运行状态可见：任务名 → pid / 启动时间 / 运行时长，UI 可查。
- G3 可手动停止（UI；CLI/MCP 在二期）。
- G4 宿主退出对常驻进程的影响可配置（killWithHost）。
- G5 （三期）意外退出可自动拉起（supervise），构成完整守护语义。

**非目标**

- 不做宿主重启后对已运行实例的接管（跨进程无法可靠移交句柄，见 S3/S4）。
- 不做进程内嵌脚本运行时/多路复用。
- 不改变 wait 模式（现状）的任何既有语义。

## 3. 术语

- **wait 模式**：现状模型，启动后等待退出，拿退出码。
- **detach 模式**：启动后立即返回，进程在后台常驻；通过退出 watcher 感知结束。
- **killWithHost**：宿主退出时是否连带终止该任务的进程树（依赖 kill-on-close 作业对象）。
- **运行注册表**：宿主进程内的 `任务名 → TaskProcessHandle` 映射，运行状态的唯一数据源。

## 4. 配置模式演进

```json
"options": {
  "hidden": true,
  "timeoutSec": 0,
  "allowConcurrent": false,
  "retry": 0,
  "workDir": "",
  "notifyOnFailure": true,
  "mode": "wait",              // 新增："wait"(默认) | "detach"
  "killWithHost": true,        // 新增：默认 true（保持现状安全语义）
  "singleInstance": false      // 新增（可选，§12）：跨宿主重启的单实例互斥
}
```

**向后兼容**：旧 tasks.json 无这些字段 → 反序列化缺省 `mode=wait`、`killWithHost=true`、`singleInstance=false`，行为与今天完全一致。无需迁移。

**校验规则**（进 `TaskDefinition.Validate()`，编辑器与加载期同时生效）：

| 组合 | 规则 | 理由 |
|------|------|------|
| `detach` + `timeoutSec > 0` | **拒绝** | 常驻进程不该被定时杀，语义矛盾 |
| `detach` + `retry > 0` | **拒绝** | retry 依赖"等到退出"才有退出码 |
| `detach` + `allowConcurrent = true` | **拒绝** | 常驻任务多实例几乎总是错误（S9） |
| `killWithHost = false` | 允许，文档明示孤儿风险 | S1/S2 |

## 5. 核心抽象：TaskProcessHandle

新增 `src/Modules/TaskScheduler/Services/TaskProcessHandle.cs`，把现在散在 `RunAsync` 里的 proc/job/kill/wait 收拢为一个可持有、可观察、可停止的对象：

```csharp
public sealed class TaskProcessHandle : IDisposable
{
    public TaskDefinition Task { get; }
    public int Pid { get; }
    public DateTime StartedAt { get; }
    public bool KillWithHost { get; }
    public bool HasExited { get; }

    /// 进程退出后触发一次（含被停止的情况）。exitCode 可能为 null（kill 时取不到）。
    public event Action<TaskProcessHandle, int?> Exited;

    /// 停止整棵进程树：job 句柄 dispose（kill-on-close 时）+ taskkill /T /F 降级链。
    public void Stop();

    /// wait 模式用：等待退出并返回退出码（现有 RunAsync 等待段逻辑原样迁入）。
    Task<int> WaitAsync(CancellationToken ct);
}
```

要点：

1. **作业对象策略**按 `killWithHost` 分派：
   - `true` → 现有 `ProcessJob.TryCreate()`（kill-on-close）；
   - `false` → **不挂作业对象**（实现修正：原设想挂不带 kill 标志的作业，但 Stop 经由作业对象本就需要 kill-on-close 标志，不带标志的作业毫无用处；`Stop` 走 `taskkill /PID x /T /F` 降级链即可，且该链不依赖 Process 对象存活——句柄 Dispose 后仍可按构造时记录的 pid 停止）；
   - assign 失败（宿主已被嵌套作业限制等）→ 降级为不挂 job，`Stop` 走 `taskkill /T /F`（沿用现 `KillTree`）。
2. **退出 watcher**：`Exited` 由一个只等退出码的后台 Task 触发（不占任何执行槽），负责：写退出日志（"finished pid=x code=y duration=…"）、从运行注册表移除、清 `_running` 槽、（detach 且非用户停止且 exitCode≠0 时）失败通知 / 三期的 restart 判定。**订阅顺序约束：调用方必须先订阅 `Exited` 再调 `BeginExitWatch()`**，否则瞬间退出的实例（S19）会错过事件、运行槽泄漏。
3. **事件错过防护**：`BeginExitWatch` 幂等；`Stop` 幂等；`Exited` 仅在 watcher 模式触发一次。
4. 本期 A 已引入的 `TaskRunOutcome`、取消令牌等待逻辑整体迁入 `WaitAsync`，`TaskRunner` 变薄：展开模板变量 → 解析脚本 → 包装解释器 → `TryStart`（启动 + 作业对象策略）→ 按 mode 分派（wait 等待 / detach 交调用方管理句柄）。

## 6. 调度器改造（TaskSchedulerService）

- `_running` 从 `Dictionary<string, bool>` 升级为 `Dictionary<string, RunSlot>`（`RunSlot { TaskProcessHandle Handle }`）。**槽存在即"有实例在运行"**（wait/detach 统一，防重逻辑不变：命中且 `!allowConcurrent` → skip）；`Handle` 在进程真正启动后回填——槽先于进程存在，启动窗口期内的并发触发不会漏判。wait 模式在 finally 清槽，detach 模式的槽由退出 watcher 清理（`ReferenceEquals` 比对防误删新实例的槽）。
- `ExecuteAsync` 分派：
  - **wait**：现状不变（等待、retry 循环、AddRecent、NotifyOnFailure），`RunAsync` 经 `onStarted` 回调把句柄回填进槽。
  - **detach**：`StartDetached` 成功 → 句柄入槽 → recent 记 `started` → 订阅 `Exited` → `BeginExitWatch`；函数立即返回。启动失败 → 清槽 + recent `fail` + 失败通知。
- **失败通知语义**（detach）：`NotifyOnFailure` 变为"进程**意外**退出（非用户停止）且退出码非 0（或退出码未知）时通知"。正常被 Stop 不通知。
- **recent 列表**：detach 任务记录 `started` 条目（而不是等退出），退出时由 watcher 追加 `ok/fail/stopped` 条目。
- `Stop()` / 宿主退出：遍历注册表，`killWithHost=true` 的 handle 停掉（job 关闭自然连带）；`false` 的只解除跟踪、不杀进程，日志记 "host exiting, detached process left running (pid=x)"。
- 任务被**禁用/删除/改名**时实例正在运行：默认**不杀**，注册表继续跟踪到退出；期间再触发不启动（同槽防重）；任务重新启用后防重恢复。改名视为删除+新增（按 Name 匹配的槽自然失效）。
- `Apply/Reload` 重建触发器，**不动**运行注册表（触发器生命周期与进程生命周期解耦）。

## 7. 运行注册表与状态查询

不建议新建独立管理类：`_running` 字典升级为注册表本身（`Dictionary<string, TaskProcessHandle>`，沿用 `_lock`）。对外提供：

```csharp
public IReadOnlyList<TaskRunInfo> GetRunning();   // name, pid, startedAt, duration, mode
public bool IsRunning(string taskName);
public bool TryStop(string taskName);             // UI / 二期 CLI 用
```

## 8. UI（任务编辑器）— B1 实际落地

1. **运行状态**（B1 简化，偏离 §8.1 原案）：不做逐行徽标（需要 TaskDefinition 引入 INPC，收益低扰动大），改为**底栏选中任务状态**：1s DispatcherTimer 轮询 `GetRunning()`，显示 `运行中 pid=x (mm:ss)` 并露出"停止任务"按钮；按任务名查询（编辑器与调度器的任务对象可能不是同一实例，按名最可靠）。逐行徽标延后到有 INPC 改造时再做。
2. **表单**：Options 区新增"运行模式"下拉（标准执行/后台常驻）+ "随宿主退出终止"复选框（仅 detach 时可见）；detach 时显示约束提示。校验与模型层一致（友好文案先行，`TaskDefinition.Validate` 兜底）。
3. **测试运行对 detach 任务**（决策见 §15-4）：不应用 10s 兜底超时（不等待，没有卡死问题）；启动后提示"已启动 pid=x，后台运行中"；**句柄由编辑器全权持有、不进调度器注册表**——停止按钮或关窗（OnClosing）即终止测试进程树，等待模式测试运行的既有语义自然延伸，无失控实例；注册表语义保持纯净（只反映调度器启动的正式实例），且测试运行与正式实例的防重互不干扰。
4. 日志查看不变（TaskLogger 已有 5MB×4 代轮转，常驻洪流输出安全）。

## 9. CLI / MCP（二期）

按 `host.*` / `services.*` 现有 CommandDescriptor 模式注册：

| 能力 | 风险级 | 说明 |
|------|--------|------|
| `tasks.status` | ReadOnly | 任务清单 + 运行状态（pid/时长/mode），含全局开关状态 |
| `tasks.stop` | Medium（建议 PIN 策略对齐 services.*，见 §15-5） | 停止指定任务的运行实例 |
| `tasks.run` | Medium | 手动触发；detach 返回 `started pid`，wait 走既有路径（受超时上限约束，防止 CLI 挂死——建议 CLI 侧强制 `min(timeoutSec, 120)` 或纯 fire-and-forget） |

## 10. 保活 / 守护（三期，可选）

```
"options": { "mode": "detach", "restart": "on-failure", "restartDelaySec": 5, "maxRestart": 3 }
```

- 触发条件：watcher 报告意外退出 且 exitCode≠0 且非用户停止 且任务仍启用。
- 退避：`delay = restartDelaySec * 2^(n-1)`，封顶 10 分钟；滑动 10 分钟窗口内最多 `maxRestart` 次，超过则放弃并通知（防 crash 风暴）。
- 与 Startup 触发组合 = 事实上的"用户态服务管理器"；不引入 Windows 服务依赖。

## 11. 场景矩阵（逐项预期行为）

| # | 场景 | 预期行为 |
|---|------|----------|
| S1 | 宿主正常退出（托盘退出/更新重启），`killWithHost=true` | 常驻进程树随作业对象关闭被内核回收（现状语义） |
| S2 | 宿主崩溃 / 被任务管理器强杀，`killWithHost=true` | 同 S1（kill-on-close 由内核保证，不依赖宿主善后） |
| S3 | 宿主重启后旧 detach 实例仍在（`killWithHost=false`），任务再次触发 | 注册表为空、防重失效 → **会双实例**。由 `singleInstance`（§12）或文档指导（用 S4 姿势）缓解；此组合必须在文档中显著警告 |
| S4 | `Startup` + `detach` + `killWithHost=true`（**推荐的标准常驻姿势**） | 宿主退出杀旧实例，宿主启动重新拉起，天然单实例 |
| S5 | 任务禁用时实例在跑 | 实例继续运行，注册表继续跟踪；再触发不启动；重新启用后防重恢复；禁用≠停止（文档明示） |
| S6 | 任务删除/改名时实例在跑 | 同 S5；改名后旧槽按名失配 → 允许启动新实例；编辑器删除确认文案提示"实例仍在运行" |
| S7 | 手动停止（UI/CLI） | `Stop()` → 进程树终止 → watcher 报告 → recent 记 `stopped`；不触发失败通知、不触发 retry |
| S8 | 意外退出且 exitCode≠0 | watcher → recent `fail:code` + NotifyOnFailure 通知；（三期）按 restart 策略拉起 |
| S9 | `allowConcurrent=true` + detach | **校验拒绝**（§4），不给多实例口子 |
| S10 | `timeoutSec>0` + detach | 校验拒绝 |
| S11 | `retry>0` + detach | 校验拒绝 |
| S12 | Watch / Hotkey / Idle / SessionEvent 触发 detach 任务 | 全部支持（触发器与执行模式正交） |
| S13 | 启动失败（文件不存在 / workDir 无效） | 照现状记日志 + recent `fail`；不进注册表 |
| S14 | 作业对象 assign 失败（嵌套作业环境） | 照旧启动运行；Stop 走 taskkill /T /F 降级；日志记降级原因 |
| S15 | 常驻进程输出洪流 | TaskLogger 轮转已覆盖；不做限速（非目标） |
| S16 | 注销 / 关机 | 会话内进程随会话结束，无法守护（Windows 语义），文档明示 |
| S17 | 编辑器测试运行 detach 任务 | 启动即提示 + 停止入口；句柄由编辑器持有（不进注册表，§8.3）；无 10s 兜底；停止按钮/关窗即终止 |
| S18 | 双编辑器窗口并发测试运行 | 各自独立 handle，允许（与现状一致） |
| S19 | detach 进程启动成功但瞬间退出（<1s） | watcher 正常收尾：日志 + recent + 通知路径，与 S8 合流 |
| S20 | 宿主休眠/唤醒 | 常驻进程随系统挂起/恢复，调度器不干预；补触发逻辑（Daily/Cron catch-up）不受影响 |

## 12. singleInstance 检测（决策点，S3 的缓解）

| 方案 | 做法 | 评价 |
|------|------|------|
| A. 进程路径探测 | 启动前枚举同可执行路径的进程 | 同程序不同参数会误判；已运行实例可能非本任务拉起 |
| B. 任务级命名 mutex | handle 持有 `Local\CarroDesk.Task.<name>`，退出/停止自动释放 | 实现最简单、跨宿主重启有效；只防"本任务重复拉起"，不感知外部同程序实例 |
| C. 不检测 | 文档指导用 S4 姿势（Startup+killWithHost=true） | 零成本，但 killWithHost=false 的场景没有兜底 |

**建议**：B 作为 `singleInstance=true` 的实现（B1 只落字段与校验，B2 实现 mutex）；C 作为默认文档建议。

## 13. 兼容性与迁移

- 旧 tasks.json 零迁移（缺省值 = 现状行为）；新增字段全部可缺省。
- 文档更新清单：`AUTORUN-DESIGN.md` options 表与示例、`USAGE.md`、`tasks.sample.json`（加 detach 示例 + S4 推荐姿势注释）、`AI-AGENT-MANUAL.md`（随二期能力）。
- A 修复已合入的 `TaskRunOutcome` / 取消令牌是本设计的直接前驱：`RunAsync` 等待逻辑迁入 `TaskProcessHandle.WaitAsync`，签名保持向后兼容。

## 14. 测试计划

- 单元（真实进程级，沿用 `TaskRunnerTests` 探针模式）：
  - detach：启动即返回、pid 可查、watcher 收到退出码；
  - killWithHost=false：宿主模拟退出不杀子进程（以 handle.Dispose 不终止验证）；
  - killWithHost=true：宿主退出路径回收（现有 ProcessJob 语义回归）；
  - Stop()：进程树（含孙进程）确实终止；
  - 校验规则：§4 四条组合全部拒绝；
  - 防重：detach 实例运行期间再触发 skip；退出后可再触发；
  - watcher：意外退出 → recent/通知路径（通知服务打桩）。
- 手动走查：S1~S20 矩阵逐项过一遍（重点 S1/S2/S3/S4/S7）。
- 回归：现有 278 项测试全绿。

## 15. 决策记录（原开放问题，2026-09-29 拍板）

1. **`killWithHost` 缺省值 = `true`**。不改变升级后行为，孤儿进程只在用户显式选择时出现。（已实施）
2. **组合校验 = 拒绝**（错误信息给原因），不做静默忽略。加载期 `TaskDefinition.Validate` 与编辑器 `ValidateForm` 双层生效。（已实施）
3. **singleInstance：B1 只落字段与序列化，mutex 实现放在 B2**（不随 CLI/MCP 顺延——它与调度器/宿主生命周期相关，与 CLI 无依赖）。（字段已实施）
4. **测试运行实例不进注册表**——按"哪种实现更清晰简单可靠"权衡后确认：不进注册表 + 编辑器全权持有句柄（停止按钮/关窗即终止）。理由：注册表语义纯净（只反映调度器启动的正式实例，防重不被测试干扰）；编辑器持有句柄补上了"detach 测试实例失去跟踪"的唯一漏洞（停止/关窗强制回收），两条路径（wait/detach 测试运行）生命周期规则完全一致。（已实施）
5. **CLI/MCP（tasks.status/stop/run）放到最后**（原 B2 → 现在最后一期），口令策略届时再评估。

## 16. 分期落地

| 期 | 内容 | 状态 |
|----|------|------|
| B1 | TaskProcessHandle + mode/killWithHost/singleInstance 字段与校验 + 运行注册表（_running 升级为 RunSlot）+ UI 运行状态/停止任务 + detach 测试运行交互 + 任务编排文档 | **已完成（2026-09-29），全量 286/286 通过** |
| B2 | singleInstance 命名 mutex 实现与校验 | 待做 |
| B3 | supervise（restart/退避/maxRestart） | 待做 |
| B4 | CLI/MCP `tasks.status/stop/run`（按用户要求放最后） | 待做 |

每期独立可发布；B1 不改变任何现有 wait 任务的可见行为。
