# CarroDesk 代码全面审查报告（bd）

> **审查日期**：2026-09-18
> **审查范围**：`src/` 全部源码（Core / Host / Services / Modules / Views / Models）+ `tests/CarroDesk.Tests` 测试工程
> **对照基线**：`docs/HOST-PLUGIN-DECOUPLING-SPEC-v3.md`（v3.0 Draft）、`docs/MODULAR-HOST-DESIGN.md`、`docs/CHANGES-20260916~18.md`
> **审查维度**：架构设计、业务与核心逻辑、代码质量、测试覆盖

---

## 0. 结论摘要

CarroDesk 是一个**进程内编译期内置模块化单体（In-Process Modular Monolith）**WPF 托盘工具箱，当前含 6 个业务模块（ScreenLock / TaskScheduler / AudioSwitch / AppAutoMute / MonitorProfile / Awake）。整体架构方向正确、工程质量处于**中上水平**：模块契约（IModule/ModuleBase/IModuleContext）、Host 微内核（ServiceContainer/ModuleManager/DynamicTrayController/SafeInvoker）、IExitGuard 退出协商、纯物理闲时服务（SystemIdleService）等 v3 规范的关键骨架**均已实际落地并可运行**，且有 62 个单元测试（含在测试中揪出两个隐蔽 bug 的先例）。

但架构清理**只完成了 S1–S3 的大半，S4/S5 仍有实质债务**：

- 🔴 **M2 未完成**：`TaskSchedulerService` 仍直访 `CarroDesk.App.Config` 与 `App.ShowBalloonPublic`（`src/Services/Tasks/TaskSchedulerService.cs:66,94,245`），是唯一残留在业务服务里的静态门面依赖；
- 🔴 **M5 部分完成**：`ConfigManager.GetModuleConfig/SaveModuleConfig`（`ConfigManager.cs:40-178`）仍以 if-else 硬编码全部 6 个模块类型，Host 出现业务模块类名，与"宿主零感知"铁律直接冲突；
- 🔴 **业务缺陷**：`ProfileScheduleEngine.GetActiveSettingForProfile` 跨午夜回退取错时间段（`ProfileScheduleEngine.cs:139`），清晨会用错亮度档位；
- 🔴 **致命异常一律被 Handled**：`OnDispatcherException` 无条件 `e.Handled = true`（`App.xaml.cs:407-411`），违反 v3 §6.4"致命异常落盘后退出"的闭环要求；
- 🟠 全仓 **186 处空 `catch { }`** 静默吞异常（`App.xaml.cs` 17 处、`TaskSchedulerService.cs`/`TaskRunner.cs` 各 16 处、`AudioService.cs` 10 处）；
- 🟠 View 层大面积直连 `App.Config / App.Services`（FloatingPanelWindow 40+ 处、TrayContextMenu、TaskEditorWindow、ConfigEditorWindow），依赖注入只贯彻到了模块层，未贯彻到 UI 层；
- 🟠 `IdleDetector.cs`（154 行）已是**死代码**（全仓无引用），应删除；
- 🟠 `ConfigService.Save()` 直接 `File.WriteAllText`（`ConfigService.cs:204`），无原子写/损坏备份，与 v3 §3.6 不符。

**判断**：架构无须推翻重来，方向正确、收益递减点已到。剩余工作集中在**收口静态依赖、删除死代码、补齐配置契约与若干正确性 bug**，按本报告 §8 的优先级执行可在 1–2 个迭代内清零。

---

## 1. 架构层评审

### 1.1 已达标（对照 v3 规范逐项核验）

| 规范条款 | 状态 | 证据 |
|---|---|---|
| §3.1 `IModule/ModuleBase/ModuleStatus` 生命周期与 Faulted 隔离 | ✅ 已落地 | `src/Core/ModuleBase.cs`：Initialize 失败置 Faulted 并重抛、Start 幂等、Faulted 禁止 Start |
| §3.2 `TrayMenuItem` 原地扩展（Order/ToolTip） | ✅ | `Core.Models.TrayMenuItem` + 契约测试 `CarroDeskContractTests` |
| §3.3 `IModuleContext` 薄上下文 | ✅ | `ModuleManager.ModuleContext`（私有实现，只含 ModuleId/Dispatcher/GetService/RequestTrayRefresh/ShowNotification）|
| §3.4 `IIdleService` 纯物理闲时：唯一线程池 Timer 扇出 + 懒启动 | ✅ | `Host/Services/SystemIdleService.cs`：显式 add/remove、无消费者不占 Timer、SystemBusy 2s 缓存轮询 |
| §3.4 业务状态机移入模块（ScreenLock 复刻 WarnBefore/暂停/白名单） | ✅ | `Modules/ScreenLock/ScreenLockModule.cs:95-170` |
| §3.4 IdleTrigger 独立判定 | ✅ | `IdleTrigger.cs` 按 afterMinutes 独立订阅，不自建 Detector |
| §3.5 `IExitGuard` 多播 + 3s 超时 + Host 持有挑战 UI + 缺 PIN 兜底 | ✅ | `App.xaml.cs:368-395`（遍历 Modules 找 IExitGuard，SafeInvoker.RunTimeout 3s）+ `HostPinService` 下沉 |
| §3.8 `ServiceContainer` 多注册 + GetServices | ✅ | `ServiceContainer.cs`（含 `ServiceContainer_Supports_MultiRegistration` 测试）|
| §4.1–4.3 托盘双锚点 + 登记表精确移除 + 150ms 防抖 + IsOpen 延迟 + 空态折叠 | ✅ | `DynamicTrayController.cs` 全文 |
| §4.5 节点摘除清理（ClearAllBindings + Click 摘除） | ✅ | `DynamicTrayController.DetachVisual` |
| M11 命名空间 `ScreenLock.*` → `CarroDesk.*` | ✅ 已验证 | 全仓 grep `namespace ScreenLock` 零命中 |
| 模块二级收敛（单根节点 + 子菜单） | ✅ | 契约测试断言 ScreenLock 只输出 1 个根项 |
| PIN/PinGuard（PBKDF2、升级、防爆破退避） | ✅ | `PinService.cs` + `PinServiceTests`（62 个测试的一部分）|

### 1.2 遗留架构债（按严重度排序）

**🔴 A1. `TaskSchedulerService` 直访 App 静态门面（v3 M2 未完成）**

```csharp
// src/Services/Tasks/TaskSchedulerService.cs:60-71
// use reflection to avoid circular dep if Config not yet init, but we can try direct
// App.Config may be null at this point
var cfg = CarroDesk.App.Config;
```

`CarroDesk.App.Config` 本身是强转出来的兼容门面（`App.xaml.cs:37`：`(Services?.GetService<IConfigManager>() as ConfigManager)?.Underlying`），业务服务再叠一层 `App.Config` 访问，等于**服务层→App 静态→强转→Underlying 具体类型**的四层穿透。同样 `RecordFailureNotify` 里 `CarroDesk.App.ShowBalloonPublic`（`TaskSchedulerService.cs:245`）绕过了 `INotificationService`。

- 影响：违背 §2.1 铁律 1/3；`TaskSchedulerService` 无法脱离 App 单独测试（测试只能用反射/空上下文绕行）；续行任何重构都会继续在这类穿透上叠加。
- 改进：构造函数注入 `IConfigManager` + `INotificationService`，`TaskSchedulerModule` 在装配时将服务传入；删除反射注释死路。

**🔴 A2. `ConfigManager` 硬编码模块路由（v3 §3.6 只做了一半）**

```csharp
// ConfigManager.cs:40-110  GetModuleConfig 逐模块 if-else
if (string.Equals(moduleId, "ScreenLock", ...)) { ... }
else if (string.Equals(moduleId, "TaskScheduler", ...)) { ... }
...
// :118-178 SaveModuleConfig 同样逐模块 if-else + JsonConvert.SerializeObject 各写各的
```

- 问题 1：**Host 出现业务模块类名**（`Modules.ScreenLock.Models.ScreenLockConfig` 等），违反铁律 3，也违反 §2.2"Order 不决定服务可见性"的抽象目标。
- 问题 2：**新增一个模块必须改 Host 代码**，违背"模块自治"；未来 v3 承诺的"模块自带配置契约注册"没有出现。
- 问题 3：ScreenLock / TaskScheduler 的配置仍是**旧扁平字段映射**（IdleMinutes/PinHash/... 直接读 `AppSettings.Current` 的根字段），只有 AudioSwitch/AppAutoMute/MonitorProfile/Awake 走了 JSON 分段——**两套配置机制并存**，迁移完成度约 60%。
- 改进（最优解）：把配置契约下沉为**模块侧注册**——`IModule` 增加可选 `void RegisterConfig(IConfigRegistry)`，Host 提供 `IConfigRegistry.Register<TConfig>(moduleId, get, save)`；ConfigManager 内部用 `Dictionary<string, ConfigAdapter>` 转发。做完后 `ConfigManager` 删除全部 if-else。

**🔴 A3. UI 层（Views）直连 App 静态门面**

| 文件 | 直连数量 | 典型 |
|---|---|---|
| `FloatingPanelWindow.xaml.cs` | 40+ 处 | `App.Config?.Current`、`App.Modules.Modules`、`App.ShowBalloonPublic`、`App.Services` |
| `TrayContextMenu.xaml.cs` | ~10 处 | `App.Config`、`App.Services` |
| `TaskEditorWindow.xaml.cs` | 4 处 | `CarroDesk.App.Services` |
| `ConfigEditorWindow.xaml.cs` | ~8 处 | `App.Config.Current` 直接改 + Save |

- 影响：所有窗口**不可独立测试、不可复用于其它宿主**；`App.Config` 是 `as ConfigManager` 强转（`App.xaml.cs:37`），一旦 ConfigManager 内部重构（A2），UI 层全部编译失败——这正是 A2 改造的最大阻力点。
- 改进：为窗口引入构造注入（`IPinService`/`IConfigService`/`ITaskSchedulerService` 最小面）；量最大的 `App.Config.Current` 改为受注入的 `AppSettings` 视图。这是 S4 前必须清掉的障碍，否则 §3.6 迁移一动 UI 全崩。

**🟠 A4. App.xaml.cs 手工 new 模块 + 属性注入回调**

`App.xaml.cs:141-173` 逐模块 `new ScreenLockModule(rawConfig) { BalloonNotifier = ... }`，其中：

- `ScreenLockModule` 构造收**具体 `ConfigService`**（而非 `IConfigManager`），`LockController` 又直接 new `PinService`/`PinGuard`/`KeyboardBlocker`——模块内仍持具体服务，`ScreenLockModule` 不能脱离 `ConfigService` 实例化（`AppAutoMuteSettingsWindow.xaml.cs:69` 还直接 `App.Services.GetService<AudioService>()` 拿具体类）。
- 各模块仍保留 `public static XxxModule Instance` 静态单例（`AudioSwitchModule.cs:13`、`AppAutoMuteModule.cs:15`、`ScreenLockModule.cs:14`、`AwakeModule.cs:16`、`MonitorProfileModule.cs` 等同），App 门面再叠一层（`App.xaml.cs:30-34`）——**静态单例 ×2 层**，v2 的批判点只消了一半。
- 改进：`Instance` 与 `App.XxxMod` 二选一删除；模块构造只收接口。ScreenLock 的 `Controller/PinService` 注入接口（`IPinService` 已有，`PinService` 具体类被 `LockController` 用，`VerifyPinWindow` 已收 `IPinService`，可顺通）。

**🟠 A5. ModuleManager 启动前过滤不一致（潜在隐患）**

- `InitializeAll` 用 `m.DefaultEnabled` 过滤（`:53`），`StartAll` 用 `Status == Faulted` 过滤（`:73`）。若某模块 `DefaultEnabled == false`：Initialize 跳过 → Status 停留在 `Created` → **Start 仍会执行**（Status 非 Faulted）→ 未初始化模块被启动。当前 6 个模块全部 `DefaultEnabled = true` 故未触发，但这是契约级洞。
- 改进：`StartAll` 加 `Status != ModuleStatus.Initialized` 跳过（Created 未初始化者不启），或 `Start` 内 `if (Status < Initialized) return`。

**🟠 A6. 死代码 `IdleDetector`**

`src/Services/IdleDetector.cs`（154 行，含 SHQueryUserNotificationState + 状态机）全仓无任何引用——SystemIdleService 出现后已成为死代码，且它的状态机与模块内复刻逻辑**语义重复**（如 `hasInput = raw < _lastRaw || raw < IntervalMs*2` 与 `ScreenLockModule.OnIdleTick` 几乎逐行一致），留着会误导后续维护者"该用哪个"。直接删除。

**🟡 A7. 模块 Order 未全覆盖**

只有 Awake（15）/ MonitorProfile（30）显式 `Order`，其余 4 个模块默认 `Order=0`，托盘排序实际按注册顺序（App.xaml.cs 注册序：ScreenLock→TaskScheduler→AudioSwitch→AppAutoMute→MonitorProfile→Awake），一旦注册顺序变化排序就变。建议 6 个模块全部显式声明 Order，并加契约测试锁死。

---

## 2. 业务与核心逻辑评审

### 2.1 🔴 亮度调度跨午夜回退取错时间段（`ProfileScheduleEngine.GetActiveSettingForProfile`）

```csharp
// ProfileScheduleEngine.cs:135-139
var sorted = settings.OrderByDescending(s => s.ToTimeSpan()).ToList();
TimeSpan now = DateTime.Now.TimeOfDay;
// 寻找不大于当前时间的最近时间点；若早于第一个时间点，回退到前一天夜间（即列表最后一段）
return sorted.FirstOrDefault(s => now >= s.ToTimeSpan()) ?? sorted.Last();
```

- 问题：`sorted` 是**降序**。假设 Daily 档位 `[07:00→亮度65, 18:00→50, 22:30→35]`，降序后为 `[22:30, 18:00, 07:00]`。凌晨 **06:00** 时 `now >= s` 全部不成立 → 返回 `sorted.Last()` = **07:00 档（亮度 65）**。
- 语义错误：清晨 6 点其实处于"昨晚 22:30 段"的延续，应回退到 **22:30 的夜间档（亮度 35）**；注释说"回退到前一天夜间（即列表最后一段）"，但"列表最后一段"按降序写代码实际取到了**升序第一段**（07:00），注释与实现矛盾，实现按注释意图是错的。
- 修复：`return sorted.FirstOrDefault(s => now >= s.ToTimeSpan()) ?? sorted.First();`——降序首元素即最晚档位（22:30）。同时补一条 06:00 场景的单测（当前 `MonitorProfileTests.cs` 未覆盖跨午夜回退）。

### 2.2 🟠 亮度异步队列的"假成功"返回（`MonitorDdcService.SetBrightnessAndContrastAsync`）

```csharp
// MonitorDdcService.cs:127-132
if (_isApplying) { return Task.FromResult(0); }   // 第二次调用立即"成功"
_isApplying = true;
```

- 问题：忙时第二次调用直接返回**已完成的 Task**，未等待实际应用。`ScheduleEngine.ApplyValuesAsync` 的 `.ContinueWith(NotifyApplied)`（`ProfileScheduleEngine.cs:247-250`）会**立即**发"已应用"通知，而真实硬件可能还被第一次未完成的循环占着——UI 状态先于硬件事实。
- 修复：忙时不立即返回，而是让调用方拿到"pending 值的最终应用完成"——用单个 `TaskCompletionSource` 存 pending，循环每次消费完解析当前 TCS 后重建。或显式化语义：document 返回的是"已入队"而非常"已应用"，UI 通知改在完整消费后统一发。

### 2.3 🟠 任务系统：文本扫描 JSON + 并发标记清理

- `TaskSchedulerService.Reload`（`TaskSchedulerService.cs:137-144`）用 `txt.Contains("\"TasksEnabled\": false")` **字符串扫描**读取配置——JSON 应走 `JsonConvert`，字符串匹配在键顺序/空白/转义变化时失效（如 `TasksEnabled : false` 带空格即漏判）。
- `Apply()` 中 `_running.Clear()`（`TaskSchedulerService.cs:162`）在重载配置时清空运行标记：若某任务正被 `ExecuteAsync` 执行中，Reload 后其 `_running[name]` 标记丢失，同一任务可被再次并发触发（`AllowConcurrent=false` 失效窗口）。
- 修复：读配置走 `ConfigManager` 或 `JsonConvert`；`_running` 改用"以执行中任务计数"或 `TaskCompletionSource` 生命周期而非字典清空。

### 2.4 🟠 全局异常处理：致命的也 `Handled=true`（v3 §6.4 违反）

```csharp
// App.xaml.cs:407-411
private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
{
    LogError(e.Exception);
    e.Handled = true;   // 一律吃掉
}
```

- `OutOfMemoryException/StackOverflowException/AccessViolationException` 也会被标记 Handled 后继续运行——v3 §6.4 明确要求"可恢复记日志 + 标记 Faulted；OOM/StackOverflow/AccessViolation 落盘后退出"。同时 UI 层异常被吃后用户**无感消失**（无气泡/对话框），排查只能翻 log.txt。
- 修复：`if (e.Exception is OutOfMemoryException || e.Exception is StackOverflowException || e.Exception is AccessViolationException) { LogError; Environment.FailFast(ex.Message); }`，可恢复异常记日志 + `ShowBalloon("操作失败，详情见日志")`。

### 2.5 🟠 全仓空 `catch { }` 泛滥

统计：**186 处**匹配空 catch（含单行 `catch { }` 与多行体全空的）。分布重灾区：`App.xaml.cs`（17 处）、`TaskSchedulerService.cs`（16 处）、`TaskRunner.cs`（16 处）、`AudioService.cs`（10 处）、`TaskEditorWindow.xaml.cs`（9 处）、`ScriptResolver.cs`（7 处），其余分布在各模块与 View。

- 空 catch 使：① 故障完全无痕迹（v3 §6 "静默吞异常禁止"）；② 排查靠猜；③ 掩盖 COM/DDC 等不可靠边界外的可恢复错误。
- 改进：批量将"可控边界"（如 UI 刷新、资源释放）空 catch 升级为 `SafeInvoker` 或 `logger?.LogWarning`；Hotkey/COM 边界至少记一次日志（防刷屏可节流）。

### 2.6 🟠 AppAutoMute 白名单模式的静音残留风险

`AppAutoMuteModule.cs:253-265` `UnmuteAllTargets`：

```csharp
if (Config.Mode == "Whitelist")
{
    var activeProcs = _audioService.GetActiveAudioProcesses();
    _audioService.UnmuteProcesses(activeProcs);   // 只解除当前活跃进程
}
```

- 白名单模式下曾被静音、但现在**已无活跃音频流**的进程不在 `activeProcs` 里——其进程级静音标志（通过 `IAudioService.SetProcessMute` 对 session 音量设置）不会恢复。若该进程下次播放时静音仍残留在 session 层，表现为"某应用莫名变哑直至重启"。黑名单模式同样只解除 `Config.TargetApps`（`_audioService.UnmuteProcesses(Config.TargetApps)`），白名单曾静音的非 target 进程一样漏。
- 改进：`UnmuteProcesses` 的语义从"对给定集合解除"扩展为"解除全部已静音进程"（AudioService 侧维护已静音名单），模块只调用"全量恢复"。

### 2.7 🟡 其它已确认问题

| 位置 | 问题 |
|---|---|
| `TaskSchedulerService.cs:362-382` | 每次 Resume 新建一个 DispatcherTimer，仅 Stop 不 Dispose/不摘 Tick，反复休眠唤醒积累悬挂计时器 |
| `App.xaml.cs:124` | `App` 以 `HotkeyService.Instance` 注册进容器，但 HotkeyTrigger 仍直接 `HotkeyService.Instance`（`Triggers/HotkeyTrigger.cs:24`）——容器注册形同虚设，v3 M6 只完成一半 |
| `AwakeModule.SaveConfig`（`AwakeModule.cs:84-97`）| 把运行时 `Mode/KeepDisplayOn` 写回配置，但 `Initialize` 不回读恢复（`AwakeService` 默认 Passive 启动）——重启后用户上次的"无限保持"状态丢失，属**设计取舍**但未写注释，建议明确并补注释或恢复 |
| `ModuleManager.InitializeAll` | `DefaultEnabled=false` 的模块不 Initialize，但 `GetAllTrayMenuItems` 仍会调其 `GetTrayMenuItems()`——未初始化模块菜单项可能被拉（当前无此模块，契约洞）|
| `LockController.Lock` (`:80`) | `if (_locked || !_pinService.IsConfigured) return;`——**未设置 PIN 时 LockSafe 直接 no-op**，托盘"立即锁定"在无 PIN 机器上无声无效；无 PIN 时至少应弹提示而非静默 |
| `FileWatcherTrigger` | 使用 `FileSystemWatcher` 未设置 `InternalBufferSize`（默认 8KB）、未订阅 `Error` 事件——大量文件事件丢事件时**无感知** |

---

## 3. 代码质量与工程实践

### 3.1 做得好的

- **契约先行**：`ModuleStatus/TrayMenuItem/IModule` 有独立契约测试（`CarroDeskContractTests`），防回归有效；
- **测试揪出真 bug 的先例**：`TemplateExpander` 日期误格式化、`PinGuard` 锁定输出时延 2 个隐蔽缺陷是被测试发现的（见 CHANGES-20260918），说明测试质量真实；
- **删除式重构意识**：ProcessHelper 抽公共层（归一化/去重/弹性比对），3 处重复逻辑收敛 1 处，且配 `ProcessHelperTests`；
- **安全敏感点处理到位**：PIN 哈希用 PBKDF2 + 旧哈希透明升级 + 防时序比对 + PinGuard 线性退避——`PinServiceTests` 覆盖完整；
- **线程纪律**：模块回调统一 `Dispatcher` 封送（AudioSwitch/AppAutoMute/Awake/ScreenLock 的 `SetXxxSelf` 均先 `CheckAccess`），后台线程更新节点属性由 Host 记错并忽略（`DynamicTrayController.cs:309-316`）——契约落实情况好。
- **版本/文档纪律**：CHANGES 日志坚持每天记录，测试数递增可追溯（28→34→62）。

### 3.2 需要改进的

| 类别 | 现状 | 改进 |
|---|---|---|
| 语言版本 | `LangVersion=7.3`（net48 约束）| 保持；但可明确 `Directory.Build.props` 统一配置 |
| Newtonsoft 迁移 | 已有 `NEWTONSOFT-JSON-MIGRATION-PLAN.md` + `JsonMigrationTests` | 继续按计划收口 `ConfigService` 内残留手写序列化（`AudioSwitchJson` 等已移 Newtonsoft，`Save` 仍整文件写）|
| 原子写 | `ConfigService.Save` = `File.WriteAllText` 直写（`:204`）| temp+replace + 损坏时备份 `.corrupt-时间戳`（v3 §3.6 明确要求）|
| 配置并发 | `SaveModuleConfig` 高频调用（ToggleEnabled 等）每次全量落盘无合并 | 300ms 合并写 + 退出前 Flush（v3 §3.6）|
| 日志 | `DefaultLoggerService` + `TaskLogger` 双轨 | 统一日志入口，避免"一半走 logger 一半落文件" |
| 字符串魔法 | `"Blacklist"/"Whitelist"`（AppAutoMuteConfig）、`"Daily"`（MonitorProfileConfig）| 枚举化（可 JSON 存字符串但代码内转枚举）|
| 资源释放 | `TaskRunner` 的 `CancellationTokenSource` 未 Dispose（`:174`）；`DynamicTrayController` 定时器只 Stop | 统一 `using`/`Dispose` |

---

## 4. 测试覆盖评估

**规模**：10 个测试文件，**62 项单测**（CHANGES-20260918 记录，100% 通过，0 警告 0 错误），net48 + MSTest 3.4.3，`dotnet test` 可跑。

**覆盖矩阵**：

| 领域 | 覆盖 | 缺口 |
|---|---|---|
| 模块契约/生命周期 | ✅ ModuleManagerTests（重复 ID、启动序倒序遍历、单模块 Faulted 隔离）、契约测试 | — |
| PIN/安全防爆破 | ✅ PinServiceTests（PBKDF2、升级、时序、PinGuard 退避）| LockController 解锁/锁窗集成流 |
| 模板/条件/Cron | ✅ TemplateExpander、TaskConditionEvaluator、CronHelper | — |
| 配置迁移 | ✅ JsonMigrationTests、MonitorProfileTests（部分）| ConfigManager 双轨制（扁平 vs JSON 段）互迁 |
| 进程工具 | ✅ ProcessHelperTests | — |
| Awake | ✅ AwakeModuleTests（契约/菜单/模式切换/持久化）| AwakeService 电池暂停/进程触发时序 |
| **TaskScheduler 调度执行** | ❌ 仅条件评估 | **`TaskRunner` 进程执行/超时/重试、触发→执行→去重全链路、任务标记并发** |
| **MonitorProfile 硬件调度** | ⚠️ 部分（配置/契约）| **跨午夜回退（本次发现 bug）、多显示、异步队列假成功** |
| **AudioSwitch/AppAutoMute 核心逻辑** | ⚠️ 仅 GetDeviceShortName | 切换决策（pattern/round-robin）、黑白名单静音语义、全量恢复 |
| 托盘引擎 | ❌ | DynamicTrayController 双锚点插入顺序/空态折叠/防抖（无 UI，可注入 Items 桩测试）|

**结论**：62 项测试覆盖了"纯逻辑原子能力"（模板、Cron、PIN、条件、进程名、配置迁移），质量好；但**跨模块集成逻辑和硬件相关调度几乎没有行为测试**——本次 §2.1 的跨午夜 bug、§2.2 的假成功、§2.5 的静音残留恰好都落在无测试覆盖区。建议下一批测试优先：`ScheduleEngine`（纯时间逻辑，最好测）、`TaskRunner`（进程桩）、`AudioService` 静音状态清单（接口桩）、`DynamicTrayController`（Items 桩）。

---

## 5. 与 v3 规范的差距清单（落点汇总）

| v3 编号 | 状态 | 差距 |
|---|---|---|
| M1 Idle 解耦 | ✅ 完成 | IdleTrigger 独立判定 |
| M2 TaskSchedulerService 经 IConfigManager | ❌ **未完成** | `TaskSchedulerService.cs:66,94,245` |
| M3 托盘业务项归模块 | ✅ 完成 | 双锚点注入已落地 |
| M4 App 静态门面删除 | ⚠️ 部分 | `Services/Modules` 保留可行，但 `ScreenLockMod/TaskSchedulerMod/...` 5 个别名 + `Config` 强转仍在，UI 大量消费 |
| M5 ConfigManager 分区+落盘+原子写 | ⚠️ 部分 | 分区 4/6，原子写 ❌，合并写 ❌ |
| M6 HotkeyService.Instance 消灭 | ⚠️ 部分 | 容器已注册，但 `HotkeyTrigger.cs:24` 仍直连静态 |
| M7 I18nService.Instance 动态化 | ✅ 基本 | `OnLanguageChanged` 已接；XAML `{loc:Loc}` 仍走静态索引器但受控 |
| M8 LockWindow 活引用注入 | ✅ 完成 | `ILockService/ILockAppearance` |
| M9 ConfigEditor 不越权 | ✅ 完成 | 仅 Save + 重载通知 |
| M10 IdleDetector 纯化 | ⚠️ 部分 | 模块复刻完成，但**旧 IdleDetector 未删除**（死代码）|
| M12 PIN 下沉 Host | ✅ 完成 | HostPinService + VerifyPinWindow 收 IPinService |
| M13 ShowBalloon 语义下沉 | ⚠️ 部分 | `INotificationService` 已注册但 `TaskSchedulerService.cs:245` 仍走 `App.ShowBalloonPublic` |

---

## 6. 改进方案（按优先级）

### P0 — 正确性（本周内）

1. **修复跨午夜回退 bug**：`ProfileScheduleEngine.cs:139` `sorted.Last()` → `sorted.First()`，补清晨场景单测；
2. **修复致命异常处理**：`App.xaml.cs:407` 增加 OOM/StackOverflow/AccessViolation 分支（`Environment.FailFast`），可恢复异常记日志 + 气泡提示；
3. **完成 M2**：`TaskSchedulerService` 构造注入 `IConfigManager`/`INotificationService`，删除 `App.Config`/`App.ShowBalloonPublic` 直访（`TaskSchedulerModule` 装配时注入）；
4. **修复 Reload 文本扫描**：`TaskSchedulerService.cs:137-144` 改 `JsonConvert` 或统一经 `ConfigManager`；`_running.Clear()`（`TaskSchedulerService.cs:162`）改按执行生命周期管理。

### P1 — 架构收口（1 个迭代）

5. **ConfigManager 去硬编码（A2）**：引入 `IConfigRegistry` + 模块注册式配置适配，删掉 `ConfigManager.cs:40-178` 全部 if-else；**在此之前**先给 Views 做注入改造（A3）解除 `App.Config` 强转依赖；
6. **删除死代码**：`IdleDetector.cs`；`App.XxxMod` 5 个别名与各模块 `static Instance` 二选一；
7. **补齐 M5 原子写 + 合并写**：`ConfigService.Save` temp+replace + `.corrupt` 备份；`SaveModuleConfig` 300ms 合并 + `OnAppExit` Flush；
8. **ModuleManager 契约加固（A5）**：`StartAll` 跳过非 `Initialized` 模块；`GetAllTrayMenuItems` 跳过未初始化模块；
9. **Hotkey 去重（M6 收尾）**：`HotkeyTrigger` 改容器注入的 `IHotkeyService`；
10. **空 catch 分级清理**：至少把 `App.xaml.cs`/`AudioService.cs` 的静默 catch 改为记日志。

### P2 — 质量（后续）

11. **补集成测试**：`ScheduleEngineTests`（跨午夜/次日/多档）、`TaskRunnerTests`（桩进程+超时）、`AudioServiceTests`（静音状态清单接口桩）、`DynamicTrayControllerTests`（注入 `IList` 桩验证插入顺序/空态折叠）；
12. **AppAutoMute 全量恢复**（§2.6）：AudioService 侧维护"已静音进程集合"，模块只调"全量解除"；
13. **枚举化字符串魔法**：`AppAutoMuteConfig.Mode`、`MonitorProfileConfig.ActiveProfile` 转枚举（JSON 层兼容）；
14. **FileWatcherTrigger**：设 `InternalBufferSize`（64KB）+ 订阅 `Error` 事件告警；
15. **TaskRunner**：`CancellationTokenSource` 用 `using`，两个分支重复的 `WaitForExit` 归一。

---

## 7. 更优方案探讨（方向性）

### 7.1 是否需要 MEDI（Microsoft.Extensions.DependencyInjection）？

**否**。v3 §3.8 已算过账：服务约 10 个、无 Scope/泛型校验需求，手写容器 ~60 行已补齐多注册/GetServices/Dispose，MEDI 会引入 200–300KB 体积（当前 Release 约 812KB）+ 绑定重定向维护链。**维持手写容器**，切换条件（服务超 20 个 / 出现 Scope 需求）出现再评估。

### 7.2 模块配置契约：从"Host 硬编码"到"模块自述"

当前最优解是让**模块声明自己的配置读写**（§6 P1-5 的 `IConfigRegistry`），而不是继续在 ConfigManager 里堆 if-else。这是 15–20 行的契约 + 每模块几行注册，收益是：新增模块零改 Host、配置持久化策略（扁平 vs JSON 段）由模块自己决定、ConfigManager 恢复纯基础设施身份。**这也是 v3 §3.6"分区 + 原子写"的自然延伸**，不建议等"配置多到爆"再重构。

### 7.3 UI 依赖收敛：注入 vs 继续用门面

Views 40+ 处 `App.Config` 不可能一次清完，建议**两步走**：

1. 先把 `App.Config` 从 `as ConfigManager` 强转改为注册 `IConfigService`（只读视图 + Save）到容器，`App.Config` 属性保留一天作为兼容过渡但内部不再强转——**消除 A3 改造的最大断裂点**；
2. 逐窗口改构造注入（FloatingPanelWindow 体量最大，放最后），`App.Internal static` 随迁移删除。

### 7.4 进程外插件 / 热插拔？

**明确不做**（与 v3 §2.3 结论一致）：net48 无 ALC、DLL 锁定 + 不可卸载 + 无隔离，收益远小于成本。若未来真要插件市场，正确路线是**进程外 mini-host 协议**（IPC），而非在进程内做 Assembly.LoadFrom。

### 7.5 值得考虑的小增量

- **配置损坏自愈**：原子写落地的同时给 `LoadOrCreate` 加"解析失败 → 备份 `.corrupt-{ts}` + 重建默认"（v3 §3.6 已要求，当前 `ConfigService.LoadOrCreate` 未见损坏分支）；
- **`IConfigManager` 的 `ConfigReloaded` 事件已存在**，可顺带把"Reload 后自动通知全部窗口刷新"做成 ViewModel 订阅，减少 `ReloadConfig` 里手写刷新链（App.xaml.cs:230-247 那串 try/catch）；
- **统一 `ILoggerService`**：`TaskLogger` 与 `DefaultLoggerService` 双轨合并，日志路径/级别策略一处配置。

---

## 8. 行动清单（可执行验收）

| # | 动作 | 验收标准 | 优先级 |
|---|---|---|---|
| 1 | 修跨午夜回退 | 06:00 场景单测绿 + 夜间档位生效 | P0 |
| 2 | 致命异常 FailFast | OOM 注入测试或代码审查确认分支存在 | P0 |
| 3 | M2 注入化 | `TaskSchedulerService` 零 `App.` 引用、编译过 | P0 |
| 4 | Reload JSON 化 + 并发标记 | 无字符串 Contains、无 `_running.Clear` 窗口期 | P0 |
| 5 | ConfigRegistry | `ConfigManager` 无模块类名、新模块零改 Host | P1 |
| 6 | 删死代码 + 静态单例收敛 | `IdleDetector.cs` 删除；`App.XxxMod` 或 `Module.Instance` 只剩一来源 | P1 |
| 7 | 原子写 + 合并写 | Save 走 temp+replace；高频 Save 合并 + 退出 Flush | P1 |
| 8 | 空 catch 分级 | App/Audio 无静默 catch（全仓从 186 降至个位数）| P1 |
| 9 | 补 4 个集成测试文件 | `dotnet test` 新增 ≥20 项全绿 | P2 |
| 10 | 静音全量恢复 | 白名单残留场景可复现并修复 | P2 |

---

## 附录 A：本次审查发现的缺陷速查

| 严重度 | 位置 | 一句话 |
|---|---|---|
| 🔴 | `ProfileScheduleEngine.cs:139` | 跨午夜回退取到升序第一段而非最后一段（夜间档丢失）|
| 🔴 | `App.xaml.cs:407-411` | 致命异常一律 Handled=true 继续运行 |
| 🔴 | `TaskSchedulerService.cs:66,94,245` | 业务服务直访 App 静态门面（M2 未完成）|
| 🟠 | `TaskSchedulerService.cs:137-144` | 字符串 Contains 解析 JSON + `_running.Clear()` 并发窗口 |
| 🟠 | `MonitorDdcService.cs:127-132` | 忙时返回假成功 Task，UI 通知早于硬件 |
| 🟠 | `ConfigManager.cs:40-178` | Host 硬编码 6 模块类型，新模块必须改 Host |
| 🟠 | `AppAutoMuteModule.cs:253-265` | 全量恢复漏掉"已静音但无活跃流"的进程 |
| 🟠 | 全仓 186 处 | 空 catch 静默吞异常 |
| 🟡 | `HotkeyTrigger.cs:24` | 容器外静态单例直连（M6 收尾）|
| 🟡 | `FileWatcherTrigger.cs` | 无 InternalBufferSize/Error 订阅，丢事件无感知 |