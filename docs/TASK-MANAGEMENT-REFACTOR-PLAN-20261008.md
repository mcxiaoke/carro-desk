# 自动化任务管理重构与体验优化方案 (2026-10-08)

- **文档标识**：`docs/TASK-MANAGEMENT-REFACTOR-PLAN-20261008.md`
- **关联设计**：`docs/AUTORUN-DESIGN.md`、`docs/TASKS-PERSISTENT-RUN-DESIGN-20260928.md`、`docs/USAGE.md`
- **目标范围**：任务进程生命周期管控（启动/停止/重启）、运行状态与配置状态彻底解耦、编辑器交互保护、触发器补全（Watch/Daily/Interval）、执行引擎加固（SingleInstance/编码/环境变量）与托盘控制。
- **暂缓范围**：IPC / CLI / MCP 能力开放（按指示暂缓实施）。

---

## 一、核心痛点根因分析

### 1. “我把进程停掉了，任务还显示启动”之谜
经代码核对，该现象由三层原因叠加导致：

1. **配置开关与运行状态混淆（假绿灯）**：
   * 在 `TaskDefinition.cs` 中：
     ```csharp
     public string StatusIndicator => Enabled ? "🟢" : "⚪";
     public string StatusColor => Enabled ? "#10B981" : "#9CA3AF";
     ```
   * 界面左侧列表绑定的圆点是 `Enabled`（配置启用），**根本不是进程运行状态**！
   * 即使进程从未启动或已被外部强杀，只要任务是勾选启用的，列表里就永远亮着绿灯 🟢，造成用户误以为“任务进程还在跑”。
2. **后台守护（RestartOnFailure）自动拉起“杀不死”**：
   * 对于开启守护的常驻任务（`restart: "on-failure"`），若用户在任务管理器中杀掉进程（非 0 退出）：
   * `OnDetachedExited` 判定为异常退出，将槽位置为等待重启，并在 5 秒后自动拉起新进程。用户在外部杀进程，任务在数秒内又重新复活，表现为“停不掉、一直显示启动”。
3. **Wait 模式停止不及时与槽位滞后**：
   * `TryStop` 调用 `h.Stop()` 杀进程，但 wait 模式下的 `RunAsync` 依然阻塞在 `WaitForExitAsync` 等待缓冲区排空，若子进程存在未关闭的继承句柄，槽位不会立即移除；
   * 编辑器状态展示依赖 1 秒 DispatcherTimer 轮询，且只对“当前选中的任务”显示底栏文本，无法全局感知各任务运行态。

### 2. 编辑器缺乏直接控制手段
* 当前任务编辑器只有“测试运行”（`OnTestRunClick`，不进入调度器在册表）和“停止测试”；
* 针对调度器真正托管的任务，只有在选中且恰好处于运行态时才露出一个次级的“停止任务”按钮；
* **完全没有【▶ 立即运行】、【⏹ 停止】、【🔄 重启】的成套标准控制动作**，用户要手动运行必须关闭窗口去托盘菜单翻找。

---

## 二、方案总体设计与架构规划

```
┌────────────────────────────────────────────────────────────────────────┐
│                          用户交互与呈现层                                │
│  ┌──────────────────────────────┐     ┌─────────────────────────────┐  │
│  │ 任务编辑器 (TaskEditorWindow) │     │ 托盘菜单 (TaskSchedulerMod) │  │
│  │  - 运行状态指示 (🟢/🟡/🔴/⚪) │     │  - 运行中任务 (N) 清单      │  │
│  │  - 显式操作: 启动/停止/重启   │     │  - 逐实例一键停止/重启      │  │
│  │  - Dirty State 未保存拦截     │     │  - 手动运行去重友好提示     │  │
│  └──────────────┬───────────────┘     └──────────────┬──────────────┘  │
└─────────────────┼────────────────────────────────────┼─────────────────┘
                  │ 广播事件 / 交互请求                  │
┌─────────────────▼────────────────────────────────────▼─────────────────┐
│                    任务调度核心 (TaskSchedulerService)                   │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │ 状态管理器 (TaskStateManager)                                     │  │
│  │  - 严格区分 ConfigState(Enabled) 与 RuntimeState(Running/Idle/...)│  │
│  │  - 状态跃迁事件推送 (TaskStateChanged) -> 毫秒级 UI 响应           │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │ 实例管控增强 (Instance Lifecycle)                                 │  │
│  │  - RunManual / TryStop / RestartTask 标准生命周期                │  │
│  │  - Stop 立即熔断取消守护重启倒计时，杜绝“杀不死”                 │  │
│  │  - 进程存活性心跳复核 (Liveness Check) -> 外部被杀秒级出册        │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└─────────────────┬────────────────────────────────────┬─────────────────┘
                  │                                    │
┌─────────────────▼──────────────┐   ┌─────────────────▼─────────────────┐
│     触发器引擎 (Triggers)       │   │     执行引擎 (TaskRunner)         │
│  - Watch: 注入 {{file}} 上下文  │   │  - Detach: PID 状态持久化防重复   │
│  - Daily: 关机/晚开机补偿启动   │   │  - 编码: OEM/UTF-8 自适应防乱码   │
│  - Interval: 启动即跑+休眠唤醒  │   │  - 环境: 注入自定义 Env 字典      │
└────────────────────────────────┘   └───────────────────────────────────┘
```

---

## 三、各模块详细实施方案

### 1. 任务进程管理与运行状态彻底解耦（用户核心痛点）

#### 1.1 状态定义明确分离
彻底拆解原有的假状态：
* **配置状态 (Configuration State)**：
  * `TaskDefinition.Enabled`：仅代表计划任务总开关是否开启；
  * UI 呈现：左侧任务项用标准 `CheckBox` + 名称透明度（禁用时文本变灰 `#9CA3AF`），**移除一切用绿色圆点表示 Enabled 的代码**。
* **运行时状态 (Runtime State)**：
  新增枚举 `TaskRuntimeState`：
  ```csharp
  public enum TaskRuntimeState
  {
      Idle,           // ⚪ 空闲（未在运行）
      Running,        // 🟢 运行中（包含 Pid、已运行时长）
      Supervising,    // 🟡 守护等待中（包含倒计时秒数、重试轮次）
      FailedMarked,   // 🔴 连续失败熔断（已停止自动拉起，等待用户干预）
  }
  ```

#### 1.2 运行时状态感知与广播
* 在 `TaskSchedulerService` 中增加事件机制：
  ```csharp
  public event Action<string, TaskRuntimeState, TaskRunInfo> TaskStateChanged;
  ```
* 触发时机：
  1. 进程成功启动并获得 PID 时 -> 广播 `Running`；
  2. 进程退出、未开启守护或正常退出（ExitCode 0）时 -> 广播 `Idle`；
  3. 异常退出进入守护延迟期间 -> 广播 `Supervising`；
  4. 守护重试超预算熔断时 -> 广播 `FailedMarked`；
  5. 用户调用 `TryStop` 停止时 -> 立即广播 `Idle`（同步清空守护队列）。
* **外部强杀感知（Liveness Check）**：
  在服务内部的轻量定时检查中，对在册的 `RunSlot` 进行 `Process.GetProcessById(Pid)` 存活核对，若外部通过任务管理器杀掉且未触发 watcher，主动标记退出并广播状态，杜绝界面残留。

#### 1.3 编辑器完备的【启动 / 停止 / 重启】控制
* **界面调整**：
  在 `TaskEditorWindow` 底栏将原来的单一测试运行按钮区改造为**完整的运行控制工具栏**：
  * **【▶ 启动】(Run)**：
    * 若当前任务未在运行：调用 `Scheduler.RunManual(taskName)` 立即调度执行；
    * 若当前任务已在运行：按钮禁用，或提供悬浮提示“当前实例运行中 (PID: xxx)”；
  * **【⏹ 停止】(Stop)**：
    * 仅在状态为 `Running` 或 `Supervising` 时高亮启用；
    * 点击后执行 `Scheduler.TryStop(taskName)`，不仅杀掉进程树，**同时强制取消挂起中的重启定时器（Cancel Pending Supervise）**，确保进程彻底停止，不会再次复活；
  * **【🔄 重启】(Restart)**：
    * 仅在运行或守护中启用；
    * 逻辑：执行 `TryStop` -> 等待退出（或 1.5s 强制终结） -> 立即调用 `RunManual` 重新拉起；
  * **【⚡ 测试运行】(Test Run)**：
    * 保留作为“使用表单临时参数试跑”，与宿主计划任务解耦。

---

### 2. 一1：编辑状态未保存保护（Dirty State 拦截）

#### 2.1 触发与识别
* 在 `TaskEditorWindow` 中维护 `_isDirty` 标记和 `_originalTaskSnapshot`（深拷贝或原始 JSON 镜像）；
* 为表单所有控件（`NameBox`, `TriggerTypeBox`, `DelayBox`, `FileBox`, `ArgsBox`, `WorkDirBox`, `RunModeBox` 等）绑定变更事件；
* 当用户修改任何内容导致表单数据与当前选中的任务不一致时，设置 `_isDirty = true`；
* 窗口标题右侧动态显示 `*` 标识（如 `任务编辑器 - [frpc-daemon]*`）。

#### 2.2 拦截场景
在以下三种场景进行拦截检查：
1. **切换左侧任务列表项**（`TaskList_SelectionChanged`）；
2. **点击【新增】或【复制】任务**（`OnAddClick` / `OnDuplicateClick`）；
3. **关闭窗口**（`OnClosing`）。

#### 2.3 交互对话框
检测到 `_isDirty == true` 时，弹出确认框：
> **当前任务 "{0}" 有未保存的修改，是否保存？**  
> 【保存】(Yes) / 【放弃修改】(No) / 【取消】(Cancel)

* **保存**：先触发 `ValidateForm` 校验，通过后自动写入内存与磁盘，再继续执行原目标操作；若校验失败则停留在当前表单并高亮报错字段；
* **放弃修改**：丢弃改动，允许切换或关闭；
* **取消**：中断切换动作，恢复原列表选中项。

---

### 3. 二1：文件监听（Watch）参数传递与文件锁排队

#### 3.1 模板上下文拓展（支持 `{{file}}`）
* **改造 `TemplateExpander`**：
  将签名从 `Expand(string text, TaskDefinition task)` 扩展为：
  ```csharp
  public static string Expand(string text, TaskDefinition task, TaskExecutionContext context = null)
  ```
* **新增变量支持**：
  | 变量 | 含义 | 示例 |
  |---|---|---|
  | `{{file}}` / `{{filePath}}` | 触发事件的目标文件完整绝对路径 | `C:\Downloads\setup.zip` |
  | `{{fileName}}` | 触发变动的文件名（含后缀） | `setup.zip` |
  | `{{fileBaseName}}` | 触发变动的文件名（不含后缀） | `setup` |
  | `{{fileExt}}` | 文件扩展名 | `.zip` |
  | `{{fileDir}}` | 触发变动的文件所在目录路径 | `C:\Downloads` |
  | `{{fileEvent}}` | 变动事件类型 | `created`, `changed`, `renamed` |

#### 3.2 文件占用锁排队检查（File Readiness Waiter）
* **痛点**：下载大文件或解压时，文件刚创建还在写入，脚本 500ms 内启动必然报错 `The process cannot access the file because it is being used by another process`。
* **方案**：
  在 `FileWatcherTrigger` 触发任务执行前，增加“文件写入就绪检测”：
  * 检测方法：尝试以 `FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` 打开；
  * 重试策略：若被独占，以 500ms 为步长重试最多 6 次（总计等待 3 秒）；
  * 仅当文件可读，或者达到最大等待时间后才拉起任务进程；
  * 如果文件在等待期间被删除（临时文件），则直接取消执行并记日志，消除大量因临时文件引发的假失败。

---

### 4. 二2：每日定时（Daily）开机漏跑补偿机制

#### 4.1 痛点根因消除
* 废除构造函数中强制将 `_lastFiredDate` 置为今天的做法；
* 状态持久化：在本地存储 `tasks.state.json` 中记录每个任务的 `LastFiredTime`（最后成功执行的时间戳）。

#### 4.2 配置项与补偿逻辑
* 在 `TaskTrigger` 中增加选项：`CatchUpOnStartup: bool`（每天定时任务默认勾选 `true`，界面提供【开机错过时自动补跑】选项）；
* **判定逻辑**：
  CarroDesk 启动初始化时（或从休眠唤醒时）：
  1. 获取该任务在 `tasks.state.json` 中的 `LastFiredTime`；
  2. 若 `LastFiredTime.Date < DateTime.Today`（即今天还没有执行过）：
  3. 且当前系统时刻已经超过了设定的目标时间点（`DateTime.Now >= DateTime.Today + _at`）：
  4. 触发补偿执行，附带原因 `reason: "daily-catchup"`；
  5. 记录 `_lastFiredDate = DateTime.Today`，持久化状态，防止重复补偿。
* **效果**：用户设定每天 09:00 备份，即使 09:30 才打开电脑开机，启动后 10 秒内即可自动补跑今天的日常任务，不再无声无息漏跑。

---

### 5. 二3：周期循环（Interval）启动即跑与休眠唤醒对齐

#### 5.1 启动即跑支持（`RunAtStartup`）
* 在 `TaskTrigger` 增加 `RunAtStartup: bool`（配置项，默认 `false`，编辑器界面提供【启动时立即执行一次】勾选框）；
* 若为 `true`：`IntervalTrigger.Start()` 建立定时器时，首先异步派发一次执行（`reason: "interval-startup"`），随后开始周期计时。

#### 5.2 休眠唤醒对齐（Sleep Resume Catch-Up）
* `IntervalTrigger` 记录 `_nextExpectedTime`（下一次计划触发时刻）；
* 在 `TaskSchedulerService.OnPowerModeChanged` 捕获到 `PowerModes.Resume` 时：
  * 遍历所有 `IntervalTrigger`，调用其 `CheckCatchUp()`；
  * 若休眠期间当前时间已经越过 `_nextExpectedTime`：
    立即补跑一次（`reason: "interval-catchup"`），并将下一个周期重新基于当前时刻对齐，彻底消除笔记本合盖导致的任务严重滞后。

---

### 6. 三1：跨宿主重启的单实例互斥（SingleInstance 与 Detach 进程管理）

#### 6.1 痛点根因消除
宿主不能只靠进程内的 `Local\CarroDesk.Task.xxx` 命名互斥体，因为宿主退出时句柄必然释放，导致旧子进程还在跑，新宿主却检测不到。

#### 6.2 基于 PID + 进程签名验证的持久化接管
* **状态持久化**：
  在 `tasks.state.json` 登记所有活跃 detach 任务：
  ```json
  {
    "frpc-daemon": {
      "pid": 14280,
      "processName": "frpc",
      "executablePath": "C:\\Tools\\frpc.exe",
      "startTimeUtc": "2026-10-08T07:12:00Z"
    }
  }
  ```
* **宿主启动/任务拉起前的双重核验**：
  当一个启用了 `singleInstance: true` 的 detach 任务准备启动时：
  1. 检查 `tasks.state.json` 中是否存在上一代实例记录；
  2. 若记录存在 PID：通过 Win32 API 检查系统中该 PID 的进程：
     * PID 存在且启动时间匹配、进程名匹配 -> **实锤旧实例仍然存活**；
     * 将其重新挂接回宿主的 `_running` 注册表（接管监控，不再新开进程）；
     * 记日志：`singleInstance: adopted existing running process pid=14280`；
     * 跳过本次创建，返回 `StartSkippedSingleInstance`；
  3. 若 PID 已不存在或被系统复用为其他进程 -> 清理过期记录，安全启动新进程并更新状态文件。
* **效果**：即使 `killWithHost: false`，CarroDesk 退出重启多次，也绝不会出现重复拉起多个后台服务实例的情况。

---

### 7. 三2：命令执行输出编码自适应（彻底解决中文乱码）

#### 7.1 问题模型
* Windows 下 `cmd.exe`、`.bat`、系统原生命令（`ping`, `netstat`, `ipconfig` 等）默认使用系统 OEM 代码页（中文 Windows 为 CP936 / GBK）；
* `ProcessStartInfo` 强制 `StandardOutputEncoding = Encoding.UTF8` 会导致非 UTF-8 文本解码严重碎裂成乱码。

#### 7.2 自适应编码策略
在 `TaskRunner.TryStart` 中调整编码选择规则：
1. **配置层允许显式指定**：
   `TaskOptions.Encoding` 默认为 `"auto"`，支持 `"utf-8"`, `"gbk"`, `"oem"`；
2. **`auto` 智能探测逻辑**：
   * 脚本类型为 `.ps1`、`.js`、`.py` 时，默认使用 `Encoding.UTF8`；
   * 脚本类型为 `.bat`、`.cmd` 或直接调用系统可执行程序（`.exe`）时，默认回退使用系统 OEM 代码页：
     ```csharp
     Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
     ```
   * 确保批处理中的中文 echo 和系统工具的中文报错信息能原汁原味、清晰无乱码地写入任务日志。

---

### 8. 三3：任务级自定义环境变量支持（Env Vars）

#### 8.1 模型与配置拓展
* 在 `TaskDefinition` 的 `TaskAction` 中增加：
  ```csharp
  public Dictionary<string, string> Env { get; set; } = new Dictionary<string, string>();
  ```
* `tasks.json` 支持直观声明：
  ```json
  "action": {
    "file": "sync.py",
    "args": "--verbose",
    "env": {
      "HTTP_PROXY": "http://127.0.0.1:7890",
      "NODE_ENV": "production",
      "API_TOKEN": "my-secret-key"
    }
  }
  ```

#### 8.2 编辑器界面与执行层
* **UI**：在编辑器“执行配置 (Action)”面板增加折叠区域“环境变量”，支持简洁的键值表格编辑或多行文本输入（`KEY=VALUE`）；
* **执行层**：在 `TaskRunner.TryStart` 中，启动进程前遍历注入：
  ```csharp
  if (task.Action?.Env != null)
  {
      foreach (var kvp in task.Action.Env)
      {
          string val = TemplateExpander.Expand(kvp.Value, task, context);
          psi.EnvironmentVariables[kvp.Key] = val;
      }
  }
  ```
  消除用户必须手写外部批处理只为了 `set PROXY=...` 的痛点。

---

### 9. 四2：托盘菜单增加“运行中任务”与快速控制

#### 9.1 托盘层级改造
改造 `TaskSchedulerModule.GetTrayMenuItems()`，在“自动化任务”子菜单下引入**实时运行管理分组**：

```
[托盘图标]
  └─ 自动化任务 (运行中)
       ├─ [✓] 启用任务调度
       ├─ ──────────────
       ├─ 运行中任务 (2)                    <-- 新增动态分组
       │    ├─ frpc-daemon (pid: 14280, 2h)
       │    │    ├─ ⏹ 停止该任务
       │    │    ├─ 🔄 重启该任务
       │    │    └─ 📄 查看日志
       │    └─ heavy-sync (pid: 2310, 45s)
       │         ├─ ⏹ 停止该任务
       │         └─ 📄 查看日志
       ├─ 手动运行 >                         <-- 点击已在运行的任务会气泡提示"已在运行中"
       ├─ 最近运行 >
       ├─ ──────────────
       ├─ 任务编辑器...
       ├─ 打开任务日志目录...
       └─ 重载任务
```

#### 9.2 友好反馈
* 当用户在托盘“手动运行”中点击了一个已经在运行且不允许并发的任务时：
  不再静默无响应，而是通过托盘通知气泡提示：`任务 [{name}] 当前已在运行中 (PID: {pid})`；
* 用户若想中止失控任务，无需打开大窗口编辑器，直接在托盘子菜单内 2 次点击即可精确停止或重启。

---

## 四、实施分步计划

| 阶段 | 目标范围 | 涉及核心文件 |
|---|---|---|
| **Phase 1** | **状态解耦与进程管控核心（核心痛点）**<br>彻底分离 Enabled 与 Runtime 状态；实现启动/停止/重启；消除假绿灯与杀不死问题 | `ITaskSchedulerService.cs`<br>`TaskSchedulerService.cs`<br>`TaskDefinition.cs`<br>`TaskEditorWindow.xaml/.cs` |
| **Phase 2** | **编辑器交互保护（一1）**<br>实现 Dirty State 检测与未保存切换拦截弹窗 | `TaskEditorWindow.xaml.cs` |
| **Phase 3** | **触发器能力补齐（二1, 二2, 二3）**<br>`{{file}}` 上下文变量与文件锁排队；Daily 开机漏跑补偿；Interval 启动即跑与休眠唤醒 | `FileWatcherTrigger.cs`<br>`DailyTrigger.cs`<br>`IntervalTrigger.cs`<br>`TemplateExpander.cs` |
| **Phase 4** | **执行引擎加固（三1, 三2, 三3）**<br>Detach PID 状态持久化防重；自适应 OEM/UTF-8 编码；环境变量注入 | `TaskSingleInstanceMutex.cs`<br>`TaskRunner.cs`<br>`TaskConfigService.cs` |
| **Phase 5** | **托盘交互与体验闭环（四2）**<br>托盘运行中任务分组、逐实例停止/重启菜单与反馈气泡 | `TaskSchedulerModule.cs` |

---

## 五、验收标准与验证方案

1. **状态更新验证**：
   - 任务在空闲时，列表显示空闲/未运行指示；
   - 任务启动时，毫秒级点亮绿色运行指示灯，并显示真实 PID；
   - 在任务管理器强制结束该进程，编辑器在 1 秒内心跳刷新熄灭运行灯；
   - 禁用任务显示清晰的置灰样式，与运行态互不干扰。
2. **生命周期控制验证**：
   - 编辑器内点击【启动】可立即启动已启用的任务；
   - 运行中点击【停止】，进程树被立即销毁，同时守护重启被彻底打断，不会在 5 秒后自启；
   - 运行中点击【重启】，观察旧进程退出后新进程正常生成且 PID 变更。
3. **未保存保护验证**：
   - 修改表单后直接点击左侧另一任务，正确弹出三键确认框；点击“取消”表单内容毫秒不差保留。
4. **触发器与环境验证**：
   - 在监视目录新建文件，检查日志中 `{{file}}` 正确展开为目标路径；
   - 批处理输出中文，检查 `task-xxx.log` 中无乱码；
   - 配置自定义环境变量并在脚本中打印，验证变量成功注入。
