# CarroDesk 宿主与内置模块解耦设计规范（修订版 v3.0 Draft）

> **版本**：v3.0 Draft（可执行修订版，未定稿）
> **日期**：2026-09-16
> **状态**：Draft，替代 `docs/HOST-PLUGIN-DECOUPLING-SPEC.md` v2.0（降级为草案）与 `docs/MODULAR-HOST-DESIGN.md` 的实施部分
> **目标系统**：Windows 10 / 11，.NET Framework 4.8 / C# 7.3，WPF，Costura.Fody 单文件
> **架构定调**：进程内编译期内置模块化单体（In-Process Modular Monolith）。**不是通用插件化，不是 MAF/ME F，不是进程外插件。**

本文综合 v2.0 方案原文、两份独立架构审查意见重写。v2.0 的问题诊断全部保留，方向（编译期模块 + 静态注册）保留，契约与实施细节按本文执行。

---

## 0. 与旧文档的关系与术语映射

### 0.1 取代声明

* `HOST-PLUGIN-DECOUPLING-SPEC.md` v2.0 自称“评审通过”降级为**设计草案**，一切与本文冲突之处以本文为准。
* `MODULAR-HOST-DESIGN.md` v2.0 的架构分层与防坑指南（COM 虚表、GC 委托、响应式菜单）继续有效，但其中 `IModule/ModuleBase/TrayMenuItem` 定义以本文 §3 为准。

### 0.2 术语：不再使用“插件（Plugin）”，统一使用“内置模块（Module）”

v2.0 最大的方向性矛盾：自称“插件化”，实际 §6.1 是手工 `new` 四个内置类的静态工厂，§6.2 的外部 DLL 只是附带一句。这种“内置模块写成插件”的措辞会导致实现者误向通用插件框架投入精力。

| 旧术语（v2.0，废弃） | 新术语（本文，执行） | 说明 |
|---|---|---|
| `IPlugin / IPluginMetadata / PluginBase<T>` | `IModule`（原地升级，见 §3.1） | 不新建平行体系，避免四个模块改名 churn |
| `TrayMenuNode / TrayMenuNodeType` | `TrayMenuItem`（原地扩展，见 §3.2） | 现有 `src/Core/Models/TrayMenuItem.cs` 已有 INPC + Children + ClickAction + ICommand，禁止另起模型 |
| `PluginManager / BuiltInPluginCatalog` | `ModuleManager`（原地升级）+ 显式注册函数 | 就是 `src/App.xaml.cs:120-143` 的整理版，不引入新类名 |
| `IPluginContext` | `IModuleContext`（薄上下文，见 §3.3） | 只含 ModuleId + Dispatcher + 刷新请求 + 服务定位，不含配置魔法 |
| `ISecurityChallengeService` | `IExitGuard` 多播守卫（见 §3.5） | 单例伪抽象，废弃 |
| 外部 `plugins/*.dll`“模式 B” | **暂不支持，明确砍掉**（见 §2.3） | 无 ALC、无卸载、无隔离，单文件工具不需要 |

---

## 1. 总体判断（两份审查的共识）

1. **诊断属实，且实际比 v2.0 写得更严重**：托盘 XAML 静态区（`src/Views/TrayContextMenu.xaml:156-176,196-204`）+ 后台强引用（`TrayContextMenu.xaml.cs:52,116,318,332`）、`TaskSchedulerModule` 构造索取 `IdleDetector`（`TaskSchedulerModule.cs:26`）、`App.*` 静态门面（`App.xaml.cs:29-39`）、`ConfigManager` 硬编码（`ConfigManager.cs:41-130`）之外，还有三处 v2.0 漏列的耦合：`TaskSchedulerService` 内部直访 `ScreenLock.App.Config`（`TaskSchedulerService.cs:64,93`）、`I18nService.Instance` 静态单例上百处引用、`HotkeyService.Instance` 静态单例（`AudioSwitchModule.cs:60`、`AppAutoMuteModule.cs:86`、`HotkeyTrigger.cs:24`）。
2. **主体方向正确**：编译期内置模块 + 静态注册是 `net48 + Costura 单文件 + <25MB/<200ms` 约束下的最优解，优于 MAF/进程外/动态 DLL。
3. **v2.0 有 1 个方向性矛盾 + 2 个契约级缺陷 + 若干实施盲区**：矛盾是“插件”措辞；缺陷是退出守卫伪抽象与装配顺序缺失；盲区是状态卡自相矛盾、托盘三坑、I18n/配置迁移/Faulted 闭环缺失。本文逐一修正。

---

## 2. 目标分层（保持三层，不引入新层）

```text
Modules（自治业务，禁止互相引用）
  ScreenLock / TaskScheduler / AudioSwitch / AppAutoMute
  只允许 → Core 契约 + Host 基础设施接口
Core（纯抽象，无实现，无 NuGet 第三方依赖；允许依赖 WPF 基类程序集 PresentationCore/WindowsBase）
  IModule / ModuleBase<TConfig> / TrayMenuItem / IModuleContext
  IIdleService / IHotkeyService / IForegroundTracker / IAudioService /
  INotificationService / IExitGuard / IStatusProvider / IConfigManager / ILoggerService
Host（微内核运行时 + 基础设施实现）
  App / ServiceContainer / ModuleManager / DynamicTrayController
  SystemIdleService / HotkeyService / ForegroundTracker / AudioService /
  ConfigManager / DefaultLoggerService / I18n 桥接
```

### 2.1 铁律（Code Review 直接拦）

1. **模块只消费宿主服务，禁止向容器注册服务**。从根上消灭“插件→插件”依赖链（v2.0 允许 `ScreenLockPlugin` 注册 `ISecurityChallengeService` 是错误的，它制造了隐式装配顺序依赖）。
2. **模块之间禁止任何类级引用**（构造参数、`Instance` 静态、`App.XxxMod` 均禁止）。跨模块协作只允许经由 Host 服务或事件。
3. **Host 代码禁止出现业务模块类名与业务概念**（`ScreenLock/IdleMinutes/耳机/扬声器` 等词不得出现在 `Host/` 与托盘宿主区）。
4. **命名空间先行**：`ScreenLock.*` → `CarroDesk.*` 是解耦第一步（当前 `App.xaml.cs:21` 仍是 `namespace ScreenLock`，`TrayContextMenu` 仍是 `ScreenLock.Views`）。不改名，一切“零感知”都是空话。

### 2.2 装配顺序（形式化，v2.0 缺失）

```text
1. Host 基础设施全部就位并注册（Config/I18n/Logger/Audio/Foreground/Hotkey/Idle/Notification）
2. Modules.RegisterModule（仅入列，不执行逻辑，允许按 Order 排序但 Order 不决定服务可用性）
3. ModuleManager.InitializeAll：只许读配置、订阅事件、声明菜单结构；禁止启动 Timer/注册热键/弹窗
4. ModuleManager.StartAll：按注册顺序启动；失败者标记 Faulted，不阻断后者
5. 运行期：事件回调一律经 SafeInvoke（见 §6）
6. 退出：先协商退出守卫（见 §3.5），再 ModuleManager.StopAll 倒序停止，
   最后释放基础设施（Audio/Foreground/Hotkey 在所有模块 Stop 之后释放）
```

`Order` 仅决定**托盘排序与启动顺序**，不决定服务可见性。任何“消费者早于提供者”的设计一律视为设计错误——因为提供者永远是 Host 而非其他模块。

### 2.3 外部 DLL 插件：明确暂不支持

在 `net48` 下 `Assembly.LoadFrom` 有三个无解硬伤，本文明确不做：

* DLL 被进程锁定，覆盖/升级/删除失败；
* 无 `AssemblyLoadContext`，外部程序集不可卸载（只能整 `AppDomain` 卸载，代价巨大）；
* 同一 `AppDomain` 无依赖隔离，版本冲突无解，且有 DLL planting 安全风险。

`BuiltInPluginCatalog` 保留其**思想**（显式编译期注册，零反射、启动快、Costura 安全），但不需要新类——就是 `App` 启动函数里四行显式 `RegisterModule`。`plugins/` 目录发现、`Assembly.LoadFrom`、签名白名单等一律不实现。将来真需要热插拔，另立项评估进程外方案。

> 注：v2.0 称“反射在 Costura 下盲扫落空”是误判。Costura 嵌入的是依赖 DLL，自身程序集 `GetTypes()` 完全可用。静态注册的理由是启动速度与确定性，不是 Costura 免疫。

---

## 3. 契约层（最终版，以此为准）

### 3.1 `IModule`：原地升级，不另起 `IPlugin`

```csharp
namespace CarroDesk.Core
{
    public enum ModuleStatus { Created, Initialized, Running, Stopped, Disabled, Faulted }

    public interface IModule : IDisposable
    {
        string Id { get; }            // "ScreenLock" 等，大小写不敏感唯一
        string Name { get; }          // 本地化显示名（允许每次访问重新 Loc.T，以支持热切换）
        string Description { get; }
        string Version { get; }       // 新增，Semantic Version 字符串，Host 只展示不解析
        int Order { get; }            // 新增，托盘排序 + 启动顺序，越小越靠前
        bool DefaultEnabled { get; }
        bool IsRunning { get; }
        ModuleStatus Status { get; }  // 新增，替代散落的 bool 标记

        void Initialize(IModuleContext context);  // 签名变更：IServiceProvider → IModuleContext
        void Start();
        void Stop();
        void OnConfigReloaded();
        void OnLanguageChanged();                  // 新增，见 §3.7
        IEnumerable<TrayMenuItem> GetTrayMenuItems();
    }
}
```

兼容策略：**C# 7.3/net48 无接口默认实现（DIM），不能字面保留"显式接口默认实现"**。正确做法是 `ModuleBase<TConfig>` 显式实现旧签名 `IModule.Initialize(IServiceProvider)`，在其内部手工构建只读 `IModuleContext`（ModuleId/Dispatcher/服务定位/请求/通知）再转发到新 `Initialize(IModuleContext)`；一次只迁一个模块，保证每步可编译。`ModuleBase<TConfig>` 增加 `Context/Config` 缓存、`Status` 转换、异常边界（见 §6），`Start/Stop` 幂等。

### 3.2 `TrayMenuItem`：原地扩展，不新建 `TrayMenuNode`

在现有 `src/Core/Models/TrayMenuItem.cs` 上加两个成员，其余不动：

```csharp
public int Order { get; set; }                 // 新增，组内排序；不设者按添加顺序
public string ToolTip { get; set; }            // 新增（可选），用于禁用原因等
// 保留：Id/Header/IsChecked/IsEnabled/IsVisible/InputGestureText/
//       IsSeparator/Command/CommandParameter/ClickAction/Children/INPC
```

废弃 `TrayMenuNode/TrayMenuNodeType`。理由：后者是前者的功能子集重命名，还丢了 `Command/CommandParameter`，制造两套模型长期共存。

契约细则（托盘引擎依赖这些保证）：

* `Id` 在**单个模块内唯一**即可；跨模块冲突时 Host 按 `ModuleId + "." + Id` 隔离，日志告警，不抛异常。
* `ClickAction` 为同步回调，**禁止阻塞**（>50ms 操作必须自己 `Task.Run`）。需要异步的模块自行转后台，Host 只保证异常不击穿（见 §6）。
* `IsChecked` 双向写回：Host 做 `TwoWay` 绑定，模块如需感知回写请订阅自己节点的 `PropertyChanged`，Host 不提供额外回调。
* `Header/ToolTip` 每次 `OnLanguageChanged()` 后由模块重设（见 §3.7），Host 不做翻译。
* `IsVisible=false` 映射为 `Collapsed`（不需要 `Hidden`，托盘无占位需求）。
* 图标、自定义模板暂不支持，需要时再扩展 `IconKey`，不为状态卡开特例（状态卡见 §4.4）。

### 3.3 `IModuleContext`：薄上下文（替代 v2.0 的 `IPluginContext`）

v2.0 的 `GetConfig<T>/SaveConfig<T>/ShowNotification` 把配置与通知魔法藏进上下文，导致 owner 绑定、缓存一致性、并发写全都不清不楚。本文只保留三样：

```csharp
namespace CarroDesk.Core
{
    public interface IModuleContext
    {
        string ModuleId { get; }
        System.Windows.Threading.Dispatcher Dispatcher { get; }
        T GetService<T>() where T : class;   // 只读消费，禁止注册
        void RequestTrayRefresh();            // 请求重刷本模块菜单（Host 合并防抖，见 §4.3）
        void ShowNotification(string message, string title = "CarroDesk");
    }
}
```

* 配置读写**不走 Context**，继续走 `IConfigManager.GetModuleConfig<T>(moduleId)/SaveModuleConfig(moduleId)`，`ModuleBase` 内部传 `Id`。消除“隐式 owner”歧义。
* `RequestTrayRefresh()` 语义：声明式请求，Host 在 UI 线程合并执行（150ms 节流 + 菜单打开时延迟到 `Closed` 后，见 §4.3）。模块可任意线程调用，可高频调用。
* `ShowNotification` 由 Host 统一节流，模块禁止直接操作 `TaskbarIcon`。

### 3.4 能力服务：先抽接口，再迁模块

现状 `AudioService/ForegroundTracker` 是具体类、`HotkeyService` 是内部静态单例、`IdleDetector` 是业务耦合类。迁移顺序必须是**先抽接口并注册，再改模块消费**，否则 `GetService<I...>` 永远返回 null。

```csharp
public interface IIdleService
{
    TimeSpan RawIdle { get; }              // 取代 int 秒，保留毫秒精度
    bool IsSystemBusyCached { get; }       // 缓存值，不直接 P/Invoke；变化走事件
    event Action<TimeSpan> IdleTick;       // 线程池广播，订阅者自行调度回 UI
    event Action UserActiveDetected;
    event Action<bool> SystemBusyChanged;
}
public interface IHotkeyService : IDisposable
{
    // 按 moduleId 分组，模块 Stop/故障时 Host 可一键回收；冲突返回 error 字符串
    int Register(string moduleId, string hotkeyStr, Action callback, out string error);
    void Unregister(string moduleId, int id);
    void UnregisterAll(string moduleId);
}
public interface IForegroundTracker : IDisposable
{
    event Action<IntPtr, string> ForegroundChanged;
    string CurrentProcessName { get; }
    IntPtr CurrentWindowHandle { get; }
}
public interface IAudioService : IDisposable
{
    IReadOnlyList<AudioDeviceItem> GetPlaybackDevices();
    AudioDeviceItem GetDefaultPlaybackDevice();
    bool SetDefaultPlaybackDevice(string deviceId);
    bool SetProcessMute(string processName, bool mute);
    void UnmuteProcesses(IEnumerable<string> processNames);
    event Action DevicesChanged;  // 新增：WM_DEVICECHANGE 桥接，音频菜单刷新用
}
public interface INotificationService
{
    void Show(string message, string title = "CarroDesk");
}
```

`IIdleService` 实现要求（单例、Host 持有唯一 Timer 扇出，插件禁止自建轮询）：

* 唯一 1s Timer 跑在**线程池**（不得用 `DispatcherTimer` 卡 UI），`IdleTick` 广播物理闲时 `TimeSpan`。
* `WarnBefore/Threshold/_effectiveMs/ShouldSuspend` 等业务状态机**移入 `ScreenLock` 模块内部复刻**（v2.0 漏交代）：提前 30s 警告且只触发一次、输入打断复位、暂停时段跳过、白名单跳过，全部是业务逻辑，不得留在服务里。
* `TaskScheduler.IdleTrigger` 的“共享阈值订阅 + 私有 Detector 回退”（`IdleTrigger.cs:28-45`）废弃，改为各自按 `afterMinutes` 独立判定服务广播的 `RawIdle`，互不干扰。
* `IsSystemBusy`（`SHQueryUserNotificationState`）在服务内缓存轮询（如 2s 一次），变化才发事件，禁止每次访问 P/Invoke。

### 3.5 退出守卫：`IExitGuard` 多播替代 `ISecurityChallengeService`（必改）

v2.0 的 `ISecurityChallengeService` 是伪抽象：接口语言抽象、语义绑定 ScreenLock、容器单例只能容纳一个守卫、隐含“插件先注册服务宿主后索取”的装配时序。替换为：

```csharp
public interface IExitGuard
{
    // true = 请求阻止退出并接管挑战流程；Host 对每个守卫设超时 3s（已定），超时按 false 放行
    bool RequestBlockExit();
}
```

宿主退出协商伪代码：

```csharp
foreach (var guard in _services.GetServices<IExitGuard>())  // 需容器支持多注册，见 §3.8
    using (var cts = new CancellationTokenSource(3000))
        if (SafeInvoke(guard, () => guard.RequestBlockExit(), cts))
        { ShowChallengeUiOwnedByHost(); return; }  // 挑战 UI 归 Host，插件只做决策
ExitApp();
```

配套规则：

* 挑战 UI（`VerifyPinWindow`）由 Host 持有，PIN 校验能力（`PinService`）下沉到 Host/Core，**缺模块时按“有 PIN 则验”兜底**。v2.0“无插件直接放行”是安全回退，禁止。
* 将来第二个守卫可直接共存（OR 语义），无顺序要求。

### 3.6 配置：兼容迁移 + 原子写（v2.0 只有口号）

文件布局不变（`config.json` + `tasks.json`），格式向前兼容：

```jsonc
{
  // 老扁平字段继续可读（IdleMinutes/PinHash/TasksEnabled/Language/...）
  "Modules": {
    "ScreenLock":  { "IdleMinutes": 5, /* ... */ },
    "AudioSwitch": { "Enabled": true, "Hotkey": "Ctrl+`", /* ... */ },
    "AppAutoMute": { "Enabled": true, /* ... */ }
  }
}
```

* 首次启动检测到老扁平字段自动迁移进 `Modules.*` 并写回（`MODULAR-HOST-DESIGN.md` §7 的承诺，v2.0 定稿版反而丢了，此处补回）。
* `AudioSwitch/AppAutoMute` 当前是内存静态（`ConfigManager.cs:89-90` 从未落盘），本次必须落盘到 `Modules` 节，否则迁移即功能回退。
* 写文件一律 temp+replace 原子写 + 进程内读写锁；损坏时备份 `.corrupt-时间戳` 并用默认值启动，不得抛穿。
* `SaveModuleConfig` 高频调用方（如 `ToggleEnabled`）由 `ConfigManager` 内部 300ms 合并写，模块无感知。**合并写必须定义刷盘口径**：模块可感知语义（如 `SetGlobalEnabled`/`ToggleEnabled` 后立即读回一致、Reload 后立刻生效）仍需保证；进程退出前在 `OnExit`/`Dispose` 通道 `Flush` 最后一批，严禁退出即丢最后一次写。

### 3.7 I18n：语言变更广播（v2.0 最大盲区）

`I18nService.Instance` 静态 + `{loc:Loc}` 遍布 XAML，模块菜单 `Header` 在构建时已翻译为字符串，语言切换后必然过期。规定：

* Host 订阅 `I18nService.LanguageChanged` 后依次调用各模块 `OnLanguageChanged()`，模块重设自己所有存活节点的 `Header/ToolTip`（XAML 侧 `{loc:Loc}` 自动经既有 `INPC` 刷新，无需改动）。
* 托盘引擎在语言切换后对全区做一次 `RequestTrayRefresh`（走正常防抖通道）。
* 清理清单必须包含该事件接线，否则中文切英文后插件菜单仍是中文。

### 3.8 `ServiceContainer`：补齐或换掉

现状只支持单例单注册（`ServiceContainer.cs:11`），支撑不了 `IExitGuard` 多播、`GetServices<T>`、`TryGet`、释放。**决策：采用方案 A（手写补齐），本期不引入 MEDI。**

兼容性结论（已核实，备查）：`Microsoft.Extensions.DependencyInjection` 最新 10.x 仍发布 `net462` 目标，`net48` 为计算兼容，技术上可用，C# 7.3 消费其基础 API 也无碍。但对本项目引入它属于增重而非减负：

* 体积：MEDI 本体 + Abstractions + `Microsoft.Bcl.AsyncInterfaces` + `System.Threading.Tasks.Extensions` 等传递包，Costura 嵌入后预计增加约 200–300KB（当前 Release 约 812KB，增幅约 25–35%），违背轻量目标；
* 配置：net48 需补 `AutoGenerateBindingRedirects` 与绑定重定向排障，多出一条维护链路；
* 收益：本项目服务约 10 个、无 Scope/泛型校验/源生成需求，缺的只是多注册 + `TryGet` + `Dispose`，约 60 行即可补齐，MEDI 的标准性收益覆盖不了上述成本。

执行方案 A（约 60 行）：`AddSingleton` 支持同接口多注册 + `GetServices<T>/TryGetService/Dispose`，`IExitGuard` 协商走 `GetServices`。切换条件：服务数超过约 20 个，或出现 Scope/选项校验/源生成需求时，再评估一次性切换到 MEDI 8.x LTS。

---

## 4. 托盘引擎（修正版双锚点）

### 4.1 布局：双锚点保留，加空态折叠

```xml
<ContextMenu x:Class="CarroDesk.Views.TrayContextMenu" ...>
  <MenuItem .../><!-- 宿主标题项（仅应用名，状态卡已砍，见 §4.4） -->
  <Separator x:Name="PluginSlotAnchorTop"/>
  <!-- 运行时在此插入各模块 MenuItem / Separator -->
  <Separator x:Name="PluginSlotAnchorBottom"/>
  <!-- 宿主通用区：开机自启/配置目录/语言/退出（仅限真正宿主项） -->
</ContextMenu>
```

* 任一锚点区间为空时折叠相邻 `Separator`，禁止出现双分隔线空隙。
* 插入算法禁止 `IndexOf(anchor)+1` 脆弱定位：Host 维护 `Dictionary<IModule, List<object>> _inserted` 登记本模块插入的元素，卸载/重建按登记表精确移除。
* 现状 `TrayContextMenu.xaml:156-176` 的“立即锁屏/空闲档位/暂停计时”**是 ScreenLock 业务**，必须整体并入 `ScreenLock` 模块贡献；`196-204` 音频区并入音频模块。实施前必须先输出**迁移后菜单对照图**（宿主区 vs 各模块区逐项映射）并落盘为临时产物（`temp/tray-menu-mapping-对照图.md`，含逐项 Id/来源/目标归属），无对照图不开工——这是 v2.0 漏掉导致 Step 4 必漏迁的坑。注意：上述 XAML 行号引用仅为说明基线，实际已随音频子菜单等多次改动漂移，Step 4 以对照图而非行号为准。

### 4.2 渲染：属性绑定 + 结构重建两档（删去“增量”承诺）

* **属性级**：`Header/IsChecked/IsEnabled/IsVisible/InputGestureText/ToolTip` 走 v2.0 §4.3 的 `SetBinding`，原地更新，不重建。
* **结构级**（增删模块、增删子项、`Children` 变化、语言切换）：**整区间重建**，打开期间（`IsOpen==true`）挂 `Closed` 后执行。v2.0 的“安全增量更新”只有整区间重建一条路，把“增量”二字从承诺中拿掉，诚实比漂亮重要。
* `CreateMenuItemFromNode` 必须是一次性快照 + 订阅 `Children.CollectionChanged → RequestTrayRefresh`，禁止“递归一次就完事”（v2.0 代码即此错误）。

### 4.3 线程与防抖（写死）

* `RequestTrayRefresh()` 任意线程可调；Host 内部 `Dispatcher.BeginInvoke` + 150ms 合并节流；`IsOpen` 时延迟到 `Closed`。
* 模块节点属性变更（`Header=...`）若在后台线程触发，由模块自己 `Dispatcher.Invoke` 回 UI 线程（`Context.Dispatcher`），Host 对非 UI 线程的 `PropertyChanged` 直接记错并忽略，不替模块封送（避免死锁归属不清）。
* 音频设备变化（`DevicesChanged`）、任务列表变化一律走 `RequestTrayRefresh`，禁止模块直接操作 `MenuItem`。

### 4.4 状态卡：砍掉（已决策，不做插件化）

现状顶部状态卡四个状态（已锁定/已暂停/已禁用/运行中，`TrayContextMenu.xaml.cs:51-83`）全是 ScreenLock 语义，放在宿主区违反“零感知”。**决策：删除状态卡，托盘只留菜单。**

* 锁屏的“已暂停/已禁用”等状态改由其自有菜单项的 `IsChecked/Header` 表达（如“暂停计时effectively”菜单项勾选态），不再占用宿主区。
* `SetStatus/RefreshStatus/UpdateTrayText` 中与状态卡相关的 UI 逻辑随卡片一并删除；`ToolTipText` 保留最简应用名或由各模块菜单自述，不再做四态聚合。
* 禁止过渡形态：卡片留宿主、语义归锁屏。

### 4.5 样式与内存

* 动态 `MenuItem` 必须继承 `ContextMenu.Resources` 隐式样式与 `SharedSizeScope` 对齐，上线前实机验证圆角/阴影/勾选列/手势列，`OverridesDefaultStyle=True` 下模板切换时序要走查。
* 移除节点时 `BindingOperations.ClearAllBindings` + 摘 `Click` 闭包，按 `§4.1` 登记表精确释放。`ClickAction` 异常只记日志 + 气泡提示一次，禁止静默吞掉。

---

## 5. 存量迁移清单（进入 Step 4 前逐项打勾，v2.0 遗漏项已补入）

| # | 清理项 | 现状位置 | 目标 |
|---|---|---|---|
| M1 | `TaskSchedulerModule` 构造索取 `Idle` | `TaskSchedulerModule.cs:26` | 改消费 `IIdleService` 广播，删除构造参数 |
| M2 | `TaskSchedulerService` 内部直访 `App.Config` | `TaskSchedulerService.cs:64,93` | 经 `IConfigManager` 或构造注入回调，v2.0 §5.1 漏列 |
| M3 | `TrayContextMenu` 强引用四模块 | `TrayContextMenu.xaml.cs:52,116,318,332` + `TrayContextMenu.xaml:156-176,196-204` | 业务项全部并入模块 `GetTrayMenuItems`，宿主只留通用区 + 对照图 |
| M4 | `App.*` 静态门面 | `App.xaml.cs:29-39`（`Controller/Idle/TaskScheduler/AudioSwitchMod`） | 保留 `Services/Modules` 两个，其余删除；`LockWindow/ConfigEditor/TaskEditor` 改构造注入接口；`App` 内残留业务方法（`PauseFor/SetIdleMinutes/ResumeIdle`）并入 ScreenLock，`UpdateTrayText` 四态聚合改宿主经 `IStatusProvider` 读取，见 M13 |
| M5 | `ConfigManager` 硬编码 | `ConfigManager.cs:41-130` + 内存静态 `89-90` | §3.6 分区 + 落盘 + 原子写 + 无损迁移 |
| M6 | `HotkeyService.Instance` 静态 | `AudioSwitchModule.cs:60`、`AppAutoMuteModule.cs:86`、`HotkeyTrigger.cs:24` | 迁 `IHotkeyService(moduleId)`，`Stop/UnregisterAll` 回收 |
| M7 | `I18nService.Instance` + `{loc:Loc}` | 全仓上百处 | §3.7 广播 + `OnLanguageChanged`，XAML 侧不动 |
| M8 | `LockWindow` 直访 `App.Config/Controller` | `LockWindow.xaml.cs:72,150,209,250` | 构造注入 `ILockService + 只读配置视图`，禁止传“快照”了事（快照无 `TryUnlock` 能力） |
| M9 | `ConfigEditorWindow` 越权直驱模块 | `ConfigEditorWindow.xaml.cs:339-392` | 只调 `IConfigManager.Save` + 发重载通知；`TaskEditor` 依赖的 `ITaskSchedulerService` 若不存在则先定义再迁 |
| M10 | `IdleDetector` 业务状态机 | `IdleDetector.cs:56-60,107-147` + `IdleTrigger.cs:28-45` | 服务纯化（§3.4），`WarnBefore/暂停/白名单` 复刻进 ScreenLock，私有 Detector 分支删除 |
| M11 | 命名空间 | `App.xaml.cs:21` 等 | `ScreenLock.*` → `CarroDesk.*`，`RootNamespace/AssemblyName` 同步 |
| M12 | `VerifyPinWindow/FirstRunWindow` | `App.xaml.cs:87-103,303-311` | PIN 能力下沉 Host，见 §3.5 |
| M13 | `App` 残留业务方法（M4 补充） | `App.xaml.cs`：`PauseFor/SetIdleMinutes/ResumeIdle`、`UpdateTrayText` 四态聚合 ToolTip、托盘 `ShowBalloon` 语义 | `PauseFor/SetIdleMinutes/ResumeIdle` 并入 ScreenLock（经 `IPauseService`/`IIdleController` 或模块方法暴露），宿主不再含 ScreenLock 业务概念；`UpdateTrayText` 聚合态改由宿主只读 `IStatusProvider` 渲染；`ShowBalloon` 下沉为 `INotificationService`，宿主与模块统一经服务发送 |

`LockWindow` 特别说明：v2.0 §5 “传快照”不可行，锁屏需要活的 `TryUnlock/GetBlockRemaining`。正确是抽 `ILockService` 接口传活引用 + 配置传只读视图。

---

## 6. 故障隔离（闭环版）

1. ** per-模块状态机**：`Created → Initialized → Running → Stopped / Disabled / Faulted`。`Initialize` 失败者直接 `Faulted` 并移出启动列，禁止再 `Start`（现状 `ModuleManager.cs:44-54` try-catch 后仍会 Start，必须改）。
2. **统一 `SafeInvoke(moduleId, action, timeout?)`** 包裹一切回调：生命周期、托盘点击、`IdleTick`/`ForegroundChanged`/热键/Timer。`ClickAction` 失败记日志 + 气泡提示，不静默。
3. **故障卸载**：托盘引擎按 §4.1 登记表卸载故障模块全部节点；`Stop` 倒序；基础设施最后释放。
4. **全局兜底区分致命与可恢复**：`DispatcherUnhandledException` 禁止一律 `Handled=true`。可恢复记日志 + 标记 Faulted；`OutOfMemory/StackOverflow/AccessViolation` 落盘后退出。静默吞异常与无感消失都禁止。
5. **禁止给钩子加终结器**：`ForegroundTracker`（已正确强引用委托，`ForegroundTracker.cs:29`）、`KeyboardBlocker` 保持显式 `Dispose` + 逆序释放。终结器线程解钩不可靠，v2.0 Layer 3 写错了。

---

## 7. 实施路线图（绞杀者，每步可编译可运行）

```text
S0 冻结：本文定稿前 v2.0 不得开工；输出 §4.1 菜单对照图 + M1-M12 清单认领。
S1 底座：命名空间改名（M11）+ IModule/ModuleBase/TrayMenuItem 原地升级（§3.1/3.2）
         + IModuleContext + ServiceContainer 补齐（§3.8）+ ModuleStatus/SafeInvoke（§6）。
         验证：双模式编译零警告，行为零变化。
S2 能力接口：IIdleService/IHotkeyService/IForegroundTracker/IAudioService/
         INotificationService 抽取并注册，老静态/HotkeyService.Instance 并存为转发。
         验证：单 Timer 扇出，音频/焦点行为一致。
S3 逐个迁模块（顺序：AudioSwitch → AppAutoMute → TaskScheduler → ScreenLock）：
         每个模块：构造去参 → Initialize(Context) → GetTrayMenuItems → OnLanguageChanged。
         每迁完一个删一处 Tray/App 强引用（M3/M4/M6/M10）。
         验证：托盘对照图逐项走查。
S4 配置与安全：§3.6 分区迁移 + §3.5 IExitGuard（超时 3s）+ §4.4 状态卡删除 + M5/M8/M9/M12。
         验证：老 config.json 无损迁移，缺模块退出仍验 PIN。
S5 收尾：删老 Initialize(IServiceProvider) 转发、删 TrayMenuNode 残留（如有）、
         新建测试工程 `tests/CarroDesk.Tests`（net48 + MSTest/NUnit，`dotnet test` 可跑，不含 UI），
         补契约单测（生命周期/守卫协商 + 超时放行/托盘排序与空锚点/老配置迁移/Idle 广播扇出/合并写刷盘），全量实机回归。
```

禁止大爆炸：任何一次提交同时改 Core 签名 + 四个模块 + 托盘 XAML 的直接打回。

---

## 8. 验证标准（缺一不可）

* `dotnet build -c Debug/Release` 零警告零错误（net48 + Costura）。
* 不带 UI 的单测（落地于独立测试工程 `tests/CarroDesk.Tests`，`dotnet test` 可执行）：状态机转换、守卫 OR 协商 + 超时放行、托盘排序与空锚点折叠、老配置迁移、Idle 广播扇出、合并写刷盘。只写“编译通过 + 实机测试”不算验证通过。
* 实机回归：闲时锁屏/Win+L 联动、`UnlockOnResume`、音频插拔切换、前后台静音防抖与退出全量恢复、中英文热切换、便携/漫游双模式。

---

## 9. 非目标（本期不做）

外部 DLL 插件、MAF、进程外插件、托盘图标自定义、菜单图标体系、插件市场/签名分发。任何新增非目标需求另立项。
