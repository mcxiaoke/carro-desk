# CarroDesk 代码全面审查报告

> **审查日期**：2026-09-19
> **审查范围**：`src/` 全部 109 个 `.cs` + 13 个 `.xaml`（约 20,850 行）、`tests/` 15 个测试文件、构建配置、本地化资源、`docs/` 文档一致性
> **审查方式**：分区域全量精读 + 关键结论逐条打开源码实证（行号以本次审查工作区版本为准）
> **对照基线**：`docs/CODE-REVIEW-20260918-bd.md`、`docs/CODE-REVIEW-20260918-gm.md`、`docs/FIX-PLAN-20260918.md`、`docs/MODULAR-HOST-DESIGN.md`
> **测试实测**：`dotnet test tests/CarroDesk.Tests` → **100/100 通过，0 失败，0 跳过，耗时 14s**

---

## 0. 结论摘要

**总体判断：架构方向正确、无需推翻重来；但并发模型与失败处理这两个横切面存在系统性债务，其中 4 项已达数据破坏 / 进程锁死 / 内存越界级别，必须在下一个迭代优先清零。**

三点核心结论：

1. **0918 两轮审查的架构类问题基本已闭环，但正确性类问题仍在持续新增。** 上一轮的静态门面依赖、原子写、死代码 `IdleDetector` 等均已修复；而本次新发现的 4 项 P0 中，有 3 项属**新引入或长期潜伏未被发现**的缺陷（测试污染生产配置、跨线程解锁、`PowerModeChanged` 订阅回归），说明当前缺少能捕捉这类问题的自动化防线——这才是根因。

2. **全仓最突出的两个系统性问题是"并发约定缺失"与"静默失败文化"。**
   - 并发：`ConfigService` 全局可变状态无锁、`MonitorDdcService` 的 Sync/Async 双路径、`TaskSchedulerService.Apply` 非串行、`LockController` 跨线程操作 UI、多个 `SystemEvents.*` 回调入口未做线程判定——**代码中没有任何一处显式的线程模型说明**。
   - 静默失败：`catch { }`（无任何日志）达 **172 处**，`ConfigManager`、`ConfigService`、各模块 `Save/RequestRefreshTray/RegisterHotkey` 的关键路径异常被完全吞掉。用户视角表现为"设置没保存""热键没生效""任务没跑"，且**日志里查不到任何线索**。

3. **工程质量处于中上水平，且基建优于同规模 WPF 工具。** `IModule` 契约、`SafeInvoker`、三层全局异常网、`AtomicFile`、`DynamicTrayController` 双锚点投影、379×2 双语 key 完全对齐、测试可真跑且全绿——这些都是加分项。问题不在"没有做"，而在"**做了一半**"（如 `SafeInvoker` 只有 `ModuleManager` 在用、`AtomicFile` 剪贴板模块没用、`CheckAccess` 封送只有托盘节点更新做了）。

**问题分布**：P0 × 4、P1 × 12、P2 × 18、P3 × 12，共 46 项。

| 优先级 | 数量 | 性质 | 建议处理窗口 |
|---|---|---|---|
| **P0 阻断** | 4 | 数据破坏、用户锁死、内存越界、配置静默丢失 | 立即（本迭代） |
| **P1 重要** | 12 | 功能静默失效、安全、并发竞态、关键路径零测试 | 下一迭代 |
| **P2 改进** | 18 | 健壮性、可维护性、性能、一致性 | 按模块渐进 |
| **P3 建议** | 12 | 代码坏味道、文档、工程规范 | 顺手清理 |

---

## 1. P0 阻断级（须立即修复）

### P0-1 运行单元测试会覆写用户真实的 `%APPDATA%\CarroDesk\config.json`

**证据（已实测复现）**

```csharp
// tests/CarroDesk.Tests/AwakeModuleTests.cs:114
var configService = new ConfigService();
// tests/CarroDesk.Tests/ModuleManagerTests.cs:199
configService.LoadOrCreate();
// tests/CarroDesk.Tests/ClipboardHistoryModuleTests.cs:266-267、MonitorProfileTests.cs:156 同
```

`ConfigService.cs:15-42` 中 `AppDataDirPath = %APPDATA%\CarroDesk` 是**静态只读且不可注入**的，测试直接 `new ConfigService()` 后写入的就是生产配置文件。

本次审查实测：执行测试后 `%APPDATA%\CarroDesk\config.json` 修改时间为 **21:56（与测试同刻）**，内容与测试夹具逐字吻合：

```json
"Awake": { "Hotkey": "Ctrl+Shift+F9", "BatteryThreshold": 30,
           "AutoAwakeProcesses": ["blender","ffmpeg"], "AutoAwakeExitDelaySeconds": 90 },
"ScreenLock": { "IdleMinutes": 12 },
"TaskScheduler": { "GlobalEnabled": false },
"ClipboardHistory": { "Hotkey": "Ctrl+Shift+V", "MaxItems": 500, "RetentionDays": 60 }
```

**为什么是 P0**：这是**确定性、每次执行都发生**的数据破坏——开发者本机设置被篡改、CI 上相互污染、用户若自行构建运行测试则直接丢失配置。比"偶发崩溃"更严重，因为它在每次 `dotnet test` 时静默发生。

**修复**

1. `ConfigService` 增加可注入基目录（构造参数或 `CARRODESK_DATA_DIR` 环境变量优先级最高），`[AssemblyInitialize]` 中统一指向 `Path.GetTempPath()\CarroDesk.Tests\<guid>`。
2. 更彻底：测试全面改用 `IConfigManager` 的 Fake 实现——`ConfigAndTriggerTests` 里的 `FakeConfigManager` 已是正确范式，其余测试向它对齐。
3. 加一道防线：`ConfigService` 检测到进程名为测试宿主（`*.Tests` / `testhost`）时拒绝写入 AppData 并抛异常。

---

### P0-2 ScreenLock 在 SystemEvents 线程上解锁，锁屏窗口永久残留且无法关闭

**证据**

```csharp
// ScreenLockModule.cs:224-238   回调线程 = Microsoft.Win32.SystemEvents 专用线程，非 UI 线程
else if (e.Reason == SessionSwitchReason.SessionUnlock)
{
    _sessionLocked = false;
    if (Config != null && Config.UnlockOnResume && Controller.IsLocked)
        Controller.Unlock();          // ← 非 UI 线程直接调用
    ResetIdleMachine();
}
```

```csharp
// LockController.cs:127-138
public void Unlock()
{
    if (!_locked) return;
    _locked = false;
    foreach (var win in _lockWindows) win.CloseSafe();   // win 是 WPF Window
    _lockWindows.Clear();                                // ← 引用在此丢失
    _blocker.Remove();
}
```

```csharp
// LockWindow.xaml.cs:389-407   CloseSafe 内全部是 WPF 对象操作
public void CloseSafe()
{
    try {
        _isClosing = true;
        _keepAliveTimer.Stop();      // DispatcherTimer 跨线程 → InvalidOperationException
        _uiTimer.Stop();
        var fadeOut = new DoubleAnimation(RootBorder.Opacity, 0, ...);   // UI 元素跨线程访问
        RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
    catch {
        try { Close(); } catch { }   // Close() 同样跨线程失败，再被吞掉
    }
}
```

**问题本质**：`CloseSafe` 内所有操作跨线程抛异常 → 被 `catch` 吞掉 → 回退 `Close()` 同样失败再被吞掉 → **窗口从未关闭，但 `_lockWindows.Clear()` 已执行，引用彻底丢失**。此后 `_locked == false`，任何再次调用 `Unlock()` 都直接 `return`。结果是解锁后全屏置顶浮层**永久留在屏幕上，用户只能杀进程**。

对比 `LockController.cs:36` 的 `OnDisplaySettingsChanged` 已正确使用 `Application.Current.Dispatcher.BeginInvoke`，说明作者知道该模式，但漏掉了 `Unlock` 这条主路径。

**修复**

```csharp
// ScreenLockModule.OnSessionSwitch
else if (e.Reason == SessionSwitchReason.SessionUnlock)
{
    _sessionLocked = false;
    Context?.Dispatcher?.BeginInvoke(new Action(() => {
        if (Config != null && Config.UnlockOnResume && Controller.IsLocked) Controller.Unlock();
        ResetIdleMachine();
    }));
}
```

并在 `LockController.Unlock()` 入口加 `CheckAccess` 守卫，且**先关窗成功再 Clear**（或失败时保留引用以便重试）。

---

### P0-3 ConfigService 全局状态无锁并发写 + 异常全程静默 → 配置静默丢失

**证据**

```csharp
// ConfigService.cs:210   普通 Dictionary，全文件无任何 lock（已 grep 确认）
private readonly Dictionary<string, JToken> _moduleConfigs =
    new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

// ConfigService.cs:233   Save 直接遍历共享字典
foreach (var kvp in _moduleConfigs)
    if (kvp.Value != null) obj[kvp.Key] = kvp.Value;
AtomicFile.WriteAllText(FilePath, json, Encoding.UTF8);

// ConfigManager.cs:84-94
try { var token = JToken.FromObject(config); _underlying.SetModuleToken(moduleId, token); _underlying.Save(); }
catch { }        // ← 全吞，无日志、无上报
```

**问题本质**：`ConfigService` 是事实上的全局可变单例，写入方遍布多线程——`App.ReloadConfig`、各模块 `OnConfigReloaded`、`AppAutoMuteModule.cs:122/349`、`TaskSchedulerService.cs:104`、`HostPinService.Verify()`（PIN 校验时写盘，可能来自锁屏窗口线程）。当 A 线程在 `Save()` 遍历 `_moduleConfigs`（:233）时 B 线程执行 `SetModuleToken`（:221），抛 `InvalidOperationException: Collection was modified`——**而该异常被 `ConfigManager.cs:94` 的 `catch { }` 完全吞掉**。

用户表现：改了设置、点了保存、界面正常关闭，**但磁盘上没有任何变化，且日志无记录**。`AtomicFile` 只保证单次写原子性，无法解决内存态竞争。

**修复**

```csharp
private readonly object _ioLock = new object();

public void Save()
{
    JObject obj;
    lock (_ioLock)                                     // 串行化
    {
        obj = JObject.FromObject(Current, _serializer);
        foreach (var kvp in _moduleConfigs.ToList())   // 快照后遍历
            if (kvp.Value != null) obj[kvp.Key] = kvp.Value;
    }
    AtomicFile.WriteAllText(FilePath, obj.ToString(Formatting.Indented), Encoding.UTF8);
}
```

`GetModuleToken`/`SetModuleToken`/`Reload` 一并纳入同一把锁。同时 `ConfigManager.cs:94` 的 `catch { }` 改为 `catch (Exception ex) { logger?.LogError(...); throw; }`——**宁可向上抛，也不允许无声失败**。

---

### P0-4 `PropVariant` 结构尺寸不足，`IPropertyStore.GetValue` 原生越界写

**证据**

```csharp
// src/Core/Audio/ComInterfaces.cs:54-62
[StructLayout(LayoutKind.Explicit)]
public struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(2)] public short wReserved1;
    [FieldOffset(4)] public short wReserved2;
    [FieldOffset(6)] public short wReserved3;
    [FieldOffset(8)] public IntPtr pwszVal;     // 结构总尺寸：x86=12B / x64=16B
}
// ComInterfaces.cs:147
int GetValue(ref PropertyKey key, out PropVariant pv);
```

**问题本质**：真实 `PROPVARIANT` 为 **x86=16 字节 / x64=24 字节**（尾部 union 需容纳 `DECIMAL`/双精度/指针）。此处声明仅 12/16 字节，`out PropVariant` 时封送器按 12/16 字节分配原生缓冲，而原生 `GetValue` 会写入 16/24 字节 → **越界写 4/8 字节**。触发路径为每次读取音频设备名（`AudioService.cs:274` → `GetValue`），属高频代码路径上的内存破坏风险，也是典型的"宿主机上偶发、难定位"的崩溃来源。

**修复**

```csharp
[StructLayout(LayoutKind.Explicit)]
public struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(8)] public IntPtr pwszVal;
    [FieldOffset(8)] public long llVal;       // 撑开 union
    [FieldOffset(16)] public long padding;    // x64 补齐到 24B
}
```

或直接 `[StructLayout(LayoutKind.Explicit, Size = 24)]`（x64）。同时确认仅 `vt == 31 (VT_LPWSTR)`、`vt == 8 (VT_BSTR)` 等分配型才会话才调 `PropVariantClear`，避免对栈上未分配 union 误调。

---

## 2. P1 重要级

### P1-1 TaskSchedulerService 缺失 `PowerModeChanged +=` 订阅，休眠补偿彻底失效（回归）

```csharp
// TaskSchedulerService.cs:115   Stop() 中只有解绑
try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
// TaskSchedulerService.cs:354   存在 handler 定义
private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) { ... CheckCatchUp() ... }
```

全仓 grep 确认**该文件内不存在 `+=`**（对比 `temp/backups/batch7/TaskSchedulerService.cs:50` 曾有正确实现，属重构过程中的一次回归）。结果：`OnPowerModeChanged` 永不触发，`DailyTrigger.CheckCatchUp()` / `CronTrigger.CheckCatchUp()` 永远不会被调用——**笔记本合盖跨过定时点后，任务永久漏执行，且无任何日志**。

**修复**：`Start()` 中补 `SystemEvents.PowerModeChanged += OnPowerModeChanged;`（需防重复注册），并为回调加 try/catch 隔离。**此类"订阅/解绑不对称"应当加静态检查**——建议在模块 `Dispose` 时打印未解绑事件清单。

### P1-2 TaskRunner 命令行拼接存在注入与引号逃逸；输出读取有竞态；存在孤儿进程

```csharp
// TaskRunner.cs:56-60
else if (ext == ".bat" || ext == ".cmd")
{
    realFile = "cmd.exe";
    realArgs = "/c \"" + file + "\" " + args;      // args 未做任何转义
}
```

三点问题：

1. **注入/逃逸**：`cmd.exe` 会对 `args` 二次解析，`&`、`|`、`>`、`%VAR%` 均被当作命令分隔符；`args` 只要以 `"` 结尾即可闭合 `file` 的引号逃逸。`.ps1`/`.js`/`.py` 分支同样是字符串拼接。`TaskRunner.cs:25-27` 还会先做 `ExpandEnvironmentVariables`，而 `TemplateExpander` 展开的 `{{env}}` 使外部环境变量可注入。
2. **输出读取竞态**：`stdout`/`stderr` 是 `StringBuilder`（非线程安全），异步回调在线程池写入（:142-156），主线程在 `await Task.Delay(100)` 后直接 `ToString()`（:202-207）——大输出量时数据错乱或丢行。且 `outText`/`errText` 赋值后**从未使用**（死变量）。
3. **孤儿进程**：`timeoutSec == 0` 时无守护，宿主退出后子进程树成为孤儿；超时分支 `KillTree` 后 `Task.Run(() => proc.WaitForExit())`（:176）仍挂在已 `Dispose` 的 Process 上（:225），产生未观测的后台异常。

**修复**：`.bat` 走逐参转义或改用 `-ArgumentList` 风格传递；提供"参数数组"配置项由代码统一引号化，禁止用户直接写整条命令行；输出缓冲改 `ConcurrentQueue<string>` 或回调内 `lock`，并改用无参 `WaitForExit()` 确保输出 drain 完成；超时用 `proc.WaitForExit(ms)` 返回值判定；**创建 Job Object 并设 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`** 根治孤儿进程。

### P1-3 ForegroundTracker 的 Win32 回调未做异常隔离

```csharp
// ForegroundTracker.cs:57-64
private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, ...)
{
    if (hWnd == IntPtr.Zero) return;
    string procName = GetProcessName(hWnd);
    CurrentWindowHandle = hWnd;
    CurrentProcessName = procName;
    ForegroundChanged?.Invoke(hWnd, procName);       // ← 无 try/catch
}
```

`SetWinEventHook(WINEVENT_OUTOFCONTEXT)` 的回调由 user32 在安装钩子的线程消息泵内直接调用，`AppAutoMuteModule`、`AwakeModule` 均订阅该事件并执行进程/音频判断。订阅者一旦抛异常，异常将从 native 回调帧逸出，**不保证被 `DispatcherUnhandledException` 捕获**——此时只有 `AppDomain.UnhandledException` 记日志，无法阻止进程终止。

对比 `HotkeyService.cs:185` 已正确使用 `try { cb(); } catch { }`，此处为遗漏。

**修复**：包 `try/catch` 并经 `ILoggerService` 记录；把 `SafeInvoker` 的语义下沉为可复用的 `SafeInvoker.Raise(...)`，供**所有** native 回调与事件扇出统一使用。

### P1-4 AppAutoMute 黑名单路径空引用 → UI 线程未捕获异常

```csharp
// AppAutoMuteModule.cs:231-244  白名单分支正确使用 ?.
var activeProcs = _audioService?.GetActiveAudioProcesses() ?? new List<string>();
_audioService?.SetProcessMute(proc, true);

// AppAutoMuteModule.cs:249-255  黑名单分支无 ?.
if (Config.TargetApps == null) return;
foreach (var app in Config.TargetApps) {
    if (!ProcessHelper.IsMatch(app, currentFore))
        _audioService.SetProcessMute(app, true);    // ← NullReferenceException
}
```

当 `IAudioService` 未注册或已释放时，异常发生在 `DispatcherTimer.Tick` 内。**修复**：统一补 `?.`，并在 `OnStart` 中若服务为空直接把模块置 `Faulted` 而非带病运行。

### P1-5 Cron 触发器漏触发、DST 处理错误、DOM/DOW 语义与标准 cron 不符

```csharp
// Triggers/CronTrigger.cs:22-45
_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
if (_lastFiredMinute == minute) return;
if (CronHelper.IsMatch(now, _expr)) { _lastFiredMinute = minute; ... }
```

1. **漏触发**：只判断"当前这一分钟"，无下次触发点计算、无补偿。UI 线程被阻塞超过 60s（弹窗、编辑器打开、GC 卡顿）该分钟直接跳过。
2. **DST/时钟回拨**：使用 `DateTime.Now` 墙钟。春季前跳使 `0 2 * * *` 当天不触发；秋季回拨 01:xx 重复出现时被 `_lastFiredMinute` 去重抑制；时钟回拨同样漏触发。
3. **语义偏差**：`CronHelper.cs:262` 中 DOM 与 DOW 恒为「与」关系，而标准 cron 在两者都被限定时应为「或」。`0 9 1 * 1` 本意"每月 1 号**或**每周一"，实际仅在"1 号且为周一"触发。
4. `CheckCatchUp()`（:48）仅调一次 `OnTick`，与注释宣称的 catch-up 语义不符。

**修复**：改为基于 `CronHelper.GetNextOccurrence()` 的一次性 `System.Threading.Timer` 精确定时；加"若当前时刻已过上次计算的下次点，则立即补一次"；DOM/DOW 增加字段是否为 `*` 的标记以还原 OR 语义。

### P1-6 MonitorDdcService 的 Sync 路径绕过串行化，与队列并发操作同一物理显示器

```csharp
// MonitorDdcService.cs:117-165  防抖队列（_isApplying/_pending 仅保护 Async 入口）
// MonitorDdcService.cs:170       Sync 版本完全不取 _syncLock
public int SetBrightnessAndContrastSync(int brightness, int contrast) { ... SetVCPFeature ... }

// ProfileScheduleEngine.cs:197-211  由 Task.Run 后台线程调用 Sync 版本
int res = _ddcService.SetBrightnessAndContrastSync(activeSetting.Brightness, activeSetting.Contrast);
```

同时 `MonitorProfileSettingsWindow.DetectHardwareAsync`（:60-61）与 30s `DispatcherTimer` 也会并发枚举物理显示器。DDC/CI 走 I2C，多线程在不同句柄上并发读写同一面板会相互干扰、加剧超时。注释宣称的"杜绝阻塞主线程"仅对 Async 入口成立。

**修复**：在 `MonitorDdcService` 内部引入单一 `SemaphoreSlim(1,1)`，让 Sync/Async/枚举三条路径全部经同一队列。

### P1-7 剪贴板历史在 UI 线程同步全量落盘，且写盘非原子

```csharp
// ClipboardHistoryModule.cs:203-212   事件已封送到 Dispatcher，随后同步执行
Context?.Dispatcher?.InvokeAsync(() => { ... _service?.RecordText(text); ... });

// ClipboardHistoryService.cs:110-112  在 _lock 内落盘
ApplyCleanupRulesLocked(); _storage.Save(_items);

// JsonClipboardHistoryStorage.cs:61-71  全量序列化 + Delete + Move
File.WriteAllText(tempPath, json, Encoding.UTF8);
if (File.Exists(_filePath)) File.Delete(_filePath);   // ← 中间窗口文件不存在
File.Move(tempPath, _filePath);                        // ← 失败则 .tmp 残留
```

1000 条历史全量 JSON 序列化 + 磁盘 I/O 在 UI 线程且持有 `_lock`，剪贴板高频切换时卡 UI；`Delete`+`Move` 非原子，崩溃/断电即丢历史。**注意：项目已有 `AtomicFile.WriteAllText`，此模块未复用**——属跨模块一致性缺失。

**修复**：改为上游合并 + `Task.Run` 落盘或 500ms 节流批量写；统一改用 `AtomicFile`。

### P1-8 ScreenLock.Lock() 失败无回滚，可能造成"假锁定 + 键盘被钩"

```csharp
// LockController.cs:75-89
_locked = true;
_blocker.Install();                     // 全局低级键盘钩子已生效
foreach (var monitor in DisplayMonitorHelper.GetAllMonitors()) {
    var win = new LockWindow(...); _lockWindows.Add(win); win.Show();
    win.ActivateIfNeeded();             // 任一窗口构造/Show 抛异常 → 整体中断
}
// LockSafe:91-101
catch (Exception ex) { Debug.WriteLine(ex); }   // 静默吞掉
```

`ScreenLockModule.OnIdleThresholdReached`（:208-212）调用 `LockSafe`，而空闲状态机已把 `_idleFired = true`（:151），**不会重试**。若首个窗口创建失败，用户处于"Win/Alt 组合键被屏蔽 + 无任何 PIN 输入界面"的状态。

**修复**：`Lock()` 整体 try/catch，失败回滚（移除钩子、关闭已建窗口、`_locked=false`）并 `ShowNotification` 提示；`LockSafe` 返回值化，失败时重置 `_idleFired` 允许重试。

### P1-9 Awake 进程守护状态机边界缺陷："关闭"被立即反转

```csharp
// AwakeService.cs:350-369
if (!string.IsNullOrEmpty(matchedProc)) {
    else if (_mode == AwakeMode.Passive) {     // ← 仅 Passive 模式才联动
        _isProcessTriggered = true; _mode = AwakeMode.Indefinite; ...
    }
}
// AwakeModule.cs:332-338  托盘"关闭"
ClickAction = () => { Service?.SetPassive(); ... }
```

两点：① 用户处于 Timed/UntilTime 时命中守护进程完全不生效（静默失效）；② 守护进程运行期间用户点托盘"关闭"→ `SetPassive`，**5 秒后 `CheckProcessTriggers` 又切回 `Indefinite`**，"关闭"无法生效，与用户意图直接冲突。另 `SetPassive`（:125）把 `_isBatteryPaused` 一并清掉，导致电池挂起态抖动。

**修复**：引入独立 `_processLinked` 标志与"用户手动覆盖位"（手动关闭后本次进程会话不再自动联动）；`SetPassive` 不清电池标志。

### P1-10 后台线程调用托盘气泡未做 Dispatcher 封送，通知静默丢失

```csharp
// App.xaml.cs:332-340
internal void ShowBalloon(string text)
{
    if (_tbIcon == null) return;
    try { _tbIcon.ShowBalloonTip("CarroDesk", text, BalloonIcon.Info); }   // 无 CheckAccess
    catch { }
}
// App.xaml.cs:274-282   ShowBalloonPublic 却正确做了 Dispatcher.BeginInvoke
```

`TaskSchedulerService.cs:221/265` 用 `Task.Run` 在线程池执行任务后发通知，`ClipboardHelper.cs:85` 亦然。`TaskbarIcon` 是 WPF `FrameworkElement`，跨线程访问抛异常被 `catch` 吞掉 → 通知丢失。**同一个类里两种写法并存**，说明是遗漏而非设计。

### P1-11 中文字符串硬编码 420 处，en-US 下大量界面仍显示中文

统计：含中文字面量的 `.cs` 文件 29 个，字符串字面量 **420 处**。分布：

| 文件 | 处数 |
|---|---|
| `TaskScheduler/Views/TaskEditorWindow.xaml.cs` | 61 |
| `Modules/Awake/AwakeModule.cs` | 50 |
| `Modules/MonitorProfile/Views/MonitorProfileSettingsWindow.xaml.cs` | 39 |
| `Modules/AudioSwitch/Views/AudioSwitchSettingsWindow.xaml.cs` | 35 |
| `Modules/ScreenLock/ScreenLockModule.cs` | 21 |

locale 已有 379 key/语言，i18n 基建完备，但 420 处绕过 `Loc.T`，**双语产品名不副实**。

**修复**：UI 可见文案迁入 locale；确属日志/内部提示的可保留，但应加 analyzer 规则或 CI grep 白名单约束。

### P1-12 核心服务零测试覆盖

grep 测试文件提及次数：`HotkeyService` 0、`ForegroundTracker` 0、`TaskRunner` 0、`AudioService` 0、`LockController` 0。

即：热键注册、前台窗口追踪（AppAutoMute 判定核心）、任务真实执行（超时/重试/并发）、音频设备切换、锁屏控制——**本次审查发现的多个 P1 恰恰全部落在这个盲区内**（P1-3、P1-4、P1-8、P1-9 若有测试覆盖均可拦截）。已有 100 个测试偏向元数据/默认值/菜单结构（如 `CarroDeskContractTests` 大量断言 `Header.Contains("屏幕保护")`），属结构契约测试而非行为测试。

**修复优先级**：`TaskRunner`（进程启动/超时/失败重试，纯逻辑易测）> `ForegroundTracker`（可注入进程名解析）> `HotkeyHelper`（纯函数）> `LockController`（`IIdleService` 已可注入）。

---

## 3. P2 改进级

### 3.1 配置与持久化

| # | 问题 | 证据 | 修复 |
|---|---|---|---|
| P2-1 | `ConfigManager.GetModuleConfig` 每次走反射找 `CreateDefault`，且反序列化失败静默返回默认值（随后会被回写覆盖原配置） | `ConfigManager.cs:70` `typeof(T).GetMethod("CreateDefault", ...)`；`:62`、`:78` `catch { }` | 用 `ConcurrentDictionary<Type, MethodInfo>` 缓存；catch 分支至少 `LogWarning` 且**保留原始 token 不回写** |
| P2-2 | `ConfigEditorWindow` 保存时连续写盘 3 次 | `ConfigEditorWindow.xaml.cs:393-395` 调 `SaveModuleConfig`×2 + `Save()`，而 `SaveModuleConfig` 内部已 `Save()`（`ConfigManager.cs:92`） | 增加 `bool persist = true` 参数，批量场景统一收口一次 `Save()` |
| P2-3 | 配置编辑器"重置默认"只重置 UI 控件，未重置数据模型，保存后残留旧 `Language`/`Pin*` | `ConfigEditorWindow.xaml.cs:251-271` | 重置 `_editing` 数据模型而非仅控件 |
| P2-4 | `HostPinService` 的 pending 与 current 语义分叉，可能出现"显示已配置却永远验证失败" | `HostPinService.cs:23-29, 31-47`：`IsConfigured` 可由 `_pendingSalt` 为真，`Verify` 走 `BuildFromCurrent()` | pending/current 合并为单一来源 |
| P2-5 | 任务配置无 schema 版本；文件损坏只备份不重建，下次启动继续失败；空 JSON 被当"零任务"静默接受 | `TaskConfigService.cs:89-92, 113-124` | 顶层写 `"version": 1` 并做迁移；损坏时重命名备份 + 重建可用文件 + 托盘提示；空文件按错误上报 |
| P2-6 | `AtomicFile` 遗留孤儿临时文件 | 实测 `%APPDATA%\CarroDesk\` 存在 `config.json.tmp.6722...`、`config.json.tmp.a899...`（Sep 18） | 失败/退出路径清理 `.tmp.*`；启动时清理超时的孤儿临时文件 |

### 3.2 并发与线程

| # | 问题 | 证据 | 修复 |
|---|---|---|---|
| P2-7 | `SystemIdleService` 的事件加减非原子、`Timer` 不可释放、属性无可见性保证、2s 轮询用取模技巧 | `SystemIdleService.cs:38-60`（`+=` 非原子）、`:36`（类未实现 `IDisposable`）、`:42-43`（后台写 UI 读，无 `volatile`）、`:96`（`Ticks % 2 == 0`） | `Interlocked.CompareExchange` 实现 add/remove；实现 `IDisposable`；属性加内存屏障；改用独立 2s Timer |
| P2-8 | `IdleTrigger._fired` 跨线程无同步 | `IdleTrigger.cs:18, 36-52`：`IdleTick` 在线程池线程触发（见 `SystemIdleService.cs:88`），与 `OnUserActive` 并发读写 | 改 `volatile` 或用 `Interlocked` 原子状态机 |
| P2-9 | `TaskSchedulerService.Apply()` 可从多路径并发调用，无串行保护 → 触发器重复绑定/悬空 | `TaskSchedulerService.cs:146-174`（`StopTriggers`→重建→替换 `_triggers` 非原子），调用方含 `Start`/`Reload`/`SetGlobalEnabled`/`OnConfigReloaded` | 引入 `_applyLock` 串行化 `Apply`，`_triggers` 读写统一临界区 |
| P2-10 | `FileWatcherTrigger` 未订阅 `Error` 事件，缓冲区溢出后静默停止工作 | `FileWatcherTrigger.cs:62-82`：无 `Error +=`，未设 `InternalBufferSize`（默认 8KB） | 订阅 `Error` 并重建 watcher；`InternalBufferSize = 64 * 1024` |
| P2-11 | `ModuleBase` 状态机非线程安全、`Initialize` 非幂等、`Stop` 无条件置 `Stopped`；`Dispose` 路径 `StopAll` 执行两次 | `ModuleBase.cs:16-17, 22-36, 38-57`；`ModuleManager.cs:87-101 + 175-186` | 状态机校验合法迁移（Created→Initialized→Running→Stopped/Faulted）；消除双重 `StopAll` |

### 3.3 架构与解耦

| # | 问题 | 证据 | 修复 |
|---|---|---|---|
| P2-12 | **App 反向依赖全部业务模块**，甚至在 :237 直接消费模块私有 Model | `App.xaml.cs:8-19`（`using` 全部模块）、`:153` `new MonitorProfileModule()`、`:237` `GetModuleConfig<ScreenLock.Models.ScreenLockConfig>("ScreenLock")` | 引入 `IModuleFactory`/模块清单由 `ModuleManager` 统一装配；模块配置读取改走泛型 `IConfigManager` |
| P2-13 | 配置存在双路径：`App.Config`（静态）与 `_configManager`（实例），不同 UI 走不同路径 | `App.xaml.cs:34`，`TrayContextMenu`/`FloatingPanelWindow` 走前者、`App` 内部走后者 | 移除静态门面，统一注入 |
| P2-14 | `ServiceContainer` 是伪 DI：`GetService(type)` 会实例化该类型下**全部**注册再取首个 | `ServiceContainer.cs:50-55, 90-116` | 定向解析首个匹配，`ResolveAll` 仅服务 `GetServices` |
| P2-15 | 退出守卫在 UI 线程同步阻塞 `task.Wait(timeout)`，最多冻结 3s | `App.xaml.cs:378-381` + `SafeInvoker.cs:36-37` | 改异步 `await`，或把守卫决策移后台回传 |
| P2-16 | 设置入口四种实现并存：ScreenLock 复用全局 `ConfigEditorWindow`；Awake/AudioSwitch/AppAutoMute/MonitorProfile 各建窗口；ClipboardHistory 无设置窗口（`MaxItems`/`RetentionDays` 无 UI 可改） | `ScreenLockModule.cs:443`；各模块 `Views/*SettingsWindow` | 收敛为统一的模块设置窗口基类/契约 |
| P2-17 | `IDisposable` 生命周期契约不一致：`LockController` 实现 `IDisposable` 但从不被调用（`ScreenLockModule` 全文无 `Dispose`），静态 `SystemEvents.DisplaySettingsChanged` 永久持有其引用；`AwakeService`/`MonitorDdcService`/`ClipboardHistoryService` 均未实现 | `LockController.cs:148-153`；`ScreenLockModule` 无 `Dispose` | 在 `ModuleBase` 定义统一释放契约：模块 `Dispose` 必须释放其内部 service 并解绑静态事件 |
| P2-18 | `MenuProjectionEngine` 对 `TrayMenuItem` 的 `PropertyChanged`/`CollectionChanged` 订阅永不解除，依赖"模块每次重建节点树"这一脆弱约定；`DetachVisual` 名称与注释宣称"精确解绑"但未解绑 CLR 事件 | `MenuProjectionEngine.cs:71-95`；`DynamicTrayController.cs:216-240` | 处理器提升为具名字段并在 `DetachVisual` 中成对 `-=`（注释与实现必须一致） |

---

## 4. P3 建议级

**正确性与死代码**

- `LockController.ApplyPinFromConfig()` 是空方法（:69-71），却在 `ScreenLockModule.OnConfigReloaded`（:90-93）被调用。
- `AwakeConfig.CustomPresets`（:30）从未被读取，托盘写死 `{15,30,60,120,240,480}`（`AwakeModule.cs:365`）。
- `TaskSchedulerConfig.TasksFile`（:6）从未被读取，任务路径固定走 `ConfigService.TaskFilePath`。
- `TaskRunner.cs:49-60` 的 `else if (isScript && !isRooted)`（:56）恒不可达；`:199` `exited = true` 从未使用。
- `App.UpdateTrayText`（:358-367）方法体恒等于 `ToolTipText = "CarroDesk"`，10 处调用点全部为死调用；`RefreshTaskMenu`/`RefreshMenuChecks`（:265-272, 327-330）逻辑完全重复。
- `StartupTrigger.cs:24-35` `delay * 1000` 可溢出为负 → `Task.Delay` 抛异常被吞 → 任务永不执行；应用 `TimeSpan.FromSeconds` 并限制上限。
- `ScriptResolver.cs:39-55` 存在路径穿越：`file = "..\\..\\Windows\\System32\\calc.exe"` 经 `Path.Combine` 规范化后仍在 `scripts/` 外，`File.Exists` 为真即返回。应校验解析结果必须位于 scripts 根目录之内。
- `TaskConditionEvaluator.cs:31-95` 全部条件为 **fail-open**：异常即视为满足放行。用户勾选"仅交流供电"正是为了"不满足就不要跑"，应在无法判定时按"不满足"处理并记 WARN。
- `HotkeyTrigger.cs:30-38` 热键注册失败仅写日志，无用户提示——用户以为任务已启用，实际永久失效。

**UI 与一致性**

- **命名空间与目录不一致**：`AwakeSettingsWindow`、`LockWindow`、`AppAutoMuteSettingsWindow`、`AudioSwitchSettingsWindow`、`MonitorProfileSettingsWindow` 的 namespace 全部是 `CarroDesk.Views`（各自 :14/:12/:18/:16/:12），只有 `ClipboardHistoryWindow` 用 `CarroDesk.Modules.ClipboardHistory.Views`。业务模块因此反向依赖宿主命名空间，破坏垂直切片边界。
- `AudioSwitchModule` 用 `public new AudioSwitchConfig Config` 遮蔽基类属性（:24-28），同一实例存在两个 Config 语义，泛型/接口视角下会读到 `null`。
- 三段几乎逐行相同的"输入分钟数"对话框：`AwakeModule.PromptMinutes`（:482-554）、`ScreenLockModule.PromptPauseMinutes`（:476-548）、`MonitorProfileSettingsWindow.PromptInput`（:261-329），应抽为 `Host/Dialogs` 公共组件。
- 重复的设备图标/短名逻辑：`AudioSwitchModule.GetDeviceIcon`（:306-315）与 `AudioSwitchSettingsWindow.GetDeviceIcon`（:225-234）。
- `FirstRunWindow.ShakeCard`（:81-93）与 `VerifyPinWindow.ShakeCard`（:59-71）逐行相同。
- `ClipboardHelper.SimulatePaste`（:83-97）用已废弃的 `keybd_event` + 线程池盲等 60ms 发 Ctrl+V，窗口 `Hide()` 后未等待前台窗口恢复，有粘贴到错误目标的风险；应换 `SendInput`。

**God Object（>400 行，需拆分）**

| 文件 | 行数 |
|---|---|
| `TaskScheduler/Views/TaskEditorWindow.xaml.cs` | 1019 |
| `Views/FloatingPanelWindow.xaml.cs` | 635 |
| `MonitorProfile/Services/MonitorDdcService.cs` | 604 |
| `TaskScheduler/Services/TaskConfigService.cs` | 571 |
| `Awake/AwakeModule.cs` | 556 |
| `ScreenLock/ScreenLockModule.cs` | 549 |

其中 `TaskEditorWindow.ValidateForm`（:517-661）与 `TaskDefinition.Validate`（:168-215）**两份校验规则几乎逐字重复**——一旦漂移即出现"UI 通过但落盘失败"。应合并为单一数据源。另 `TaskEditorWindow` 的 `_cronDebounceTimer` 在 `OnClosing`（:1014-1017，目前是空实现）未 `Stop()`。

**工程规范**

- `.slnx` 未包含测试工程（`CarroDesk.slnx:2` 仅 `src/CarroDesk.csproj`），对解决方案执行构建/CI 时**不会编译测试**，回归保护形同虚设。
- 无 `TreatWarningsAsErrors`、`EnableNETAnalyzers`、`AnalysisLevel`、StyleCop；无 `global.json` 固定 SDK（当前用 SDK 10 构建 net48）。
- 无 README、无 LICENSE、无 `.github/workflows` CI。
- `app.manifest:5-6` 仅 `dpiAware=true`（系统级），缺 `dpiAwareness=PerMonitorV2` 与 Win10/11 `supportedOS` GUID。本项目含多显示器亮度调度与悬浮面板，混合 DPI 多屏下会模糊/错位。（管理员权限当前为 `asInvoker`，对托盘工具是正确的，无需改。）
- 测试工程 `EnableDefaultCompileItems=false` + 逐条 `<Compile Include>`，新增测试文件忘登记会**静默不编译**。
- 测试脆弱性：`UiRenderingTests.cs:88,138` 用 `..\..\..\..\..` 反推工程根、`:76` `Thread.Sleep(150)`；`ConfigAndTriggerTests.cs:183-195` 反向遍历文件系统找 `.git`；`CronHelperTests.cs:157` 断言 500 次求值 `< 200ms`（墙钟断言，CI 负载下易假失败）；`ProcessHelperTests.cs:87-99` 依赖真实桌面进程状态。建议改用 `AppContext.BaseDirectory` + 项目锚点，性能断言移出单测。
- 语言文件占位符不一致 1 处：`Tray.AwakeNotifyProcessActive` zh 无 `{0}`、en 有 `{0}`；调用点 `AwakeModule.cs:189` 用 `Loc.T(key, defaultValue)` 重载（不执行 `string.Format`），导致 zh 丢失进程名、en 显示字面量 `{0}`。建议补全并增加"占位符跨语言一致性"单测覆盖全部 379 key。
- `docs/` 中规范类文档路径已失效：`docs/CHANGES-20260918.md:75` 引用 `src/Services/IdleDetector.cs`、`:117` 与 `CODE-REVIEW-20260918-bd.md:16` 引用 `src/Services/Tasks/TaskSchedulerService.cs`、`:124` 引用 `src/Views/AwakeSettingsWindow.*` —— 三者均已不存在。历史 CHANGES 可归档保留，但 `CODE-REVIEW`/`MODULE-DEVELOPMENT-GUIDE` 应更新。

**其他**

- 单实例 Mutex 只 `ReleaseMutex()` 未 `Dispose()`（`App.xaml.cs:53, 493`）；第二个实例直接 `Shutdown`，未激活已存在实例（与 `DESIGN.md` §4.1 承诺不符）。
- `CronHelper.cs:41` 的 `_cache` 以任意表达式串为键，无容量上限。
- `ModuleBase.DefaultEnabled=false` 是死路径：`ModuleManager.InitializeAll` 只初始化 `DefaultEnabled` 模块（:53），`StartAll` 又拒绝非 `Initialized`（:74），当前无任何启用入口，属预留但未闭环的设计。

---

## 5. 跨模块共性问题（建议专项治理）

这四项不属于任何单一模块，但贡献了本次审查中**最多的缺陷条目**，建议作为一次专项重构统一处理，收益远高于逐条修补。

### 5.1 空 `catch { }` 静默失败文化 —— 172 处，全仓第一号技术债

统计：`src/` 下无任何日志的 `catch { }` 共 **172 处**（较 0918 的 186 处略有下降）。

典型受害路径：`AwakeModule.SaveConfig`（:93）、`AwakeModule.RequestRefreshTray`（:98）、`AwakeModule.RegisterHotkey`（:113）、`ConfigManager.cs:62/78/94`、`ConfigService.cs:118/205`、各模块 `MonitorProfileModule` 6 处 / `AudioSwitchModule` 6 处 / `ScreenLockModule` 6 处 / `LockWindow` 5 处。

**治理方案**：Host 已有 `SafeInvoker.Run(id, action, onError)`，但只有 `ModuleManager` 在用。

1. 规定"**允许静默的仅限：native 清理路径（Dispose/Unhook）、已降级的可选项**"，其余一律 `catch (Exception ex) { logger?.LogWarning(...) }`。
2. 加 Roslyn analyzer 或在 CI 中用 `grep -rn "catch\s*{\s*}"` 做行数基线门禁（只允许下降）。
3. `DynamicTrayController.TryLog` 已是正确范例，推广之。

### 5.2 并发模型未定义 —— 需要一份明确的线程约定

当前 `ConfigService`、`SystemIdleService`、`MonitorDdcService`、`TaskSchedulerService`、`LockController` 各自为政，**代码中没有任何一处线程模型的显式说明**。建议在 `MODULE-DEVELOPMENT-GUIDE.md` 中确立并强制执行：

- **谁在哪条线程**：写入 `Dependency Object` / WPF 对象的操作**只能**在 Dispatcher 线程。所有 `SystemEvents.*` 回调（`SessionSwitch`/`PowerModeChanged`/`DisplaySettingsChanged`）、`SetWinEventHook` 回调、`DispatcherTimer` 之外的 Timer 回调、线程池回调，**入口第一件事就是 `Dispatcher.BeginInvoke`**。
- **共享可变状态的归属**：`ConfigService` 是全局单例，必须自带锁（P0-3）；模块内部状态用 `volatile`/`Interlocked` 或明确"仅 UI 线程访问"。
- 提供统一的 `UiThread.Invoke(action)` Helper，替代各模块自行写的 `CheckAccess` 分支（当前 `AwakeModule.cs:300-311`、`AudioSwitchModule.cs:318-329`、`AppAutoMuteModule.cs:159-169`、`ScreenLockModule.cs:331-342` 各写了一遍）。

### 5.3 `IDisposable` 与事件生命周期契约不统一

`ModuleBase.Dispose` 只调 `Stop()`，而各模块 `OnStop` 的清理程度差异巨大（Awake 有 `UnregisterAll`，ClipboardHistory 没有）。静态事件（`SystemEvents.*`）是最容易泄漏的一类，`LockController` 已实证此问题（P2-17）。

**治理方案**：在 `ModuleBase` 中定义并文档化释放契约——模块 `Dispose` **必须**：① 解绑所有静态事件；② 释放其持有的 service；③ 停止并释放所有 Timer。建议为 `IModule` 增加 `IEnumerable<string> GetActiveSubscriptions()` 调试接口，退出时打印未清理项。

### 5.4 安全边界的三处缺口

1. `TaskRunner` 命令行注入（P1-2）——任务配置是唯一的外部输入面。
2. `ScriptResolver` 路径穿越（P3）。
3. `PropVariant` 内存越界（P0-4）。

考虑到本项目会解析用户可编辑的 JSON 配置文件与可执行脚本，建议把"**配置文件视为不可信输入**"写进设计原则：所有来自配置的路径做规范化 + 根目录校验，所有来自配置的参数做类型化传递而非字符串拼接。

---

## 6. 架构评估与演进建议

### 6.1 当前架构评价：方向正确，收口未完成

`IModule` / `ModuleBase<TConfig>` / `IModuleContext` / `ModuleManager` / `ServiceContainer` / `DynamicTrayController` 构成的进程内模块化单体骨架是合理选择——对一个本地托盘工具而言，引入完整 DI 容器或插件化加载（AssemblyLoadContext）是过度设计。`IModuleContext` 只暴露 `GetService`/`Dispatcher`/`RequestTrayRefresh`/`ShowNotification` 四个能力，边界克制得当；`MenuProjectionEngine` 把 `TrayMenuItem` 树投影为 WPF 视觉树的双锚点设计，也比直接把 `MenuItem` 暴露给模块更干净。**这部分不需要动。**

问题在收口：`App.xaml.cs` 仍 `using` 全部 6 个业务模块并直接 `new`，这一处把"宿主零感知业务"的铁律打破了（P2-12）。与 0918 相比，`TaskSchedulerService`/`ConfigManager` 里的静态门面已被清理，**但依赖方向的问题从"服务层"转移到了"入口层"**。

### 6.2 建议的三个演进方向（按 ROI 排序）

**方向一（高 ROI，建议本迭代做）：把"防线"补上，而非继续加功能。**

本次 4 项 P0 中有 3 项是"只要有一道自动化防线就能拦住"的：
- 测试隔离（P0-1）→ 一个 `[AssemblyInitialize]` 就够；
- 订阅/解绑不对称（P1-1）→ 一个简单的静态检查或 `Dispose` 时的订阅审计；
- 跨线程 UI 访问（P0-2）→ 在现有 `UiRenderingTests` 基础上加一个"在非 UI 线程调用 `Unlock()`"的测试即可复现。

具体动作：① 修 `.slnx` 纳入测试工程；② 加最小 CI（`windows-latest` + `dotnet test`）；③ 启用 `EnableNETAnalyzers`；④ 给 `ConfigService` 加测试隔离。**这四项加起来不到一天，但能阻止同类问题再次进入主干。**

**方向二（中 ROI）：以"配置契约"替代"配置字典"。**

当前配置的读写路径是 `JToken` 字典 + 反射 `CreateDefault` + 泛型反序列化，缺少：schema 版本、字段级校验、迁移机制、损坏恢复。建议为每个模块的 Config 类增加 `static int SchemaVersion` 与 `static MigrationResult Migrate(JObject)`，`ConfigManager` 统一在 `GetModuleConfig` 时校验版本并按需迁移。这同时解决 P2-1、P2-5，并为"配置导入导出/跨版本升级"打好地基。

**方向三（中 ROI）：拆分三个 God Object，但**先合并重复逻辑再拆**。**

`TaskEditorWindow`（1019 行）的拆分应**从消除 `ValidateForm` 与 `TaskDefinition.Validate` 的重复开始**——否则拆分只会把重复代码复制到更多文件。同样，`AwakeModule.GetTrayMenuItems`（:313-471，约 160 行内联构建）应先抽 `BuildModeMenu/BuildTimedMenu/BuildUntilMenu`，再考虑移动文件。

### 6.3 明确不建议做的事

- **不要引入完整 DI 容器（Autofac 等）**：当前 `ServiceContainer` 的规模（151 行）与该项目的复杂度匹配，重构成本远大于收益。
- **不要引入 AssemblyLoadContext 插件化**：垂直切片的收益已通过编译期模块获得，运行时隔离对托盘工具体验无实际改善。
- **不要为 `PropVariant` 等问题改用 CsWin32/源生成**：单点修复即可，改造面过大。
- **不要追求测试覆盖率数字**：优先为 P1-12 列出的 4 个关键服务补行为测试，而非补齐 100 个结构断言。

---

## 7. 改进路线图

### 批次 1 —— 止血（本迭代，建议 1-2 天）

| 项 | 动作 | 验收标准 |
|---|---|---|
| P0-1 | 测试隔离存储基目录，全面改用 `FakeConfigManager` | 跑完测试后 `%APPDATA%\CarroDesk\config.json` 的 mtime 与内容均不变 |
| P0-2 | `OnSessionSwitch` 改 `Dispatcher.BeginInvoke`；`Unlock` 加 `CheckAccess` 守卫；先关窗成功再 `Clear` | 新增单测：非 UI 线程调用 `Unlock()` 后窗口引用仍可关闭 |
| P0-3 | `ConfigService` 加 `_ioLock` 串行化 + `Save` 前快照；`ConfigManager` 空 catch 改为记日志并重抛 | 并发 100 次 `SaveModuleConfig` 无异常、无丢失 |
| P0-4 | 修正 `PropVariant` 结构尺寸 | `SetBrightness`/设备名读取在 x64 下无越界（用 `Marshal.SizeOf` 断言 = 24） |
| P1-1 | 补 `PowerModeChanged +=` | 单测断言 `Start()` 后事件已注册 |
| P2-2 | `.slnx` 纳入测试工程 | `dotnet build` 在解决方案级编译测试 |

### 批次 2 —— 正确性与并发（下一迭代）

P1-2（TaskRunner 注入/输出/孤儿进程）、P1-3（回调异常隔离 + `SafeInvoker` 推广）、P1-4（`_audioService?.`）、P1-5（Cron 下次触发点调度）、P1-6（DDC 统一串行化）、P1-8（Lock 失败回滚）、P1-9（Awake 守护覆盖位）、P2-7~P2-11（`SystemIdleService`/`IdleTrigger`/`Apply`/`FileWatcher`/`ModuleBase` 状态机）。

**验收**：`docs/MODULE-DEVELOPMENT-GUIDE.md` 新增"线程模型"章节；全仓 `catch { }` 计数不高于当前基线并持续下降。

### 批次 3 —— 架构收口与可维护性（后续迭代）

P2-12~P2-18（App 反向依赖、双配置路径、ServiceContainer、退出守卫、设置入口统一、Dispose 契约、菜单订阅解绑）、P1-11（i18n 迁移 420 处）。

### 批次 4 —— 工程化与清理（顺手做）

P1-12（核心服务补测）、P3 全部、README/LICENSE/CI、`app.manifest` DPI 与 supportedOS、文档路径修正。

---

## 8. 与 2026-09-18 两轮审查的对照

| 0918 提出的问题 | 当前状态 | 证据 |
|---|---|---|
| 静态门面依赖（`App.Config`/`App.ShowBalloonPublic` 直连） | ✅ **基本修复**（`TaskSchedulerService` 已无静态直连） | 本次 grep 未复现 |
| `ConfigService.Save()` 非原子写 | ✅ **已修复** | 现有 `AtomicFile.WriteAllText` |
| `IdleDetector.cs` 死代码 | ✅ **已移除** | 文件不存在 |
| `ConfigManager` 硬编码 6 个模块类型 | ✅ **已改为泛型 + 反射** | `ConfigManager.cs:70` |
| `ProfileScheduleEngine` 跨午夜取错时间段 | ✅ **已修复** | 现按分钟跨天计算 |
| 致命异常一律 `e.Handled = true` | ✅ **已改为 FailFast** | `App.xaml.cs` 现按致命性分流 |
| 空 `catch { }` 186 处 | ⚠️ **172 处，略降但仍为第一号债** | 本次实测 |
| View 层直连 `App.Config/App.Services` | ⚠️ **部分改善**（40+ 处 → 少量残留），但入口层新增反向依赖 | P2-12、P2-13 |
| **本次新发现** | | |
| 测试污染生产配置 | 🆕 **P0-1**（实测复现） | 见 §1 |
| ScreenLock 跨线程解锁锁死用户 | 🆕 **P0-2** | 见 §1 |
| `ConfigService` 并发写 + 静默吞异常 | 🆕 **P0-3**（0918 仅提原子写，未提并发） | 见 §1 |
| `PropVariant` 越界写 | 🆕 **P0-4** | 见 §1 |
| `PowerModeChanged` 订阅回归 | 🆕 **P1-1**（重构引入的回归） | 见 §2 |
| ForegroundTracker 回调异常未隔离 | 🆕 **P1-3** | 见 §2 |
| `_audioService` 空引用不一致 | 🆕 **P1-4** | 见 §2 |
| DDC Sync 路径绕过串行化 | 🆕 **P1-6** | 见 §2 |
| 核心服务零测试覆盖 | 🆕 **P1-12** | 见 §2 |

**趋势判读**：架构类问题在收敛，正确性/并发类问题在新增。这说明 0918 的整改集中在"结构与依赖"上并按计划完成了，但**没有同步建立起捕捉正确性缺陷的机制**（测试盲区恰好覆盖了所有新发现的 P1）。因此本次报告把"补防线"排在"加功能"和"拆文件"之前。

---

## 附录 A：审查方法与量化基线

| 指标 | 数值 |
|---|---|
| `src/` 下 `.cs` 文件 | 109 |
| `src/` 下 `.xaml` 文件 | 13 |
| 源码总行数（不含 obj/bin） | 20,807 |
| `tests/` 测试文件 | 15（100 个 `[TestMethod]`） |
| 测试实测结果 | **100 通过 / 0 失败 / 0 跳过，14s** |
| 无日志 `catch { }` | 172 |
| 中文字符串字面量（`.cs` 中） | 420（29 个文件） |
| >400 行文件 | 13 |
| 最大文件 | `TaskEditorWindow.xaml.cs` 1019 行 |
| locale key 对齐 | 379 / 379 完全对齐（zh-CN ⇄ en-US） |
| 已知高危依赖漏洞 | 无（Newtonsoft.Json 13.0.4 已含 CVE-2024-21907 修复） |

**审查方法**：按"宿主/核心基础设施""TaskScheduler""其余业务模块""工程化与测试"四个区域做全量精读与交叉验证；对报告中的每一项 P0/P1 结论，均通过直接打开源码文件复核行号与代码片段，并用独立命令复现（含实际运行测试、实际检查 `%APPDATA%` 文件 mtime 与内容）。未经复现的推断性结论已在正文中显式标注为"风险"而非"缺陷"。

**核验记录**（本次亲自复核的关键证据）：
- `ForegroundTracker.cs:57-64` 回调无 try/catch ✅
- `ConfigService.cs` 全文件无 `lock` ✅
- `TaskSchedulerService.cs` 仅 `-=`（:115）无 `+=` ✅
- `ComInterfaces.cs:54-62` `PropVariant` 尺寸不足 ✅
- `ScreenLockModule.cs:224-238` 在 SystemEvents 线程调 `Unlock()` ✅
- `LockController.cs:127-138` 先 `CloseSafe` 后 `Clear` ✅
- `TaskRunner.cs:56-60` 命令行拼接 ✅
- `AppAutoMuteModule.cs:249-255` 缺 `?.` ✅
- `App.xaml.cs:332-340` `ShowBalloon` 无封送（对比 :274 有）✅
- 测试污染 `%APPDATA%\CarroDesk\config.json`（mtime 21:56 + 夹具值吻合）✅

---

*报告生成：2026-09-19*
