# 任务模块日志系统诊断与重构方案（2026-11-14）

> 状态：**已实施（2026-11-14）**，见 [§9 实施结果](#9-实施结果)。
> 本文第 1、2 节保留诊断时的现状快照（行号为重构前），第 3～8 节是方案原稿；
> 其中 §3.2 的「过渡期兼容文件」与 §5 的「P3 移除兼容层」按用户决策 4 **不保留兼容层**，
> §3 的 UI 历史面板按决策 2 推迟，§3.3 的脱敏按决策 3 暂不做。最终以 §9 为准。
> 证据基于当时工作树（v1.4.4，`docs/CHANGES-20261114.md` 20:18 批次之后），所有行号均为重构前行号。
> 范围：`src/Modules/TaskScheduler/` 的任务日志，以及与之相连的宿主日志出口、UI、测试、文档。

---

## 0. 结论摘要

1. **根因是「一条日志写两处、且所有类型共用一个通道」**：`TaskLogger.Write()` 无条件把每一行同时写入 `logs/tasks.log` 和 `logs/task-<name>.log`（`Services/TaskLogger.cs:89-108`）。管理事件、任务调度事件、子进程 stdout/stderr 三类语义完全不同的数据，都从这一个函数进出，因此两个文件都必然混杂。
2. **运行历史事实上不存在**：只有 `TaskSchedulerService._recent`（内存 10 条 `HH:mm:ss <name> <status>`，`Services/TaskSchedulerService.cs:1465-1480`），不持久化、不含命令/耗时/输出，且 `GetRecent()` 在生产代码中**零调用**（仅 3 处测试调用）。UI 的「最近结果」来自内存状态机快照，重启即丢。
3. **一次运行没有身份**：没有 runId，日志只有时间戳和任务名；并发实例（单任务上限 5、全局 20）的输出交错在同一文件里，`triggered` / `started pid=` / `OUT` / `finished` 之间无法可靠归属。
4. **建议**：按「调度管理日志 / 单次运行日志 / 结构化运行历史索引」三层拆分，用 runId 贯穿一次运行的全生命周期，UI 增加运行历史面板；分 4 阶段落地，过渡期保留旧 API 兼容层，避免一次性击穿 512 个现有测试。

---

## 1. 现状实证

### 1.1 任务日志写入链路

| 环节 | 位置 | 行为 |
|---|---|---|
| 唯一落盘入口 | `Services/TaskLogger.cs:89-108` | `Write(taskName, level, message)` 生成 `yyyy-MM-dd HH:mm:ss.fff [level] [name] msg`，先写 `tasks.log`，只要 `taskName` 非空再写 `task-<name>.log` |
| 聚合文件 | `TaskLogger.cs:69-72` | `logs/tasks.log` |
| 单任务文件 | `TaskLogger.cs:63-67, 264-276` | `logs/task-<Sanitize(name)>.log`；非法字符替换为 `_`、截断 64 字符 |
| 子进程输出 | `TaskRunner.cs:324-333` → `TaskLogger.cs:111-121` | `OutputDataReceived` / `ErrorDataReceived` 回调里逐行以 `level="OUT"/"ERR"` 调用 `Write()`，即**同样双写两个文件** |
| 轮转 | `TaskLogger.cs:35-39, 202-251` | 单文件 5MB，保留 `.1/.2/.3` 三代；轮转检查按路径限频 5 秒 |
| 管理事件 | `TaskSchedulerService.cs` 约 60 处、`TriggerStateStore.cs:182-208`、`TaskFireLimiter.cs:303-327` 等 | 统一以 `TaskLogger.Warn("system", ...)` 等形式写入 |

### 1.2 全仓库文件日志出口（共 4 类，格式互不相同）

| 出口 | 文件 | 格式 / 轮转 |
|---|---|---|
| `TaskLogger` | `logs/tasks.log`、`logs/task-*.log` | 时间戳无方括号；5MB × 3 代 |
| `Host/Services/DefaultLoggerService.cs:33,47-61` | 根 `log.txt` | `[ts] [LEVEL] [module] msg`；1MB → `.1` |
| `Common/StaticLog.cs:27-31`、`App.xaml.cs:664` | 同一 `log.txt` | 另两种格式（秒级 WARN、`[CRITICAL]`），**均无轮转** |
| `App.xaml.cs:68-69,621-622` | `fatal.log` | 崩溃日志，**无轮转** |
| `Host/Commands/CommandAudit.cs:50-80` | `logs/ipc-audit.log` | 单行 JSON、参数 SHA-256 摘要；1MB → `.1` |

任务模块除少数生命周期点走 `ILoggerService` / `StaticLog`（`TaskSchedulerModule.cs:59,86,99,173,231,276`、`Triggers/SessionEventTrigger.cs:32,53`）外，**完全绕过宿主日志服务**。因此「任务管理日志」实际散落在 `tasks.log`、`task-system.log`、根 `log.txt` 三处。

### 1.3 运行历史与 UI

- `_recent`：内存 `List<string>`，上限 10 条（`TaskSchedulerService.cs:1465-1480`），16 处 `AddRecent` 生产，`GetRecent()` 仅被 `tests/CarroDesk.Tests/TaskPersistentRunTests.cs:279,545,776-788` 消费；接口 `ITaskSchedulerService` 未声明该方法。
- `TaskStateMachine` 的 `LastExitCode` / `LastFinishedAt` / `LastOutcome` 全为内存字段（`Services/TaskStateMachine.cs:15-25,338-356`），`GetOrCreateFsm` 每次 `new`（`TaskSchedulerService.cs:91-94`），配置重载还会 `Reset()`（`TaskSchedulerService.cs:383-386`）——重启/重载后全部丢失。
- `Views/TaskManagerWindow.xaml.cs:694-714`：「查看日志」只是 `File.Exists` 后用系统默认程序打开 `task-<name>.log`，**不读取内容**；`:699` 用未 `Sanitize` 的 `row.Name` 拼路径，与写入端是两套实现。`:716-726` 打开日志目录。
- `Views/TaskManagerWindow.xaml:316-317`「最近结果」列来自 `_scheduler.GetAllStates()` 快照（`.xaml.cs:354-359,1131-1141`），是当次进程内的单次状态，不是历史。
- `Views/TaskEditDialog.xaml:146` 硬编码提示 `logs/task-{name}.log`；`:393` 的「测试运行」只看返回码，不展示日志。**全仓库没有任何读取日志内容的 UI，也没有运行历史 UI。**

---

## 2. 问题清单

### P0（直接对应用户诉求，必须先解）

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | 管理日志与任务日志混杂 | `TaskLogger.cs:104-108` 无差别双写；`TaskSchedulerService.cs:180,182-184,223,265,289,411,1499-1530` 等管理事件与 `TaskRunner.cs:308` 运行事件走同一函数 | 单个任务的日志里塞满调度器启停、限流、状态持久化；`tasks.log` 里又是全部任务 + 全部管理事件 |
| 2 | 聚合日志被子进程输出淹没 | `TaskLogger.cs:111-121` + `Write()` 双写 | 文档声称 `tasks.log` 是「调度事件汇总」（`docs/USAGE.md:181,248`），实际包含每行 stdout；高频任务一次输出即可把 5MB 轮转窗口内的调度事件冲掉 |
| 3 | `"system"` 被当作任务名 | `TaskLogger.cs:105` 仅判空白；`Warn("system", ...)` 因此生成 `logs/task-system.log` | 产生一个不对应任何任务的伪任务日志文件；管理日志既在 `tasks.log` 又在这个文件里，重复且无归属 |
| 4 | 一次运行无边界、无 runId | `TaskRunner.cs:308,363`、`TaskProcessHandle.cs:147`、`TaskLogger.cs:91` | 只有 `started pid=` / `finished pid=` 带 pid，`OUT/ERR` 行不带；重试（`TaskSchedulerService.cs:803-866`）、并发实例、常驻任务重启的输出交错后无法归因 |
| 5 | 没有持久化运行历史 | `TaskSchedulerService.cs:1465-1480`、`TaskStateMachine.cs:15-25` | 用户要的「命令、时间、输出」目前只能靠翻纯文本猜；重启后连最近一次退出码都没有 |

### P1（可靠性、性能、安全）

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 6 | 每行输出同步开关文件 ×2 | `TaskLogger.cs:123-157`、`Write()` 双写 | 每行 2 次 `FileStream` 开关 + `Flush()`，且 `GetFileLock` / `RememberSizeLocked` / `RotateIfNeededLocked` 每行至少 4 次争用全局 `_metaLock`（`:166-187,206-212`）——所谓「每文件一把锁」被元数据锁抵消。`TaskLogger.cs:11-28` 的注释已明确：输出回调处理不及会写满 4KB 匿名管道、把子进程**阻塞挂起**，并自述异步落盘 + flush 屏障「属更大改动，另行评估」 |
| 7 | 输出无长度/行数/总量限制，无脱敏 | `TaskRunner.cs:308,324-333`、`TaskLogger.cs:111-121` | 命令行参数原样落盘（可能含口令/token），子进程可无限刷屏；IPC 侧已有 SHA-256 摘要的成熟做法（`CommandAudit.cs:90-116`）未复用 |
| 8 | 只有按字节轮转，没有按时间/次数的保留策略 | `TaskLogger.cs:35-36,202-251`；全 `src/` 无日志清理代码 | 历史被覆盖即永久丢失；磁盘上界 = 任务数 × 4 × 5MB，且旧文件永不删除 |
| 9 | 级别语义混乱、多套格式 | `TaskLogger.cs:119` 把 `"OUT"/"ERR"` 当 level；`DefaultLoggerService.cs:33` / `StaticLog.cs:27` / `App.xaml.cs:664` 三种格式 | 无法按级别过滤，`ERR`（stderr 流）与 `ERROR`（错误级别）混淆；跨文件排障要靠肉眼对时间 |
| 10 | UI 路径与写入端是两套实现 | `TaskManagerWindow.xaml.cs:699` 未 `Sanitize`；`TaskLogger.cs:264-276` | 正常配置下因 `TaskDefinition.Validate()` 限制 `[a-zA-Z0-9_-]{1,64}`（`Models/TaskDefinition.cs:270-278`）暂未暴露，但手工编辑/校验放宽即必然「暂无日志」；`Sanitize` 还会把不同名字映射到同一文件造成串写 |

### P2（一致性、可维护性）

- 11 时间戳为本地 `DateTime.Now`、无 UTC/无序号（`TaskLogger.cs:91`）；项目自身已有系统时钟回拨处理（`TaskFireLimiter.cs:153`），日志未做应对，排序会乱。
- 12 文档与代码矛盾：`docs/AUTORUN-DESIGN.md:218` 写 `*.1.log`，实际是 `task-<name>.log.1`；`docs/TASKS-PERSISTENT-RUN-DESIGN-20260928.md:144` 写「5MB×4 代」，代码是 `KeepGenerations=3`；`docs/USAGE.md:179` 仍写「最近运行」而该功能已移除（`docs/CHANGES-20261008.md:31`）。
- 13 子进程空行被静默丢弃（`TaskLogger.cs:118`）；`GetRecent()` 为无生产消费者死代码，语言包仍残留 `RecentRun` / `NoRecentTasks`。

---

## 3. 目标架构

### 3.1 三条数据通道严格分离

| 通道 | 内容 | 落盘 |
|---|---|---|
| **调度管理日志** | 调度器启停/重载、全局开关、触发器注册与告警、限流拒绝、状态持久化降级、宿主退出清理、配置错误 | `logs/scheduler.log`（结构化文本或 JSONL，固定分类字段，**绝不写入任何任务运行文件**） |
| **单次运行日志** | 该次运行的 header（命令、参数、工作目录、触发原因、开始时间）、带流标记与时间戳的 stdout/stderr、footer（结束时间、耗时、退出码、结束原因） | `logs/runs/<taskId>/<yyyyMM>/<runId>.log`，**一次运行一个文件** |
| **运行历史索引** | 每次运行一条可查询记录：runId、任务名、触发原因、命令、时间、耗时、退出码、结果、输出字节数、日志相对路径 | `logs/runs/history.jsonl`（append-only 事件流） |

### 3.2 建议目录布局

```
<DirPath>/logs/
  scheduler.log                 # 管理日志，1MB 轮转、保留 3 代
  scheduler.log.1 ...
  tasks.log                     # [过渡期兼容] 仅运行摘要事件，不再写 stdout
  task-<name>.log               # [过渡期兼容] 镜像最近一次运行，供旧 UI/测试读取
  runs/
    history.jsonl               # run.start / run.end / run.abandoned 事件流
    <taskId>/
      202611/
        20261114-203012-1234-0007.log
```

### 3.3 runId 与记录格式

- `runId = yyyyMMdd-HHmmssfff-<pid>-<seq>`（`seq` 进程内 `Interlocked` 递增）：按时间可排序、全局唯一、含 pid 便于和外部进程关联。
- `RunContext` 在一次运行创建时生成 runId，随 `TaskRunner.TryStart` → `TaskProcessHandle` → 输出回调 → 退出处理传递，`TaskSchedulerService` 的重试循环每轮生成**新的** runId（重试是独立运行，但共享同一个 `triggerId` 便于串联）。
- 运行日志固定三段（UTF-8 无 BOM，时间戳 ISO-8601 带本地偏移 + UTC 双记）：

```
=== RUN START runId=... task=... trigger=daily reason=... startUtc=... startLocal=... ===
CMD  file=powershell.exe args="-File backup.ps1 --target D:\bak" workDir=D:\scripts
ENV  (可选，脱敏后)
2026-11-14T20:30:12.123+08:00 OUT  ...
2026-11-14T20:30:12.130+08:00 ERR  ...
=== RUN END   runId=... endUtc=... durationMs=... exitCode=0 outcome=ok outputBytes=... lines=... ===
```

- 历史索引采用 **append-only 事件流**，而非「结束时写一条完整记录」：崩溃/被 kill 时 `run.end` 会缺失，因此启动时扫描上一次未闭合的 `run.start`，补写 `run.abandoned`（outcome=interrupted）。查询时按 runId 归并成一行展示。示例：

```json
{"ts":"2026-11-14T12:30:12.123Z","type":"run.start","runId":"...","task":"backup-docs","trigger":"daily","reason":"daily","command":"powershell.exe -File backup.ps1 --target D:\\bak","commandDigest":"sha256:...","workDir":"D:\\scripts","attempt":1,"logPath":"runs/backup-docs/202611/<runId>.log"}
{"ts":"2026-11-14T12:30:15.401Z","type":"run.end","runId":"...","exitCode":0,"outcome":"ok","durationMs":3278,"outputBytes":1204,"lines":18,"endedBy":"exited"}
```

- `command` 保留明文以便排障，`commandDigest` 供比对；提供 `logSensitiveArgs` 策略（默认对形如 `--password=xxx` / `token=` 的键值做掩码，参考 `CommandAudit.cs:90-116`）；文档需明确「日志会记录命令原文」。

### 3.4 组件划分

| 组件 | 职责 | 关键点 |
|---|---|---|
| `TaskLogPaths` | **路径单一真源** | `SchedulerLogPath` / `HistoryIndexPath` / `GetRunLogPath()` / `GetLegacyTaskLogPath()`；UI、测试、写入端统一调用，消除 `:699` 的重复拼接 |
| `SchedulerLogger` | 管理日志 | `Write(category, level, message, taskName?)`；category ∈ scheduler/trigger/state/config/host；`taskName` 仅作字段，**不触发写任务文件** |
| `RunContext` | 一次运行的身份与快照 | runId、命令、时间、pid、attempt、输出计数；`Complete()` 写 footer + 触发 `run.end` |
| `RunLogWriter` | 单次运行文件写入 | 每个 run 一个写者，`FileShare.ReadWrite` 常开流 + 批量/定时 flush；`Flush()` 显式屏障供 UI 打开日志、测试断言、进程退出前调用；`Dispose()` 必须先 drain 完输出缓冲 |
| `RunHistoryStore` | 索引读写 | append JSONL、查询（按任务/时间/结果分页）、启动补 `run.abandoned`、并发串行化 |
| `RetentionPolicy` | 保留与清理 | 默认 30 天 / 每任务 200 次 / 总 200MB；启动时 + 每日一次，先删 run 文件再压缩索引；策略进配置 |
| `OutputLimiter` | 输出保护 | 单默认 10MB / 单行 64KB 截断并写 `[truncated]` 标记；**超限后仍要继续读取管道**，只是不再落盘（否则回到 §2 P1-6 的阻塞问题） |
| UI `RunHistoryWindow` | 运行历史面板 | 按任务筛选、时间/触发原因/命令/耗时/退出码/结果列表，双击打开该次运行日志；「查看日志」改为打开最近一次运行或历史面板；任务管理器右键新增「运行历史」 |

---

## 4. 兼容约束（改即失败的硬依赖）

以下均需保留到迁移完成，不能一次性删除：

- 公共 API `TaskLogger.GetTaskLogPath()` / `GetAggregateLogPath()` 及返回路径：`tests/.../TaskRunnerTests.cs:80,96`、`CronHelperTests.cs:302`、`TaskFireLimitIdleTests.cs:161`、`TaskPersistentRunTests.cs:79,900-909`、`TemplateExpanderTests.cs:50`。
- 读后写一致：`TaskRunnerTests.cs:93-101`（写后立刻 `File.ReadAllText` 可见）、`:103-144`（8 线程 × 40 行不丢行、聚合日志同步含同一行）；注释 `:84-91` 明确三条约束来自真实调用方。
- 日志内的固定文本：`first-line`、`ARGS[...]`、`started pid=(\d+)`、`no occurrence within 8 years`、`idle deferred by fire limit`、`second`（`TaskRunnerTests.cs:99,150,156,273,320`；`CronHelperTests.cs:305`；`TaskPersistentRunTests.cs:909`；`TaskFireLimitIdleTests.cs:166`）。
- `LocalizationContractTests.cs:148-153` 白名单字面串 `唯一标识，日志将自动写入 logs/task-{name}.log`；`TaskEditDialog.xaml:146` 同文案；`CarroDeskContractTests.cs:266-284` 的 `Tray.OpenTaskLogsDir` 中英文案；`{{logs}}` 模板展开。
- 文档 7+ 处写死目录布局：`STORAGE-ARCHITECTURE-CURRENT-STATE-20260918.md:28-31,74-84,190-215`、`USAGE.md:14,80,158,181,244-250`、`AUTORUN-DESIGN.md:29,68-69,95,211-218`、`TASKS-PERSISTENT-RUN-DESIGN-20260928.md:144,218`、`DESIGN.md:120`、`I18N-DESIGN.md:142` 等。
- 现有基线：`dotnet test` 512 通过 / 0 失败 / 0 跳过，`Release` 构建 0 警告 0 错误（`docs/CHANGES-20261114.md`）。

**兼容策略**：过渡期保留 `TaskLogger` 的旧静态方法作为**兼容外观**——`Info/Warn/Error/WriteOutput` 内部转发到新组件，并继续镜像写 `task-<name>.log` 与 `tasks.log`（`tasks.log` 不再接收逐行 stdout，只接收运行摘要事件）；新代码一律使用 `SchedulerLogger` / `RunContext` / `RunLogWriter`。旧输出在 UI 与测试全部迁移后，随一个明确版本移除。

---

## 5. 分阶段实施

| 阶段 | 内容 | 可独立上线 | 验收 |
|---|---|---|---|
| **P0 止血**（不动存储结构） | ① `Write()` 按 `category` 分流：管理事件只进 `scheduler.log`，运行事件进任务文件；② 取消 `WriteOutput` 对 `tasks.log` 的双写；③ 废除 `"system"` 作为任务名（改为显式 `category=system` 且只写管理日志）；④ `TaskLogPaths` 统一 UI 路径；⑤ 校正文档中轮转命名/代数的矛盾 | 是，风险最低，可先发一个小版本 | 管理日志 0 行出现在任务文件；`tasks.log` 不含 `OUT/ERR` 行；512 测试全绿 |
| **P1 运行上下文** | 引入 `RunContext` + runId，改造 `TaskRunner.TryStart` / `TaskProcessHandle.WaitAsync` / `TaskSchedulerService` 重试与 detach 退出链；每次运行独立文件，header/footer 齐全；`RunLogWriter` 异步落盘 + `Flush()` 屏障；`OutputLimiter` | 是 | 一次运行一个文件且 start/end 成对；5 并发实例输出不串；高频输出压测（如 10 万行）子进程不阻塞 |
| **P2 历史索引与保留** | `RunHistoryStore`（append 事件流 + 启动补 `run.abandoned`）、`RetentionPolicy`、崩溃/被 kill 场景补齐、脱敏策略 | 是 | 重启后历史可查；kill -9 后该次运行标记 interrupted；保留策略生效且不误删运行中文件 |
| **P3 UI 与迁移** | 运行历史窗口、右键入口、「查看日志」改指向具体 run、测试改为断言结构化记录、文档与语言包同步、移除兼容层 | 否（需前序就绪） | 用户可在 UI 按任务查看命令/时间/耗时/退出码/输出；旧测试全部迁移；构建 0 警告 0 错误 |

每阶段结束均运行 `dotnet test` 与 `dotnet build CarroDesk.slnx -c Release`；P1/P2 需新增并发、崩溃、限流、保留策略用例。

---

## 6. 关键取舍与风险

| 项 | 取舍 | 理由 / 风险 |
|---|---|---|
| 第三方日志库（Serilog/NLog） | **不引入** | 项目现为手写零依赖日志、`Newtonsoft.Json 13.0.4` 已在用（`src/CarroDesk.csproj:54`）；结构化 JSONL 用现成序列化即可，避免为托盘工具增加依赖与配置面 |
| 异步落盘 | 采用，但必须保留 `Flush()` 屏障 | `TaskLogger.cs:26-28` 已论证异步会破坏读后写一致性；方案把屏障做成显式 API，UI 打开日志/测试断言/进程退出前调用，兼顾两者 |
| 常开写句柄 | per-run 一个，`FileShare.ReadWrite` | 旧注释（`:30-31`）担心与 `File.ReadAllText(FileShare.Read)` 冲突；`FileShare.ReadWrite` + 及时 flush 可解，但需在 Windows 上实测外部记事本打开行为 |
| 文件数膨胀 | 按 run 保留 + 可选压缩归档 | 高频任务每天上千次会产生上千小文件；保留策略必须按「次数 + 天数 + 总量」三重约束，否则磁盘 inode/遍历退化 |
| 崩溃/被杀 | 事件流 + 启动补 `run.abandoned` | 不依赖「结束时才写记录」；残留 `run.start` 用宿主启动时间判定为 interrupted |
| 敏感信息 | 默认键值掩码 + `commandDigest`，明文命令仍进 run 日志 | 需文档明确告知；如要求更严，可加 `maskCommandInHistory` 开关，但会损失排障能力 |
| 旧 `tasks.log` / `task-<name>.log` | 过渡期镜像保留，之后移除 | 涉及 6+ 测试文件、2 处 UI 文案、7+ 文档；一次性改完风险过高 |
| 宿主 `log.txt` 三源并存 | 本次**不合并**，仅在 P0 明确边界 | 属宿主日志体系问题，与任务日志解耦；建议后续单独立项统一 `ILoggerService` |

---

## 7. 验收标准（重构完成态）

1. 任一任务运行文件中不含调度器启停、限流拒绝、状态持久化等管理事件；`scheduler.log` 中不含任何 stdout/stderr 行。
2. 每次运行有唯一 runId，对应唯一日志文件与一条可查询历史记录；重试、并发、常驻重启各自独立且可归因。
3. 历史记录包含命令（脱敏后）、触发原因、开始/结束时间、耗时、退出码、结果、输出字节数、日志路径，宿主重启后仍可查。
4. 子进程高频输出不因日志写入被阻塞；单次输出超限被截断并留标记，管道仍被完整读取。
5. 保留策略按天数/次数/总量生效，运行中文件不被删除。
6. UI 可按任务筛选历史并直接打开该次运行日志；「查看日志」不再出现 `NoLogFile` 误报。
7. `dotnet test` 全绿（含新增用例），`Release` 构建 0 警告 0 错误；文档与语言包同步更新。

---

## 8. 产品决策（2026-11-14 已确认）

| # | 决策 | 落地 |
|---|---|---|
| 1 | 保留策略默认值合适 | `TaskLogPaths` 常量：30 天 / 每任务 200 次 / 单次输出 10MB / `runs` 总计 200MB；单行 64KB 截断 |
| 2 | 先不加 UI，后续再加 | 不新增运行历史面板；仅把任务管理器「日志」按钮改为打开该任务最近一次 run 文件（先 `FlushActive()`），「打开日志目录」改走 `TaskLogPaths.EnsureDirs()` |
| 3 | 暂时不做脱敏 | 命令与参数原样写入 run 文件 header 与 `history.jsonl`；文档明确提示 |
| 4 | 不兼容 | 删除 `GetTaskLogPath` / `GetAggregateLogPath` / `EnsureLogDir` / `WriteOutput` 及 `tasks.log`、`task-<name>.log`、`task-system.log`；测试、语言包、XAML 文案、8 份文档同步迁移 |
| 5 | 不需要 IPC/CLI | 运行历史暂不对外暴露，仅文件与 `RunHistoryStore.Query()` 进程内 API |

## 9. 实施结果

### 9.1 最终目录布局

```
<DirPath>/logs/
  scheduler.log                                  # 管理事件，5MB × 3 代轮转
  scheduler.log.1 ~ .3
  runs/
    history.jsonl                                # append-only 事件流：run.start / run.end / run.abandoned
    <taskName>-<8位名字哈希>/
      <yyyyMM>/
        <yyyyMMdd-HHmmssfff>-<hostPid>-<seq>.log  # 一次运行一个文件
```

run 文件三段式：`=== RUN START ===`（runId、命令、参数、工作目录、触发原因、attempt、UTC/本地时间）→ 带时间戳的 `OUT` / `ERR` 行 → `=== RUN END ===`（结束时间、durationMs、exitCode、outcome、outputBytes、outputLines、truncated、endedBy）。

### 9.2 代码变更

- 新增 `Services/Logging/TaskLogPaths.cs`：路径唯一真源 + 保留策略常量；任务目录名带名字哈希，消除 Windows 大小写不敏感与 64 字符截断造成的碰撞。
- 新增 `Services/Logging/RunLogWriter.cs`：内存缓冲 + 每 64 行/250ms 批量追加 + 显式 `Flush()` 屏障。无常开写句柄（外部可读），stdout 回调不碰磁盘（不阻塞 4KB 管道）；单行 64KB、单次 10MB 截断后仍继续 drain。
- 新增 `Services/Logging/RunContext.cs`：runId 贯穿 header/输出/footer，`Complete()` 幂等，`Abandon()` 覆盖启动失败。
- 新增 `Services/Logging/RunHistoryStore.cs`：JSONL 事件流、`Query()` 归并、`RecoverAbandoned()` 崩溃补齐、`GetLatestRunLogPath()` 给 UI、`Prune()` 按天/次/总量淘汰并原子重写索引（活跃运行不淘汰）。
- 重写 `TaskLogger.cs`：只写 `scheduler.log`，格式 `ts [LEVEL] [task=<name>] msg`；`"system"` 归一化为字段值，不再产生伪任务文件；删除全部旧路径 API。
- `TaskRunner` / `TaskProcessHandle`：每次运行创建 `RunContext`，运行期事件与 stdout/stderr 写 run 文件，`WaitAsync` 结束写 footer + `run.end`；管理事件仍走 `TaskLogger`。
- `TaskSchedulerService.Start()` 调用 `RunHistoryStore.Instance.Initialize()`（崩溃恢复 + 每日清理）。
- UI / 语言包 / XAML：5 个中英 key、编辑器提示、本地化白名单同步到新布局。
- 升级前已存在的 `logs/tasks.log` / `logs/task-<name>.log` / `task-system.log` **不自动删除、不迁移**（避免误删用户排障资料），由用户自行清理；新日志不再写入这些文件。

### 9.3 测试

- 迁移 4 个测试文件（`TaskRunnerTests`、`CronHelperTests`、`TaskFireLimitIdleTests`、`TaskPersistentRunTests`）到新读取入口 `TaskLogTestHelper`（管理日志 / 运行日志分开读）。
- 新增 `TaskLoggingTests.cs` 9 项：runId 唯一与独立文件、header/footer 字段、history 归并查询、崩溃补 interrupted、按天数/次数保留、输出截断、system 字段化与输出不泄漏、目录名哈希防碰撞。
- 全量：`dotnet test` **534 通过 / 0 失败 / 0 跳过**（原 525 + 9）。

### 9.4 运行历史 UI（2026-11-14 追加）

- 任务右键菜单新增「🕘 运行历史」，打开 `Views/RunHistoryWindow`：每行一次运行，列为开始时间（本地）、触发方式、命令、耗时、结果、输出统计；运行中显示 `running`，输出统计显示 `-`（`run.start` 不含统计）；双击打开该次 run 日志；底部提供刷新 / 打开日志目录 / 关闭。
- 数据来自 `RunHistoryStore.Query(task, 200)`，打开与刷新前调用 `FlushActive()` 落盘缓冲。
- 顺带修复：`RunHistoryStore.TryParse` 改用 `JsonTextReader` + `DateParseHandling.None`。此前 `JObject.Parse` 会把索引里的 ISO 时间转成 `DateTime`，取回字符串时丢失时区，历史时间按 UTC 原样显示（少一个时区偏移）。
- 本地化：中英各新增 10 个 `Tasks.*` key，XAML 全部走 `loc:Loc`；新增 `RunHistoryUiTests` 5 项格式化用例（含运行中输出显示 `-`）。

### 9.5 未纳入本次范围

命令参数脱敏、IPC/CLI 查询接口、宿主 `log.txt` 三套写入源的统一（属宿主日志体系，建议单独立项）。`TaskLastRunStore`（`task-last-run.json`，管理窗口「最近运行」列）保持不变：它是 UI 快路径，与全量运行历史职责不同。
