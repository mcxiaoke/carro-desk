# 桌面工具箱宿主架构与模块化重构设计方案 (DeskKit / TrayTools)

> **版本**：v2.0 (工业级标准演进版)  
> **更新时间**：2026-09-16  
> **适用平台**：Windows 10 / 11 (x86 / x64)  
> **目标技术栈**：.NET Framework 4.8 + WPF (C# 7.3+) / 单文件便携打包 (Costura.Fody)  
> **架构模式**：内置模块化单体架构 (In-Process Modular Monolith)

---

## 修订记录 (Revision History)

| 版本 | 日期 | 修订人 | 修订摘要 |
| :--- | :--- | :--- | :--- |
| **v1.0** | 2026-09-16 | System | 初始草案：提出微内核 Host + 插件化解耦思路，规划锁屏、任务调度、音频设备切换、应用自动静音四大功能，初步提出 `IPlugin` 与 `IPluginHost` 接口。 |
| **v2.0** | 2026-09-16 | Antigravity | **核心架构工业级重塑**：<br>1. **架构定调澄清**：确立“内置模块化单体（Modular Monolith）”模式，规避 .NET 4.8 下动态 DLL 插件的依赖地狱与部署复杂度，继续保持 Costura.Fody 单文件便携优势。<br>2. **消除 God Object 接口**：废弃庞大的 `IPluginHost`，改用细粒度基础设施服务注入（`IHotkeyService`、`IForegroundTracker`、`IAudioService` 等），引入 `ModuleBase<TConfig>` 抽象基类统一生命周期与异常保护。<br>3. **Windows 底层 COM 兼容性防护**：针对 `IPolicyConfig` 非公开 COM 接口在 Win10/Win11 各版本虚函数表（vtable）变动隐患，引入多版本自适应虚表包装与 SEH 崩溃隔离。<br>4. **前台焦点追踪防坑设计**：废除 200ms 轮询，采用 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 事件驱动；明确 GC 委托常驻保护方案，并收敛为统一的 `IForegroundTracker` 基础设施供全应用共享。<br>5. **托盘菜单响应式升级**：`TrayMenuItem` 引入 `INotifyPropertyChanged`，实现属性单点更新，彻底消除整树重建导致的菜单闪退与折叠打断。<br>6. **明确全链路异常安全网**：建立进程级三层未捕获异常防线与 COM/钩子资源强制回收规范。 |

---

## 1. 现状剖析与演进目标

### 1.1 现状问题
1. **命名与职责严重错位**：项目名为 `ScreenLock`（锁屏工具），但在演进中已集成了强大的计划任务系统（`TaskSchedulerService`，包含 Cron、周期定时、文件监听、进程排除等）。若直接继续植入“音频设备切换”和“前后台应用自动静音”，宿主将彻底沦为严重耦合的“大杂烩”，维护成本急剧上升。
2. **缺乏统一模块化规范**：各功能各自定义配置、生命周期与热键，缺乏清晰边界，新加功能极易牵一发动全身。
3. **外部脚本痛点**：当前两款 AHK 脚本（`SwitchAudio.ahk` 与 `MuteApps.ahk`）依赖外部 `nircmd.exe`，每次操作频繁创建/销毁外部进程，系统开销大，且在托盘中常驻多个分散进程，缺乏统一管控。

### 1.2 演进目标
将项目由单一的“锁屏工具”转型为**轻量级模块化桌面生产力工具箱宿主（Host）**（项目建议更名为 **`DeskKit`** 或 **`TrayTools`**）：
- **内置模块化单体（In-Process Modular Monolith）**：
  - 核心模块在编译期由一个工程组织，在逻辑上严格物理与接口隔离。
  - **不搞复杂的动态加载外部 DLL**（规避 .NET Framework 4.8 缺少现代 ALC 导致的 AppDomain 跨域性能损耗与版本地狱）。
  - 各模块以平等、自治的独立组件挂载运行。
- **职责彻底解耦**：
  - 原有的“闲时伪锁屏”、“自动化任务调度”以及新规划的“音频切换”、“应用自动静音”，全部封装为互不依赖的**业务模块（Modules）**。
  - 宿主仅负责基础底座能力（系统托盘、全局热键、配置管理、开机自启、通知反馈、焦点感知、生命周期调度）。
- **保留核心优势**：
  - 继续保持 **单 EXE 独立免安装运行**（基于 Costura.Fody 嵌入所有托管依赖）。
  - 超低内存占用（常规常驻 < 25MB）、极速冷启动（< 200ms）。
  - 完美兼容便携模式（`portable.ini`）与漫游模式（`%AppData%`）。

---

## 2. 总体架构设计

```mermaid
graph TD
    subgraph OS [Windows 操作系统底层]
        WinTray[系统托盘 NotifyIcon]
        CoreAudio[Core Audio WASAPI / MMDevice]
        PolicyConfig[IPolicyConfig 私有 COM 接口]
        WinEventHook[WinEventHook 焦点感知]
        WinHotkey[RegisterHotKey API]
    end

    subgraph HostCore [宿主核心 Host (DeskKit)]
        AppEntry[App 入口 / 单实例 Mutex / 全局异常网]
        ServiceContainer[轻量服务容器 ServiceProvider]
        ModuleRegistry[模块生命周期总线 ModuleRegistry]
        TrayDispatcher[响应式托盘菜单聚合器 TrayManager]
    end

    subgraph InfraServices [基础设施服务层 Services]
        IHotkeyService[IHotkeyService<br>热键统一路由与防冲突]
        IConfigManager[IConfigManager<br>强类型分区配置持久化]
        IForegroundTracker[IForegroundTracker<br>单例安全焦点监听服务]
        IAudioService[IAudioService<br>多版本自适应 WASAPI 音频引擎]
        INotificationService[INotificationService<br>气泡/Toast/提示音服务]
        IAutoStartManager[IAutoStartManager<br>注册表开机自启同步]
    end

    subgraph Contracts [核心抽象契约 Core]
        IModule[IModule 模块契约]
        ModuleBase[ModuleBase 模块抽象基类]
        TrayMenuItem[TrayMenuItem 响应式菜单项]
    end

    subgraph Modules [独立业务模块层 Modules]
        M1[ScreenLockModule<br>闲时锁屏 / PIN 安全保护]
        M2[TaskSchedulerModule<br>计划任务 / 自动化调度]
        M3[AudioSwitchModule<br>耳机扬声器快捷切换]
        M4[AppAutoMuteModule<br>前后台应用智能静音]
    end

    WinTray <--> TrayDispatcher
    WinHotkey <--> IHotkeyService
    CoreAudio <--> IAudioService
    PolicyConfig <--> IAudioService
    WinEventHook <--> IForegroundTracker

    HostCore --> InfraServices
    InfraServices --> Contracts
    Contracts --> Modules
    Modules -.-> InfraServices
```

### 2.1 架构分层原则

| 层级 | 模块/命名空间 | 职责说明 |
| :--- | :--- | :--- |
| **宿主核心 (Host Core)** | `DeskKit.Host` | 1. 唯一单实例互斥体 (`Mutex`) 与防重入<br>2. 顶层三层未捕获异常防护网（防止闪退）<br>3. 初始化服务容器与模块生命周期协调<br>4. 统一主托盘图标交互与 WPF 菜单渲染 |
| **基础设施层 (Infra Services)** | `DeskKit.Host.Services` | 封装 Windows 原生 API 与公共能力，以独立服务接口暴露（如 `IHotkeyService`, `IForegroundTracker`, `IAudioService`），提供线程安全与资源自动回收 |
| **契约层 (Core Contracts)** | `DeskKit.Core` | 宿主与模块之间的纯抽象契约与基础模型（`IModule`, `ModuleBase<TConfig>`, `TrayMenuItem`），无具体业务实现，保持极低耦合 |
| **业务模块层 (Modules)** | `DeskKit.Modules.*` | 具体功能实现。每个模块为一个自包含子系统，包含专属配置 DTO、内部逻辑与 UI；仅通过依赖注入按需获取基础设施，模块间绝对禁止直接强引用 |

---

## 3. 核心契约与抽象模型

### 3.1 模块契约 (`IModule`)

统一标准化模块生命周期与托盘挂载契约：

```csharp
namespace DeskKit.Core
{
    public interface IModule : IDisposable
    {
        // 模块元数据
        string Id { get; }              // 唯一标识 (如 "screen_lock", "audio_switch")
        string Name { get; }            // 本地化显示名
        string Description { get; }     // 模块描述
        bool DefaultEnabled { get; }    // 首次运行默认状态
        bool IsRunning { get; }         // 当前是否处于活跃运行态

        // 生命周期
        void Initialize(IServiceProvider services);
        void Start();                   // 启动业务（挂载监听、注册热键）
        void Stop();                    // 停用业务（恢复系统状态、注销资源）
        void OnConfigReloaded();        // 配置文件重载通知

        // 托盘动态接入点
        IEnumerable<TrayMenuItem> GetTrayMenuItems();
    }
}
```

### 3.2 模块抽象基类 (`ModuleBase<TConfig>`)

为了杜绝样板代码，提升开发与维护效率，并建立统一的**异常边界守卫**，提供泛型模块抽象基类：

```csharp
namespace DeskKit.Core
{
    public abstract class ModuleBase<TConfig> : IModule where TConfig : class, new()
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual bool DefaultEnabled => true;
        public bool IsRunning { get; private set; }

        protected IServiceProvider Services { get; private set; }
        protected TConfig Config { get; private set; }

        public virtual void Initialize(IServiceProvider services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
            var configMgr = services.GetService<IConfigManager>();
            Config = configMgr?.GetModuleConfig<TConfig>(Id) ?? new TConfig();
        }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                OnStart();
                IsRunning = true;
            }
            catch (Exception ex)
            {
                Services.GetService<ILoggerService>()?.LogError(Id, "模块启动异常", ex);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try
            {
                OnStop();
            }
            catch (Exception ex)
            {
                Services.GetService<ILoggerService>()?.LogError(Id, "模块停用异常", ex);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public virtual void OnConfigReloaded()
        {
            var configMgr = Services.GetService<IConfigManager>();
            Config = configMgr?.GetModuleConfig<TConfig>(Id) ?? new TConfig();
        }

        public virtual IEnumerable<TrayMenuItem> GetTrayMenuItems() => Enumerable.Empty<TrayMenuItem>();

        protected abstract void OnStart();
        protected abstract void OnStop();

        public virtual void Dispose()
        {
            Stop();
        }
    }
}
```

### 3.3 响应式托盘菜单项模型 (`TrayMenuItem`)

原方案调用 `host.RefreshTrayMenu()` 会造成整棵菜单树推倒重建，导致用户正在展开菜单时意外收起或闪烁。
v2.0 升级为基于 WPF 数据绑定的**响应式菜单模型**，属性变更通过 `INotifyPropertyChanged` 单点刷新：

```csharp
namespace DeskKit.Core
{
    public class TrayMenuItem : INotifyPropertyChanged
    {
        private string _header;
        private bool _isChecked;
        private bool _isEnabled = true;
        private bool _isVisible = true;

        public string Id { get; set; }
        public bool IsSeparator { get; set; }

        public string Header
        {
            get => _header;
            set { if (_header != value) { _header = value; OnPropertyChanged(); } }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        public string InputGestureText { get; set; }
        public ICommand Command { get; set; }
        public ObservableCollection<TrayMenuItem> Children { get; } = new ObservableCollection<TrayMenuItem>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
```

---

## 4. 基础设施服务体系 (Infrastructure Services)

废弃原 `IPluginHost`“上帝接口”，按单一职责拆分为 6 大核心基础设施服务，由宿主服务容器统一管理并注入：

### 4.1 全局热键路由服务 (`IHotkeyService`)
- 集中注册 `RegisterHotKey`，统一在一个常驻的 Win32 消息循环窗口（HWND）中派发 `WM_HOTKEY`。
- 支持快捷键字符串智能解析（如 `"Ctrl+Alt+S"`, `"Win+F1"`）。
- 支持以 `moduleId` 为作用域分组管理，当模块 `Stop()` 或异常时自动回收，防止热键泄露与冲突。

```csharp
public interface IHotkeyService : IDisposable
{
    bool Register(string moduleId, string hotkeyStr, Action callback, out string error);
    void Unregister(string moduleId, string hotkeyStr);
    void UnregisterAll(string moduleId);
}
```

### 4.2 全局焦点感知服务 (`IForegroundTracker`)
- 替代低效的定时器轮询，通过 Win32 `SetWinEventHook` 监听 `EVENT_SYSTEM_FOREGROUND`。
- **GC 安全守护**：回调委托由常驻单例持有，彻底杜绝回调函数指针被 GC 回收引发的闪退。
- 提供全应用共享事件，模块只需订阅，无需重复注册系统级钩子。

```csharp
public interface IForegroundTracker : IDisposable
{
    event Action<IntPtr, string> ForegroundChanged; // hWnd, processName
    string CurrentProcessName { get; }
    IntPtr CurrentWindowHandle { get; }
}
```

### 4.3 核心音频控制服务 (`IAudioService`)
- 统一封装 Windows Core Audio WASAPI 与 MMDevice API。
- 负责音频设备枚举、默认设备切换、单应用 AudioSession 静音与音量调节。
- 内置针对 `IPolicyConfig` 的多版本兼容适配与 SEH 崩溃防御层。

```csharp
public interface IAudioService : IDisposable
{
    IReadOnlyList<AudioDeviceModel> GetPlaybackDevices();
    AudioDeviceModel GetDefaultPlaybackDevice();
    bool SetDefaultPlaybackDevice(string deviceId);
    
    // 按进程名精准静音/取消静音
    bool SetProcessMute(string processName, bool mute);
    void UnmuteProcesses(IEnumerable<string> processNames);
}
```

### 4.4 强类型分区配置服务 (`IConfigManager`)
- 统一读写根配置文件（漫游模式 `%AppData%\DeskKit\config.json` 或便携模式 `{exe}\app_data\config.json`）。
- 模块专属配置按模块 ID 隔离反序列化，相互独立。
- 支持配置文件热加载（FileWatcher + 手动重载）。

```csharp
public interface IConfigManager
{
    T GetModuleConfig<T>(string moduleId) where T : class, new();
    void SaveModuleConfig<T>(string moduleId, T config) where T : class;
    void Reload();
    event Action ConfigReloaded;
}
```

### 4.5 统一通知与音效服务 (`INotificationService`)
- 统一接管托盘气泡（BalloonTip）、Windows 10/11 原生 Toast 及系统反馈提示音。
- 统一节流与降噪，避免多个模块短时间内并发弹窗刷屏。

---

## 5. 关键系统机制与防坑设计指南

### 5.1 `IPolicyConfig` 工业级多版本兼容与防崩溃方案

#### 核心难点
Windows 官方未公开切换默认音频输出端点的 API。社区通用方案是调用 COM 接口 `IPolicyConfig`。但是：
- Windows 7 使用 `IPolicyConfigVista` (IID: `568b9108-44bf-40b4-9003-b035218649fb`)；
- Windows 10 早期使用 `IPolicyConfig` (IID: `f8679f50-850a-41cf-9c72-430f290290c8`)；
- Windows 10 2004 及 Windows 11 变更为不同虚表偏移与包装；
- 若直接按 C# 静态接口调用，不同系统版本下虚函数表顺序一旦偏差一位，将导致指针调用错误并抛出 `AccessViolationException`。

#### 落地解决方案 (参考 EarTrumpet 成熟架构)
1. **多签名自适应选择**：
   - 声明完整的 COM 虚表包装类，通过检测当前系统内部 Build 号（`Environment.OSVersion` 与注册表 `CurrentBuildNumber`）动态选择对应的 COM 调度方案。
2. **SEH 异常隔离网**：
   - 在音频切换底层方法标注 `[HandleProcessCorruptedStateExceptions]` 与 `[SecurityCritical]` 特性。
   - 即使遭遇底层 COM 异常，也能安全捕获并返回 `false`，降级记录日志，**坚决杜绝托盘主进程崩溃**。
3. **友好模糊匹配**：
   - 用户配置 `"耳机"`，自动通过 `Regex` 或 `Contains` 匹配设备友好名称（`PKEY_Device_FriendlyName`），适应声卡驱动升级导致设备名称尾缀微调的场景。

### 5.2 焦点感知服务 (`IForegroundTracker`) 的 GC 防坑实现

```csharp
public class ForegroundTracker : IForegroundTracker
{
    private readonly Win32Native.WinEventDelegate _hookProc; // 必须由实例强引用持有，严防 GC 回收！
    private IntPtr _hHook;

    public event Action<IntPtr, string> ForegroundChanged;

    public ForegroundTracker()
    {
        _hookProc = OnWinEvent; // 绑定委托
        _hHook = Win32Native.SetWinEventHook(
            Win32Native.EVENT_SYSTEM_FOREGROUND,
            Win32Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookProc,
            0, 0,
            Win32Native.WINEVENT_OUTOFCONTEXT | Win32Native.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hWnd == IntPtr.Zero) return;
        string procName = ProcessHelper.GetProcessNameFromHwnd(hWnd);
        ForegroundChanged?.Invoke(hWnd, procName);
    }

    public void Dispose()
    {
        if (_hHook != IntPtr.Zero)
        {
            Win32Native.UnhookWinEvent(_hHook);
            _hHook = IntPtr.Zero;
        }
    }
}
```

### 5.3 全链路故障隔离与异常防御网

在 .NET 进程内单体中，建立三层防御机制：

```text
[第一层：业务模块级防护 (ModuleBase)]
   ├── 所有模块启动、停止、事件回调由 ModuleBase try-catch 拦截
   └── 单个模块内部异常仅标记该模块为 Faulted 并停用，不扩散

[第二层：宿主调度防护 (Dispatcher & Tasks)]
   ├── App.xaml.cs 挂载 DispatcherUnhandledException (主 UI 线程异常)
   └── TaskScheduler.UnobservedTaskException (后台异步 Task 吞没异常)

[第三层：非托管与底层互操作防护 (SEH)]
   ├── App.config 开启 legacyCorruptedStateExceptionsPolicy
   └── 关键 Win32/COM 调用打上 [HandleProcessCorruptedStateExceptions]
```

### 5.4 动态托盘菜单聚合与分层渲染

托盘菜单采用统一的 WPF `ContextMenu`，数据源动态绑定聚合自各模块的 `TrayMenuItem`：

```text
┌──────────────────────────────────────────────┐
│ [实时状态微卡片 (DeskKit / 运行中 🟢)]         │
├──────────────────────────────────────────────┤
│ 🔒 锁屏与闲时 (ScreenLock)                   │
│    ├─ 立即锁屏                               │
│    └─ 启用闲时锁屏 [✓]                       │
│ 🎧 音频工具 (Audio)                          │
│    ├─ 切换输出设备 (当前: 🎧 耳机)           │
│    └─ 应用自动静音 [✓]                       │
│ ⏰ 自动化计划任务 (Tasks)                     │
│    ├─ 任务管理编辑器...                      │
│    └─ 立即运行: 清理临时文件                 │
├──────────────────────────────────────────────┤
│ ⚙️ 配置中心 (Settings)                        │
│    ├─ 重新加载配置                           │
│    └─ 打开数据目录                           │
│ 🚀 开机自启动 [✓]                            │
│ ❌ 退出 DeskKit                              │
└──────────────────────────────────────────────┘
```

- 顶层为宿主常驻状态微卡片；
- 中间为已启用的业务模块注入菜单（自动按模块分组）；
- 底层为宿主全局控制项（配置、自启、退出）。

---

## 6. 四大核心业务模块具体设计

### 6.1 锁屏与安全防护模块 (`ScreenLockModule`)
- **定位**：继承自现有 `ScreenLock` 核心逻辑，收拢为独立模块。
- **组件**：`LockController`, `IdleDetector`, `KeyboardBlocker`, `PinService`, `LockWindow`。
- **职责**：
  - 监听用户闲时并在超时后呼出全屏伪锁屏遮罩。
  - Windows 会话解锁（`SessionUnlock`）自动联动。
  - 提供 PIN 码验证与全键盘防绕过拦截。

### 6.2 计划任务调度模块 (`TaskSchedulerModule`)
- **定位**：收拢现有 `TaskSchedulerService`。
- **组件**：`TaskRunner`, `CronTrigger`, `FileWatcherTrigger`, `HotkeyTrigger`, `TaskEditorWindow`。
- **职责**：
  - 读取 `tasks.json`，独立管理后台自动化脚本生命周期。
  - 为托盘提供快速手动触发菜单项。

### 6.3 音频输出端点切换模块 (`AudioSwitchModule`)
- **定位**：彻底替代原有 `SwitchAudio.ahk`。
- **职责**：
  - 注册全局热键（如 `Ctrl+\``）。
  - 读取配置中的默认扬声器与默认耳机关键字。
  - 触发热键时，在两种设备间毫秒级切换，并播放轻量提示音。
  - 动态变更托盘菜单文字（如 `当前: 🎧 USB Headset`）。

```csharp
public class AudioSwitchConfig
{
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+`";
    public string SpeakerPattern { get; set; } = "扬声器";
    public string HeadphonePattern { get; set; } = "耳机";
    public bool PlayNotificationSound { get; set; } = true;
}
```

### 6.4 前后台应用智能静音模块 (`AppAutoMuteModule`)
- **定位**：彻底替代原有 `MuteApps.ahk`。
- **职责**：
  - 订阅 `IForegroundTracker.ForegroundChanged`。
  - **前台防抖状态机**：
    - 前台切入目标进程（如 `chrome.exe`, `QQMusic.exe`）：启动取消静音防抖计时，达到 `UnmuteDelayMs`（默认 500ms）后恢复声音；
    - 前台切出目标进程：启动静音防抖计时，达到 `MuteDelayMs`（默认 1000ms）后调用 WASAPI 实施静音。
  - **核心安全保障**：模块停用、热键切换或程序退出时，**强制遍历目标进程并全量解除静音**，杜绝应用被永久意外静音。

```csharp
public class AppAutoMuteConfig
{
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Win+S";
    public int MuteDelayMs { get; set; } = 1000;
    public int UnmuteDelayMs { get; set; } = 500;
    public List<string> TargetApps { get; set; } = new List<string> { "chrome.exe", "QQMusic.exe" };
}
```

---

## 7. 统一配置与向后兼容平滑迁移

统一配置文件存放在 `config.json`：

```json
{
  "General": {
    "Language": "zh-CN",
    "AutoStart": true,
    "LogLevel": "Info"
  },
  "Modules": {
    "ScreenLock": {
      "Enabled": true,
      "IdleMinutes": 5,
      "PinHash": "...",
      "PinSalt": "...",
      "ShowClock": true,
      "OverlayOpacity": 0.88,
      "ExcludeProcesses": ["GenshinImpact.exe"]
    },
    "TaskScheduler": {
      "Enabled": true,
      "TasksFile": "tasks.json"
    },
    "AudioSwitch": {
      "Enabled": true,
      "Hotkey": "Ctrl+`",
      "SpeakerPattern": "扬声器",
      "HeadphonePattern": "耳机",
      "PlayNotificationSound": true
    },
    "AppAutoMute": {
      "Enabled": true,
      "Hotkey": "Ctrl+Win+S",
      "MuteDelayMs": 1000,
      "UnmuteDelayMs": 500,
      "TargetApps": ["chrome.exe", "QQMusic.exe"]
    }
  }
}
```

### 向后平滑兼容机制
首次启动新版本时，`ConfigManager` 检测若存在老版扁平格式配置（如直接在根节点存在 `IdleMinutes`、`PinHash` 等），**自动无损迁移**至 `Modules.ScreenLock` 节点并写回，老用户无感平滑升级。

---

## 8. 代码工程结构规划

```text
src/
├── Core/                                // 纯抽象契约层 (无第三方复杂依赖)
│   ├── IModule.cs
│   ├── ModuleBase.cs
│   ├── Models/
│   │   ├── TrayMenuItem.cs
│   │   └── LogLevel.cs
│   └── Win32/
│       ├── Win32Native.cs
│       └── ComGuids.cs
│
├── Host/                                // 宿主与基础设施层
│   ├── App.xaml / App.xaml.cs           (入口、Mutex、全局异常防护)
│   ├── Services/
│   │   ├── ModuleManager.cs             (模块扫描与生命周期总线)
│   │   ├── ServiceContainer.cs          (轻量 IoC 容器)
│   │   ├── HotkeyService.cs             (Win32 全局热键路由)
│   │   ├── ForegroundTracker.cs         (SetWinEventHook 焦点感知)
│   │   ├── AudioService.cs              (WASAPI + IPolicyConfig 自适应引擎)
│   │   ├── ConfigManager.cs             (JSON 分区存取与平滑迁移)
│   │   ├── TrayManager.cs               (托盘图标与响应式菜单绑定)
│   │   └── NotificationService.cs       (气泡与提示音)
│   └── Views/
│       └── TrayContextMenu.xaml         (现代托盘右键菜单模板)
│
└── Modules/                             // 独立业务模块层
    ├── ScreenLock/                      // 模块 1: 锁屏与安全防护
    │   ├── ScreenLockModule.cs
    │   ├── Models/ScreenLockConfig.cs
    │   ├── Services/ (IdleDetector, LockController, PinService, KeyboardBlocker)
    │   └── Views/ (LockWindow, FirstRunWindow, VerifyPinWindow)
    │
    ├── TaskScheduler/                   // 模块 2: 任务计划与自动化
    │   ├── TaskSchedulerModule.cs
    │   ├── Models/TaskDefinition.cs
    │   ├── Services/ (TaskRunner, ScriptResolver, Triggers/*)
    │   └── Views/ (TaskEditorWindow)
    │
    ├── AudioSwitch/                     // 模块 3: 输出设备快速切换
    │   ├── AudioSwitchModule.cs
    │   └── Models/AudioSwitchConfig.cs
    │
    └── AppAutoMute/                     // 模块 4: 应用智能静音
        ├── AppAutoMuteModule.cs
        └── Models/AppAutoMuteConfig.cs
```

---

## 9. 实施路线图与验证方案

```text
[阶段 1：契约定义与底座改造]
   ├── 建立 Core 抽象契约 (IModule, ModuleBase, TrayMenuItem)
   ├── 实现 IHotkeyService 与 IForegroundTracker (验证 GC 委托常驻安全)
   └── 实现 AudioService 基础结构并完成 Windows 10/11 虚表兼容性验证

[阶段 2：存量功能模块化封装]
   ├── 将锁屏核心逻辑封装进 ScreenLockModule
   ├── 将任务调度逻辑封装进 TaskSchedulerModule
   └── 启动联调，确保原有功能 100% 行为一致，老配置平滑迁移

[阶段 3：音频两大新模块落地]
   ├── 落地 AudioSwitchModule (快捷键触发、WASAPI 切换、托盘文字响应式更新)
   └── 落地 AppAutoMuteModule (焦点事件订阅、防抖计时、退出全量恢复测试)

[阶段 4：工程更名与整体交付]
   ├── 方案成熟后评估工程由 ScreenLock 更名为 DeskKit / TrayTools
   └── Costura.Fody 单文件打包实机深度回归验证
```

---

## 10. 结论与架构价值

1. **高可靠性**：
   - 彻底规避了非公开 COM 接口引发 SEH 闪退的风险；
   - 统一由 `IForegroundTracker` 管理 Win32 钩子，从源头消除了 GC 回收函数指针的隐患；
   - 建立了全链路异常兜底网络。
2. **高易用性与极致性能**：
   - 彻底取代 AHK 脚本和外部 `nircmd.exe`，切换延迟降至毫秒级，无子进程频繁开销；
   - 托盘菜单响应式刷新，用户操作丝滑无抖动。
3. **极佳的可维护性与扩展性**：
   - 依靠 `ModuleBase<TConfig>` 契约，任何新功能（如剪贴板历史、窗口快捷置顶、取色器等）均可在独立子目录内开发，无需碰触宿主核心代码，随时插拔。
   - 依然保持单一独立的便携 EXE，完全符合轻量级绿色桌面工具的最佳工程范式。
