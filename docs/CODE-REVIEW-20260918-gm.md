# CarroDesk 全面代码审查与架构演进报告 (CODE-REVIEW-gm)

> **审查日期**：2026-09-18  
> **项目版本**：v0.1.0 (Modular Monolith 架构演进阶段)  
> **目标平台**：Windows 10 / 11 (x86/x64)  
> **技术栈**：.NET Framework 4.8 + WPF (C# 7.3) / Costura.Fody 单文件打包  

---

## 目录
1. [执行摘要 (Executive Summary)](#1-执行摘要-executive-summary)
2. [架构设计与解耦深度审查](#2-架构设计与解耦深度审查)
3. [核心逻辑与隐蔽缺陷清单 (Bugs & Defects)](#3-核心逻辑与隐蔽缺陷清单-bugs--defects)
4. [代码质量、性能瓶颈与资源管理](#4-代码质量性能瓶颈与资源管理)
5. [Windows 底层与硬件互操作审查](#5-windows-底层与硬件互操作审查)
6. [架构升级与更优演进方案](#6-架构升级与更优演进方案)
7. [整改实施优先级路线图](#7-整改实施优先级路线图)

---

## 1. 执行摘要 (Executive Summary)

**CarroDesk** 从最初单一的 `ScreenLock`（闲时伪锁屏）成功重构转型为**进程内模块化单体 (In-Process Modular Monolith)** 桌面工具箱宿主。当前已集成 **屏幕伪锁屏 (ScreenLock)**、**计划任务 (TaskScheduler)**、**音频设备切换 (AudioSwitch)**、**应用自动静音 (AppAutoMute)**、**保持唤醒 (Awake)**、**显示器情境调节 (MonitorProfile)** 等丰富功能。

### 综合评价
- **优势亮点**：
  1. **架构选型契合实际**：规避了 .NET 4.8 下外部 DLL 插件加载带来的 AppDomain 隔离与依赖冲突问题，保持了 `Costura.Fody` 单 EXE 独立便携免安装的核心分发优势。
  2. **契约抽象规范**：建立了 `IModule`、`ModuleBase<TConfig>`、`IConfigManager`、`IAudioService`、`IForegroundTracker`、`IIdleService` 等关键底层抽象，具备良好的工程规范意识。
  3. **响应式托盘体验流畅**：`DynamicTrayController` 采用双锚点插槽注入、属性单点更新绑定、150ms 防抖与菜单展开时延迟更新机制，彻底解决了 WPF 托盘菜单因整树重建而闪退或打断展开的问题。
  4. **工程健壮度高**：具备 62 项覆盖安全加密、Cron 语法解析、模板展开、进程归一化的单元测试（100% 通过），版本管理统一由 `Directory.Build.props` 驱动。

- **突出风险与短板**：
  1. **存在高危业务 Bug**：`SystemIdleService` 中 Windows 原生通知状态枚举错位，导致正常桌面办公被误判为忙碌而冻结锁屏计时，全屏游戏挂机中反而漏判忙碌；`FloatingPanelWindow` 递归重建造成事件监听雪崩泄露；`AppAutoMuteModule` 白名单模式下前台聚焦应用无法解静音。
  2. **配置层违背开闭原则 (OCP)**：`ConfigManager` 和 `ConfigService` 内部充斥对具体业务模块的 `if-else` 硬编码，子模块配置被转义为包含 `\"` 的嵌套字符串直接落盘。
  3. **算法性能隐患**：`CronHelper.GetNextOccurrence` 存在 52 万次循环的暴力穷举与数百万级字符串瞬时分配，且在 UI 文本输入事件中被实时调用。
  4. **非托管句柄生命周期疏漏**：`AwakeService` 中进程枚举句柄未释放；`MonitorDdcService` WMI COM 对象未完全加入 `using` 保护。

---

## 2. 架构设计与解耦深度审查

```mermaid
graph TD
    subgraph HostCore [宿主核心 Host]
        AppEntry[App.xaml.cs 宿主入口]
        Container[ServiceContainer 轻量依赖容器]
        ModMgr[ModuleManager 模块总线]
        TrayCtrl[DynamicTrayController 托盘聚合引擎]
        CfgMgr[ConfigManager 分区配置管理]
    end

    subgraph Infra [基础设施服务 Services]
        AudioSvc[AudioService (WASAPI/CoreAudio)]
        FgTracker[ForegroundTracker (WinEventHook)]
        IdleSvc[SystemIdleService (GetLastInputInfo)]
        HotkeySvc[HotkeyService (RegisterHotKey)]
        I18nSvc[I18nService (响应式多语言)]
    end

    subgraph Modules [内置业务模块 Modules]
        M_Lock[ScreenLockModule]
        M_Task[TaskSchedulerModule]
        M_Audio[AudioSwitchModule]
        M_Mute[AppAutoMuteModule]
        M_Awake[AwakeModule]
        M_Mon[MonitorProfileModule]
    end

    subgraph Views [视图呈现 Views]
        V_Tray[TrayContextMenu]
        V_Panel[FloatingPanelWindow]
        V_Lock[LockWindow]
        V_Editors[Config/Task/Awake/Monitor Editors]
    end

    AppEntry --> Container --> Infra
    AppEntry --> ModMgr --> Modules
    TrayCtrl --> V_Tray
    Modules -.-> Infra
    Views -.-> HostCore
    Views -.-> Modules
```

### 2.1 静态 God Object 残留与控制反转不彻底
- **现状**：
  虽然引入了 `ServiceContainer` 和 `IModuleContext`，但全局各处仍广泛耦合 `App` 静态门面：
  - `App.Config.Current`：在 `TrayContextMenu.xaml.cs`、`FloatingPanelWindow.xaml.cs`、`TaskSchedulerService.cs`、`ConfigEditorWindow.xaml.cs` 等多处被直接读写；
  - 各模块自行暴露静态单例（`ScreenLockModule.Instance`、`AwakeModule.Instance`、`AudioSwitchModule.Instance`、`TaskSchedulerModule.Instance`）；
  - `AppAutoMuteSettingsWindow.xaml.cs` 直访 `App.AppAutoMuteMod?.Config` 与 `App.Services?.GetService<AudioService>()`。
- **弊端**：
  静态上下文导致模块无法进行完全隔离的单元测试与并行测试，且容易在初始化早期因访问未初始化的静态属性引发 `NullReferenceException`。

### 2.2 配置持久化层违背开闭原则 (OCP)
- **现状**：
  - [`src/Host/Services/ConfigManager.cs`](file:///c:/Home/Projects/CarroDesk/src/Host/Services/ConfigManager.cs#L40-L178) 在 `GetModuleConfig<T>` 与 `SaveModuleConfig<T>` 中，全部采用 `if-else` 分支显式匹配 `"ScreenLock"`、`"TaskScheduler"`、`"AudioSwitch"`、`"AppAutoMute"`、`"MonitorProfile"`、`"Awake"`。
  - [`src/Services/ConfigService.cs`](file:///c:/Home/Projects/CarroDesk/src/Services/ConfigService.cs#L71-L75) 显式声明了 `AudioSwitchJson`、`AppAutoMuteJson`、`MonitorProfileJson`、`AwakeJson` 等专属属性。
  - 在 `ConfigService.Save()` 中直接存为字符串：
    ```csharp
    obj["AudioSwitch"] = this.AudioSwitchJson ?? "";
    ```
- **弊端**：
  1. 每当新增模块，必须同时侵入修改 `ConfigService` 和 `ConfigManager`；
  2. 磁盘上的 `config.json` 将子模块配置序列化为带大量 `\"` 转义的字符串（例如 `"AudioSwitch": "{\"Enabled\":true}"`），破坏了用户直接通过文本编辑器修改 JSON 配置的体验。

### 2.3 模块初始化与生命周期防御漏洞
- **涉及文件**：[`src/Host/Services/ModuleManager.cs`](file:///c:/Home/Projects/CarroDesk/src/Host/Services/ModuleManager.cs#L53-L77)
- **现状**：
  `InitializeAll` 中通过 `Where(m => m.DefaultEnabled)` 过滤只初始化默认启用的模块：
  ```csharp
  foreach (var module in Modules.Where(m => m.DefaultEnabled))
  {
      module.Initialize(context);
  }
  ```
  而在 `StartAll` 中：
  ```csharp
  foreach (var module in Modules)
  {
      if (module.Status == ModuleStatus.Faulted) continue;
      module.Start();
  }
  ```
- **隐患**：
  如果某个模块在未来被定义为 `DefaultEnabled => false`，该模块将不会执行 `Initialize`（`Status` 仍为 `Created`），但在 `StartAll` 中由于其非 `Faulted`，仍会被调用 `Start()`。由于其 `Context` 为 `null`，一旦内部访问 `Context.GetService<T>()` 将立即抛出 `NullReferenceException` 导致启动崩溃。

### 2.4 模型文件职责膨胀
- **涉及文件**：[`src/Models/TaskDefinition.cs`](file:///c:/Home/Projects/CarroDesk/src/Models/TaskDefinition.cs)
- **现状**：
  该文件长达 561 行，包含了领域模型实体，但同时塞入了：
  1. `CronHelper`（Cron 语法解析、区间计算、穷举预测、中文释义）
  2. `HotkeyHelper`（Win32 虚拟键码解析、修饰键转换）
- **改进建议**：
  将 `CronHelper` 和 `HotkeyHelper` 抽离为独立的工具类和算法服务，让 Model 目录回归纯粹的 DTO/领域实体定义。

---

## 3. 核心逻辑与隐蔽缺陷清单 (Bugs & Defects)

### 🔴 缺陷 1：`SystemIdleService` 系统忙碌枚举值错位（严重缺陷）
- **位置**：[`src/Host/Services/SystemIdleService.cs`](file:///c:/Home/Projects/CarroDesk/src/Host/Services/SystemIdleService.cs#L28-L33)
- **代码分析**：
  ```csharp
  // 当前代码：
  private const int QUNS_BUSY = 2;
  private const int QUNS_RUNNING_D3D_FULL_SCREEN = 4; // 错误！Windows API 定义实际是 3
  private const int QUNS_PRESENTATION_MODE = 5;         // 错误！Windows API 定义实际是 4
  private const int QUNS_ACCEPTS_NOTIFICATIONS = 6;   // 错误！实际是 5
  private const int QUNS_QUIET_TIME = 7;              // 错误！实际是 6
  ```
  Windows `SHQueryUserNotificationState` 真实枚举：
  ```c
  typedef enum {
      QUNS_NOT_PRESENT = 1,
      QUNS_BUSY = 2,
      QUNS_RUNNING_D3D_FULL_SCREEN = 3, // D3D 全屏游戏
      QUNS_PRESENTATION_MODE = 4,       // 演示模式
      QUNS_ACCEPTS_NOTIFICATIONS = 5,   // 正常桌面接受通知
      QUNS_QUIET_TIME = 6,              // 免打扰
      QUNS_APP = 7
  } QUERY_USER_NOTIFICATION_STATE;
  ```
- **缺陷表现**：
  1. 正常桌面办公状态下，API 返回 `5` (`QUNS_ACCEPTS_NOTIFICATIONS`)。但代码中 `state == QUNS_PRESENTATION_MODE`（误定义为 5）命中了忙碌条件！导致正常使用电脑时，`IsSystemBusyCached` 持续为 `true`，**锁屏闲时计时被意外冻结，防误碰锁屏无法生效**。
  2. 全屏 3D 游戏（返回 `3`）时，代码并未检测 `3`，反而判定为“不忙碌”，在游戏挂机中误弹锁屏。
- **修复方案**：将常量定义修正为标准值 2, 3, 4。

---

### 🔴 缺陷 2：`FloatingPanelWindow` 递归重建引发事件监听雪崩泄露
- **位置**：[`src/Views/FloatingPanelWindow.xaml.cs`](file:///c:/Home/Projects/CarroDesk/src/Views/FloatingPanelWindow.xaml.cs#L572-L576)
- **代码分析**：
  ```csharp
  private object CreateVisual(TrayMenuItem node)
  {
      ...
      // 监听子项变化动态重构
      node.Children.CollectionChanged += (s, e) =>
      {
          Dispatcher.BeginInvoke(new Action(RebuildMenu));
      };
      return menuItem;
  }
  ```
- **缺陷表现**：
  `node` 是各业务模块长期存活的菜单模型对象。`FloatingPanelWindow.ShowPanel()` 每次打开都会调用 `RebuildMenu()`，为每个节点的 `Children.CollectionChanged` 注册一个捕获了当前窗口引用的匿名委托。
  打开悬浮窗 $N$ 次，事件就会被绑定 $N$ 次。一旦子菜单项变更，将触发 $N$ 次 `RebuildMenu`，每次又注册更多事件，引发严重雪崩并彻底阻止窗口对象被 GC 回收。
- **修复方案**：
  在 `CreateVisual` 中禁止向全局模型对象绑定匿名事件；应通过集中式的刷新机制触发 UI 重新渲染。

---

### 🟠 缺陷 3：`AppAutoMuteModule` 白名单模式下前台应用无法发声
- **位置**：[`src/Modules/AppAutoMute/AppAutoMuteModule.cs`](file:///c:/Home/Projects/CarroDesk/src/Modules/AppAutoMute/AppAutoMuteModule.cs#L183-L204)
- **代码分析**：
  ```csharp
  private void EvaluateForeground(string procName)
  {
      bool isTarget = ProcessHelper.ContainsProcess(Config.TargetApps, procName);
      if (isTarget)
      {
          _muteTimer.Stop();
          _lastTargetProc = procName;
          _unmuteTimer.Start();
      }
      else
      {
          _unmuteTimer.Stop();
          _muteTimer.Start();
      }
  }
  ```
- **缺陷表现**：
  - **黑名单模式**：`TargetApps` 记录“切后台需静音的进程（如游戏）”，当前台是游戏时解静音，切出时静音，逻辑正常。
  - **白名单模式**：`TargetApps` 记录“在后台也允许播放声音的应用（如音乐播放器）”。当用户聚焦到前台应用（例如 Chrome 播放视频，Chrome 并不在后台白名单中）时，`isTarget` 为 `false`，导致进入 `else` 分支停止解静音并启动静音定时器！**最终导致用户在前台操作的任何非白名单应用全部被静音**。
- **修复方案**：
  业务规则应为：**当前获得前台焦点的应用无论在何种模式下，都必须无条件解除静音**。模式的区别仅在于后台进程的处理策略。

---

### 🟠 缺陷 4：`FileWatcherTrigger` 单一时间戳防抖导致批量文件被吞
- **位置**：[`src/Services/Tasks/Triggers/FileWatcherTrigger.cs`](file:///c:/Home/Projects/CarroDesk/src/Services/Tasks/Triggers/FileWatcherTrigger.cs#L90-L98)
- **代码分析**：
  ```csharp
  private void OnEvent(object sender, FileSystemEventArgs e)
  {
      // debounce 500ms per file
      var now = DateTime.Now;
      if ((now - _lastFired).TotalMilliseconds < 500) return;
      _lastFired = now;
      var h = Fired;
      if (h != null) h(Task, "watch:" + e.ChangeType + ":" + e.Name);
  }
  ```
- **缺陷表现**：
  代码注释为 `per file`（针对单文件防抖），但实际使用的 `_lastFired` 是整个触发器单例共享的字段。
  当用户同时将 10 个文件拖入监听目录，或自动化程序批量生成文件时，第 1 个文件触发后，其余 9 个文件在 500ms 内全部被丢弃。
- **修复方案**：
  引入 `ConcurrentDictionary<string, DateTime> _fileDebounceMap`，按文件全路径单独防抖，并定期清理过期键。

---

### 🟡 缺陷 5：`KeyboardBlocker` 中 `IsAllowedKey` 属于死逻辑
- **位置**：[`src/Services/KeyboardBlocker.cs`](file:///c:/Home/Projects/CarroDesk/src/Services/KeyboardBlocker.cs#L84-L127)
- **代码分析**：
  ```csharp
  if (IsAllowedKey(vk) && !winDown && !altDown)
      return CallNextHookEx(_hook, nCode, wParam, lParam);

  if (vk == VK_LWIN || vk == VK_RWIN || vk == VK_APPS) return (IntPtr)1;
  if (winDown) return (IntPtr)1;
  if (altDown) return (IntPtr)1;
  if (vk == VK_ESCAPE && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) return (IntPtr)1;

  return CallNextHookEx(_hook, nCode, wParam, lParam);
  ```
- **缺陷表现**：
  `IsAllowedKey(vk)` 仅包含数字和控制键。如果输入字母 'A'，第 1 个 if 不满足；但由于其非 Win、非 Alt、非 Ctrl+Esc，在最末行依然执行了 `CallNextHookEx` 放行。
  这使得 `IsAllowedKey` 完全沦为无用代码。虽然避免了锁死字母 PIN 用户，但代码表意与实际行为产生背离，容易给后续维护者造成困惑。
- **修复方案**：
  明确安全策略：若支持字母 PIN，则删除无效的 `IsAllowedKey` 方法；若限制纯数字 PIN，应在 `FirstRunWindow` 拦截非数字输入，并在钩子末尾将未允许按键全部拦截。

---

### 🟡 缺陷 6：`HostPinService` 校验后未透明升级哈希与潜在空引用
- **位置**：[`src/Services/HostPinService.cs`](file:///c:/Home/Projects/CarroDesk/src/Services/HostPinService.cs#L25, #L31-L35)
- **代码分析**：
  1. 行 25：`get { return !string.IsNullOrEmpty(_pendingSalt) || !string.IsNullOrEmpty(_pendingHash) || Current().HasPin(); }`
     若 `Current()` 为 `null`，直接抛出 `NullReferenceException`。
  2. `LockController` 在解锁成功时会检查 `_pinService.JustUpgraded`，若命中则将历史单轮 SHA256 哈希透明无感升级为 PBKDF2 并保存。但 `HostPinService`（退出验证与设置窗口验证）通过 `_inner.Verify(pin)` 验证成功后，未检查 `JustUpgraded`，导致非锁屏界面的 PIN 校验无法完成透明升级。

---

### 🟡 缺陷 7：`ConfigEditorWindow` 取消时未回滚内存语言设置
- **位置**：[`src/Views/ConfigEditorWindow.xaml.cs`](file:///c:/Home/Projects/CarroDesk/src/Views/ConfigEditorWindow.xaml.cs#L104-L115, #L385)
- **代码分析**：
  在配置编辑窗口中，用户切换语言下拉框时直接触发 `I18nService.Instance.SetLanguage(lang)`。如果用户点击“关闭”或按 ESC 放弃保存，此时底层配置 `App.Config.Current.Language` 仍为旧语言，而内存中的 `I18nService` 已经变成了新语言，造成界面语言与持久化配置脱节。

---

## 4. 代码质量、性能瓶颈与资源管理

### ⚡ 性能瓶颈 1：`CronHelper.GetNextOccurrence` 穷举 52 万次与内存 GC 冲击
- **位置**：[`src/Models/TaskDefinition.cs`](file:///c:/Home/Projects/CarroDesk/src/Models/TaskDefinition.cs#L413-L425)
- **分析**：
  ```csharp
  int maxMinutes = 60 * 24 * 366; // 525,600 分钟
  for (int i = 0; i < maxMinutes; i++)
  {
      if (IsMatch(cur, expr)) return cur;
      cur = cur.AddMinutes(1);
  }
  ```
  在 `IsMatch` 中，每一次循环都会执行 `expr.Trim().Split(new char[] { ' ', '\t' }, ...)` 以及子字符串 `Split`。在最差情况下（例如一年内未命中的表达式或闰年匹配），单次计算将进行 52 万次循环，并在托管堆上分配超过 **200 万个临时字符串对象**。
  由于 `TaskEditorWindow.CronBox_TextChanged` 在每次按键输入时实时调用该方法，用户在打字过程中极易引发严重的 UI 掉帧卡顿。

### ⚡ 资源泄露 2：`AwakeService` 中 `Process` 内核句柄未释放
- **位置**：[`src/Modules/Awake/Services/AwakeService.cs`](file:///c:/Home/Projects/CarroDesk/src/Modules/Awake/Services/AwakeService.cs#L306-L312)
- **分析**：
  `CheckProcessTriggers()` 每 5 秒轮询一次：
  ```csharp
  var processes = Process.GetProcessesByName(nameOnly);
  if (processes != null && processes.Length > 0)
  {
      matchedProc = ProcessHelper.Normalize(procSetting);
      break;
  }
  ```
  `Process.GetProcessesByName` 返回由操作系统分配的托管句柄包装数组。由于没有使用 `using` 或在 `finally` 中显式遍历调用 `Dispose()`，长期挂机运行会导致系统句柄缓慢泄漏。

### ⚡ 资源泄露 3：`MonitorDdcService` 中 WMI COM 对象释放疏漏
- **位置**：[`src/Modules/MonitorProfile/Services/MonitorDdcService.cs`](file:///c:/Home/Projects/CarroDesk/src/Modules/MonitorProfile/Services/MonitorDdcService.cs#L557-L563)
- **分析**：
  ```csharp
  foreach (ManagementObject obj in results)
  {
      obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)brightness });
      Log($"WMI: 亮度已成功设置为 {brightness}");
      return true; // 未对 obj 进行 Dispose 直接跳出！
  }
  ```
  `ManagementObject` 实现了 `IDisposable` 并持有底层的 WMI/COM 资源。提前 `return` 会跳过隐式释放，导致 `wmiprvse.exe` 服务端资源积压。

---

## 5. Windows 底层与硬件互操作审查

### 5.1 多显示器与混合 DPI 缩放下锁屏窗口坐标错位
- **涉及文件**：[`src/Views/LockWindow.xaml.cs`](file:///c:/Home/Projects/CarroDesk/src/Views/LockWindow.xaml.cs#L62-L67)
- **问题分析**：
  ```csharp
  Left = screen.Bounds.Left;
  Top = screen.Bounds.Top;
  Width = screen.Bounds.Width;
  Height = screen.Bounds.Height;
  ```
  - `System.Windows.Forms.Screen.Bounds` 获取的是**设备物理像素 (Physical Pixels)**；
  - WPF 窗口的 `Left/Top/Width/Height` 单位是**设备无关像素 (DIPs, 1/96 inch)**。
- **实际后果**：
  当用户连接了不同缩放比的显示器时（例如笔记本 14 寸 150% 缩放 + 外接 27 寸 100% 缩放）：
  主屏为 150% 缩放时，WPF 将物理像素（如 1920）当成 DIPs，再次乘以 1.5 渲染为 2880 物理像素，导致锁屏窗口超出屏幕边界；副屏则可能因坐标换算错误出现黑边、未遮满或输入框卡片偏移。
- **更优方案**：
  使用 `VisualTreeHelper` 或 Win32 `GetDpiForMonitor` 取得各屏幕实际 DPI Scale 矩阵，对 WinForms 物理尺寸进行转换：
  ```csharp
  var source = PresentationSource.FromVisual(this);
  double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
  double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
  Left = screen.Bounds.Left / dpiX;
  Top = screen.Bounds.Top / dpiY;
  Width = screen.Bounds.Width / dpiX;
  Height = screen.Bounds.Height / dpiY;
  ```

### 5.2 `SetThreadExecutionState` 线程绑定特性
- **涉及文件**：[`src/Modules/Awake/Services/AwakeService.cs`](file:///c:/Home/Projects/CarroDesk/src/Modules/Awake/Services/AwakeService.cs#L206-L218)
- **审查评价**：
  `SetThreadExecutionState` 的状态与发起调用的操作系统线程强绑定。`AwakeService` 中严谨地使用了 `_dispatcher.CheckAccess()` 并封送回主 UI 线程调用，保证了 API 在同一常驻线程上生效，设计非常规范。

### 5.3 Core Audio WASAPI 默认音频设备与进程静音
- **涉及文件**：[`src/Host/Services/AudioService.cs`](file:///c:/Home/Projects/CarroDesk/src/Host/Services/AudioService.cs#L137-L224)
- **审查建议**：
  `SetProcessMute` 当前只在 `GetDefaultAudioEndpoint` 上枚举 AudioSession。如果某应用（如某些播放器或游戏）在启动时锁定了特定的非默认音频输出端点（例如专门输出至 USB 声卡），切换默认设备后，该应用的声音会遗留在旧端点上。建议支持在所有活动的渲染端点（`eRender` 全部端点）上遍历静音。

---

## 6. 架构升级与更优演进方案

### 方案 A：通用子模块配置动态托管（彻底消除硬编码）
**改造目标**：新增任何模块时，零修改 `ConfigService` 与 `ConfigManager`，且生成的 `config.json` 保持为格式规范的标准 JSON 对象。

```csharp
// 1. AppSettings 中引入 JsonExtensionData
public class AppSettings
{
    public int IdleMinutes { get; set; } = 5;
    public bool AutoStart { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.88;
    public string PinSalt { get; set; } = "";
    public string PinHash { get; set; } = "";
    public bool TasksEnabled { get; set; } = true;
    public bool UnlockOnResume { get; set; } = true;
    public string Language { get; set; } = "auto";
    public List<string> ExcludeProcesses { get; set; } = new List<string>();

    [JsonExtensionData]
    public IDictionary<string, JToken> ModuleSections { get; set; } = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
}

// 2. ConfigManager 实现通用存取
public T GetModuleConfig<T>(string moduleId) where T : class, new()
{
    if (_underlying.Current.ModuleSections.TryGetValue(moduleId, out var token) && token != null)
    {
        try
        {
            return token.ToObject<T>() ?? new T();
        }
        catch { return new T(); }
    }
    return new T();
}

public void SaveModuleConfig<T>(string moduleId, T config) where T : class
{
    _underlying.Current.ModuleSections[moduleId] = JToken.FromObject(config);
    _underlying.Save();
}
```

---

### 方案 B：预编译位掩码高性能 Cron 评估引擎
**改造目标**：将 52 万次字符串分割与词法解析，降低至纳秒级位运算，彻底杜绝 GC 堆分配与界面卡顿。

```csharp
public class CompiledCronExpression
{
    public ulong MinuteMask;   // 0~59 位
    public uint HourMask;      // 0~23 位
    public uint DayOfMonthMask;// 1~31 位
    public ushort MonthMask;   // 1~12 位
    public byte DayOfWeekMask; // 0~6 位 (0=Sunday)

    public static CompiledCronExpression Parse(string expr)
    {
        // 仅在创建时解析一次，生成各个时间维度的位掩码
        ...
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsMatch(DateTime dt)
    {
        return ((MinuteMask & (1UL << dt.Minute)) != 0)
            && ((HourMask & (1U << dt.Hour)) != 0)
            && ((DayOfMonthMask & (1U << dt.Day)) != 0)
            && ((MonthMask & (1U << dt.Month)) != 0)
            && ((DayOfWeekMask & (1 << (int)dt.DayOfWeek)) != 0);
    }
}
```

---

## 7. 整改实施优先级路线图

| 阶段 | 优先级 | 问题项 | 类别 | 预估工作量 | 预期收益 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Phase 1<br>(即刻修复)** | **P0** | 修复 `SystemIdleService` 系统忙碌枚举值定义错位 | Bug 修复 | 0.5h | 恢复闲时锁屏核心业务，解决全屏游戏误判问题 |
| | **P0** | 修复 `FloatingPanelWindow` 递归重建事件泄露 | 内存防漏 | 1h | 彻底消灭悬浮窗事件雪崩与内存泄露 |
| | **P1** | 修复 `AppAutoMuteModule` 白名单前台应用静音逻辑 | 业务逻辑 | 1h | 纠正白名单模式前台出声倒置问题 |
| | **P1** | 修复 `FileWatcherTrigger` 批量文件事件丢弃 | 业务逻辑 | 1h | 支持多文件批量自动化触发 |
| | **P1** | 修复 `AwakeService` 中 `Process` 对象句柄未释放 | 资源清理 | 0.5h | 防止常驻后台句柄持续泄漏 |
| **Phase 2<br>(结构优化)** | **P2** | 重构 `ConfigManager`：基于 `JsonExtensionData` 通用模块分区 | 架构解耦 | 2h | 消除硬编码，实现模块即插即拔与美观 JSON 落盘 |
| | **P2** | 抽离 `CronHelper` 与 `HotkeyHelper` 独立文件 | 代码重构 | 1h | 降低 `TaskDefinition.cs` 复杂度，消除职责混淆 |
| | **P2** | 实现预编译位掩码 Cron 评估引擎 | 性能优化 | 2h | 计算耗时降低 99%，任务编辑打字不再卡顿 |
| **Phase 3<br>(体验打磨)** | **P3** | 优化 `LockWindow` 多显示器混合 DPI 换算 | UI/UX | 2h | 保证复杂多屏环境下锁屏遮罩 100% 贴合 |
| | **P3** | 完善 `HostPinService` 透明哈希升级与空保护 | 安全/细节 | 0.5h | 退出与设置时无感将旧 SHA256 提升为 PBKDF2 |
| | **P3** | 收敛 `App.*` 静态门面，改由模块上下文传递 | 架构解耦 | 3h | 增强模块自治性与单元测试可测性 |
