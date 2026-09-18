# CarroDesk 模块开发标准与测试指南 (Module Development Guide)

> 版本：v1.0  
> 适用架构：进程内垂直切片模块化单体（In-Process Vertical Slice Modular Monolith）  
> 适用范围：所有 CarroDesk 新增功能模块开发、老模块演进与单元测试编写。

---

## 1. 核心架构原则

在 CarroDesk 中新增或维护模块，必须遵循以下核心架构准则：

1. **垂直切片物理高内聚（Vertical Slice Architecture）**
   - 模块的所有代码（服务 `Services`、数据模型 `Models`、界面视图 `Views`）必须完全物理收敛在 `src/Modules/{ModuleName}/` 目录内。
   - 严禁将模块私有的 Service 散落于 `src/Services/`，或将设置窗口散落于根 `src/Views/`。

2. **零静态单例与微内核依赖倒置**
   - **绝对禁止**在模块中声明 `public static XxxModule Instance` 或任何对外暴露的静态单例字段。
   - 模块实例由微内核 `ModuleManager` 统一管理生命周期。
   - 模块所需宿主能力（日志、通知、热键、空闲探测等）一律通过 `IModuleContext.GetService<T>()` 动态解析获取，严禁直接访问 `App.*` 全局静态门面。

3. **原生独立配置契约（零迁移包袱）**
   - 每个模块在 `config.json` 中独占顶层同名原生 JSON 对象节点（例如 `"MyModule": { ... }`）。
   - **严禁**在宿主 `AppSettings` 中添加模块私有配置字段。
   - 模块配置由 `ConfigManager.GetModuleConfig<T>()` 与 `SaveModuleConfig<T>()` 原生序列化/反序列化，禁止使用中间过渡适配器层。

4. **托盘与悬浮菜单二级收敛契约**
   - 每个模块在 `GetTrayMenuItems()` 中**严格只允许输出 1 个根节点**（`TrayMenuItem`），模块所有控制项、状态项、子操作均收敛在该根节点的子菜单（`Children`）中。
   - 严禁向托盘顶层散落平铺多个菜单项破坏全局布局。

5. **资源安全与异常隔离**
   - 模块的业务代码必须具备异常自愈能力，严禁静默捕获吞异常（catch 必须有语义日志或注释说明）。
   - 模块的崩溃会被微内核捕获并标记为 `ModuleStatus.Faulted`，不应击穿导致宿主进程崩溃。
   - 停止（`Stop`）或释放（`Dispose`）时必须彻底注销热键、Hook、计时器并释放非托管句柄。

---

## 2. 模块目录组织规范

新建模块命名采用大驼峰（PascalCase），例如 `ClipboardHistory`、`NetworkMonitor`。

```text
src/Modules/{ModuleName}/
├── {ModuleName}Module.cs                 # [必选] 模块入口类，继承 ModuleBase<TConfig>
├── Models/
│   ├── {ModuleName}Config.cs            # [必选] 模块强类型配置模型
│   └── {Feature}Item.cs                 # [可选] 领域实体、DTO、传输模型
├── Services/
│   ├── I{Feature}Service.cs             # [建议] 核心业务服务接口（便于单测 Mock）
│   └── {Feature}Service.cs              # [必选] 核心业务逻辑实现（无 UI 依赖）
└── Views/                               # [可选] 模块专属界面（如独立配置窗口）
    ├── {ModuleName}SettingsWindow.xaml
    └── {ModuleName}SettingsWindow.xaml.cs
```

> **XAML 视图规范**：模块专属视图放置在 `src/Modules/{ModuleName}/Views/` 目录下。为了与 WPF 既有 XAML 绑定和资源解析无缝兼容，推荐命名空间使用 `namespace CarroDesk.Views` 或直接对应模块命名空间。

---

## 3. 核心接口与生命周期契约

### 3.1 `IModule` 与 `ModuleBase<TConfig>`

所有模块均实现 `CarroDesk.Core.IModule` 接口。**推荐所有业务模块直接继承 `ModuleBase<TConfig>`**，其已内置完备的线程安全状态机（`ModuleStatus`）：

```csharp
using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Modules.MyFeature.Models;
using CarroDesk.Modules.MyFeature.Services;

namespace CarroDesk.Modules.MyFeature
{
    public class MyFeatureModule : ModuleBase<MyFeatureConfig>
    {
        // 1. 基础元数据
        public override string Id => "MyFeature";                                    // 全局唯一标识符
        public override string Name => Loc.T("Tray.MyFeature", "我的特性");          // 支持多语言
        public override string Description => "提供我的特性后台服务与配置管理";
        public override string Version => "1.0.0";
        public override int Order => 30;                                             // 托盘与启动排序（越小越靠前）
        public override bool DefaultEnabled => true;                                 // 默认是否随宿主启动

        private MyFeatureService _service;

        // 2. 初始化（微内核启动第一阶段）
        public override void Initialize(IModuleContext context)
        {
            base.Initialize(context); // 自动从 ConfigManager 加载 Config
            
            var logger = context.GetService<ILoggerService>();
            _service = new MyFeatureService(logger);
            _service.SomethingHappened += OnSomethingHappened;
        }

        // 3. 启动（微内核启动第二阶段）
        protected override void OnStart()
        {
            // 注册全局热键、启动后台循环、开启文件监听等
            var hotkeys = Context.GetService<IHotkeyService>();
            if (hotkeys != null && !string.IsNullOrWhiteSpace(Config.Hotkey))
            {
                hotkeys.Register(Id, Config.Hotkey, () => _service.TriggerAction(), out _);
            }

            _service.Start(Config);
        }

        // 4. 停止（微内核优雅退出阶段，倒序调用）
        protected override void OnStop()
        {
            // 卸载热键、关闭计时器、释放非托管句柄
            var hotkeys = Context.GetService<IHotkeyService>();
            hotkeys?.UnregisterAll(Id);

            _service?.Stop();
        }

        // 5. 配置动态重载回调
        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded(); // 自动刷新 this.Config 为最新值
            _service?.ApplyConfig(Config);
            UpdateTrayHeader();
        }

        // 6. 语言动态切换回调
        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeader();
        }

        // 7. 托盘菜单输出（二级收敛契约）
        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var root = new TrayMenuItem
            {
                Id = "myfeature_root",
                Header = BuildHeader(),
                ToolTip = BuildToolTip()
            };

            root.Children.Add(new TrayMenuItem
            {
                Id = "myfeature_toggle",
                Header = Loc.T("MyFeature.Toggle", "启用功能"),
                IsChecked = Config.Enabled,
                ClickAction = () => ToggleEnabled()
            });

            root.Children.Add(TrayMenuItem.Separator());

            root.Children.Add(new TrayMenuItem
            {
                Id = "myfeature_settings",
                Header = Loc.T("Common.SettingsWithDots", "设置..."),
                ClickAction = () => OpenSettingsWindow()
            });

            yield return root; // 严格仅输出 1 个根节点！
        }

        private void OnSomethingHappened(string msg)
        {
            // 统一通过上下文弹通知，严禁私自定义通知委托
            Context?.ShowNotification(msg, Name);
        }

        private void ToggleEnabled()
        {
            Config.Enabled = !Config.Enabled;
            // 强类型保存至 config.json 顶层模块节点
            Context?.GetService<IConfigManager>()?.SaveModuleConfig(Id, Config);
            Context?.RequestTrayRefresh(); // 请求托盘与悬浮窗同步重刷
        }

        private void OpenSettingsWindow()
        {
            var win = new Views.MyFeatureSettingsWindow(this)
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
            };
            win.ShowDialog();
        }
    }
}
```

---

## 4. 模块配置规范

### 4.1 配置模型定义

配置类位于 `src/Modules/{ModuleName}/Models/{ModuleName}Config.cs`：

```csharp
using System.Collections.Generic;
using CarroDesk.Models.Converters;
using Newtonsoft.Json;

namespace CarroDesk.Modules.MyFeature.Models
{
    public class MyFeatureConfig
    {
        public bool Enabled { get; set; } = true;
        public int IntervalSeconds { get; set; } = 60;
        public string Hotkey { get; set; } = "Win+Alt+M";

        // 支持数组 ["a","b"] 或逗号分隔字符串 "a, b"
        [JsonConverter(typeof(StringOrStringListConverter))]
        public List<string> FilterList { get; set; } = new List<string>();

        // 推荐实现 Clone，便于设置窗口编辑与回退
        public MyFeatureConfig Clone()
        {
            return new MyFeatureConfig
            {
                Enabled = Enabled,
                IntervalSeconds = IntervalSeconds,
                Hotkey = Hotkey,
                FilterList = FilterList != null ? new List<string>(FilterList) : new List<string>()
            };
        }

        // 可选：如果包含复杂初始化逻辑，可提供公共静态默认工厂，ConfigManager 反射优先采用
        public static MyFeatureConfig CreateDefault()
        {
            return new MyFeatureConfig();
        }
    }
}
```

### 4.2 配置落盘与读取机制

- **自动绑定**：`ModuleBase.Initialize` 会自动通过 `IConfigManager.GetModuleConfig<TConfig>(Id)` 反序列化 `config.json` 中名为 `Id` 的原生对象。如果不存在，则实例化默认对象。
- **动态更新保存**：
  ```csharp
  var configMgr = Context.GetService<IConfigManager>();
  configMgr.SaveModuleConfig(Id, Config);
  ```
- **示例 JSON 结构**（`config.json`）：
  ```json
  {
    "AutoStart": true,
    "Language": "zh-CN",
    "MyFeature": {
      "Enabled": true,
      "IntervalSeconds": 60,
      "Hotkey": "Win+Alt+M",
      "FilterList": ["proc1", "proc2"]
    }
  }
  ```

### 4.3 模块业务持久化数据规范（Payload Data vs Config）

- **职责边界**：
  - **模块配置（Config）**：存储用户偏好、开关、常量阈值（如最大条数、保留天数、快捷键）。由 `IConfigManager` 管理，存放在全局 `config.json` 中。
  - **业务数据（Payload Data）**：存储模块运行期产生的大容量、高频变动的业务记录（如剪贴板历史、任务日志、网络流量统计、离线缓存）。
  - **严禁红线**：**严禁将业务数据存入 `config.json`！** 否则会导致配置文件体积急剧膨胀，拖慢全局保存与序列化，甚至可能导致配置竞争损坏。
- **存储路径约定**：
  - 模块业务数据必须统一定位在宿主数据目录下的 `data/{ModuleName}/` 目录中。
  - 统一通过 `ConfigService.DirPath` 解析基准目录（自动适配便携版与 AppData 安装版）：
    ```csharp
    // 获取模块专属持久化数据目录
    string moduleDataDir = Path.Combine(ConfigService.DirPath, "data", "ClipboardHistory");
    Directory.CreateDirectory(moduleDataDir);
    string dataFilePath = Path.Combine(moduleDataDir, "history.json");
    ```
- **写入与并发保护**：
  - 采用独立的文件读写抽象（如 `I{Feature}Storage`），业务持久化操作建议异步非阻塞写入（如先写临时文件后原子替换，或加文件锁）。

---

## 5. UI 与交互规范

### 5.1 托盘与悬浮菜单集成（`MenuProjectionEngine`）

- **数据契约声明**：模块不负责 WPF ContextMenu 或 MenuItem 的创建，只需通过 `TrayMenuItem` 纯数据结构声明。
- **自愈机制**：宿主 `MenuProjectionEngine` 会自动合并多余的分隔符、剔除头部/尾部分隔符、响应 `Children.CollectionChanged` 动态增删子菜单。
- **刷新通知**：当模块内部状态变化需要重刷托盘状态文案或勾选项时，调用 `Context.RequestTrayRefresh()`。

### 5.2 弹窗与系统气泡

- **统一入口**：必须通过 `Context.ShowNotification(message, title)`。
- **严禁事项**：
  - 严禁自行声明 `Action<string>` 委托并由 App 手工连线。
  - 严禁直接依赖 `System.Windows.Forms.NotifyIcon` 或 `Hardcodet.Wpf.TaskbarNotification`。

### 5.3 独立设置窗口设计

- 构造函数传入模块实例或通过 `ServiceContainer` 解析依赖，**严禁使用 `App.MyFeature` 或全局单例访问**。
- 设置窗口应提供保存（Save）与取消（Cancel）机制，配合 `Config.Clone()` 操作，避免界面未点击确定即污染运行时配置。

### 5.4 快捷浮动启动窗设计规范（Quick-Launcher / Popup Window）

针对通过全局快捷键唤出的浮动小窗（如剪贴板历史列表、快速启动搜索框、迷你监视窗）：
- **生命周期（Hide 代替 Close）**：浮窗创建后应尽可能常驻，按下快捷键时显示，关闭或失焦时调用 `Hide()`，避免频繁创建销毁 HWND 导致闪烁与开销。仅在 `Module.OnStop()` 时调用 `Close()`。
- **失焦自隐（Auto-Dismiss）**：必须监听 `Window.Deactivated` 事件，当用户点击桌面其他程序时自动 `Hide()`。
- **抢占焦点与唤出定位**：
  ```csharp
  public void ShowAndActivate()
  {
      if (Visibility != Visibility.Visible) Show();
      WindowState = WindowState.Normal;
      Activate();
      Focus();
      // 可选：将窗口居中于当前鼠标所在屏幕或主显示屏
  }
  ```
- **键盘导航友好**：必须支持 `Esc` 退出隐藏、方向键上下选择条目、`Enter` 确认操作。
- **回写防自环（Self-Feedback Loop Prevention）**：若窗口操作涉及向系统底层回写数据（如双击条目回写剪贴板、模拟按键），在写入前必须通知底层服务抑制一次自身监听广播，防止循环入库。

---

## 6. 跨模块服务与安全边界

宿主 `IModuleContext` 提供通用基础设施。模块可通过 `context.GetService<T>()` 获取：

| 服务接口 | 用途 | 模块使用场景 |
| :--- | :--- | :--- |
| `ILoggerService` | 统一分级日志 | 记录模块业务运行、诊断与异常详情 |
| `IHotkeyService` | 全局热键注册与注销 | 模块快捷键（按模块 ID 作用域全生命周期隔离） |
| `IIdleService` | 系统空闲状态广播与全屏探测 | 闲时节能、自动化触发、游戏全屏免打扰 |
| `IConfigManager` | 原生模块配置读写 | 模块强类型配置存取与重载 |
| `IPinService` | 宿主安全 PIN 验证 | 安全敏感模块的放行校验（如锁屏退出挑战） |
| `Dispatcher` | WPF UI 线程调度器 | 跨线程事件派发至 UI 渲染 |

### 6.3 Win32 / 系统原生消息监听范式（HWND 与 STA 约束）

当模块需要监听 Windows 系统广播（如剪贴板变更 `WM_CLIPBOARDUPDATE`、设备插拔 `WM_DEVICECHANGE`、电源变化 `WM_POWERBROADCAST`）时：
1. **轻量原生消息窗**：推荐继承 `System.Windows.Forms.NativeWindow`（参考 `HotkeyService`），在 STA 线程通过 `CreateHandle(new CreateParams())` 创建纯消息隐形窗口，无需额外渲染 WPF Window。
2. **STA 线程与 COM 边界**：
   - 诸如 `Clipboard.GetText()` 等 Windows Shell/OLE API 必须在 **STA 线程**（或调度回 `Context.Dispatcher`）中执行，严禁直接在 MTA 后台线程调用。
3. **并发竞争与重试保护**：
   - 系统剪贴板等共享资源经常被其他应用程序临时占用。读取必须包含指数退避/短暂休眠重试（如 3 次尝试，每次间隔 50ms），捕获 `CLIPBRD_E_CANT_OPEN` (0x800401D0) 异常。
4. **测试脱机解耦**：
   - 核心业务服务严禁直接调用 Win32 P/Invoke。应定义接口（如 `IClipboardListener`），业务服务只订阅抽象接口事件，方便脱机单测使用 Mock 驱动。

---

## 7. 新增模块实施标准步骤（SOP）

当在项目中开发一个新模块时，按顺序执行以下 7 步：

1. **新建模块目录**：在 `src/Modules/` 下创建子目录，建立 `Models/`、`Services/` 与 `Views/`。
2. **定义配置模型**：编写 `Models/{ModuleName}Config.cs`，提供默认值与必要属性。
3. **实现核心服务**：编写 `Services/{ModuleName}Service.cs`，保持无 UI 状态、独立可测。
4. **实现模块入口**：编写 `{ModuleName}Module.cs`，继承 `ModuleBase<{ModuleName}Config>`，实现核心声明周期与 `GetTrayMenuItems()`。
5. **添加本地化文案**：在 `src/Assets/Locales/zh-CN.json` 和 `en-US.json` 中追加模块使用的语言键值。
6. **宿主注册**：
   在 `src/App.xaml.cs` 的 `OnStartup` 方法中注册新模块：
   ```csharp
   var myModule = new MyFeatureModule();
   Modules.RegisterModule(myModule);
   ```
7. **补充示例配置**：在 `src/Samples/config.sample.json` 中追加该模块的默认注释样例。

---

## 8. 模块测试方法与自动化断言

每个模块必须在 `tests/CarroDesk.Tests/` 目录下配套独立的单元测试套件 `{ModuleName}Tests.cs`。

### 8.1 必备单测清单

1. **配置往返测试（Config Round-Trip Test）**
   - 验证 `ConfigManager.GetModuleConfig` 默认值正确。
   - 验证 `ConfigManager.SaveModuleConfig` 能写入原生 JToken 并成功重新加载。
2. **托盘二级收敛契约测试（Tray Menu Single-Root Contract）**
   - 断言 `module.GetTrayMenuItems().Count() == 1`。
   - 验证根项 Header 格式及必要二级控制项存在。
3. **脱机可测试性（Offline Testability）**
   - 验证 Service 和 Module 实例在没有 `Application.Current`、没有真实托盘图标的环境下可直接 `new` 并在测试运行时调用。
4. **生命周期与状态测试（Lifecycle State Transition）**
   - 验证 `Initialize -> Start -> Stop` 状态流转符合 `ModuleStatus` 规范。
   - 验证模块 Start 异常被标记为 `ModuleStatus.Faulted`，且不会影响其他模块。
5. **资源句柄防泄漏测试（Resource Dispose Test）**
   - 针对使用 `Timer`、`Process`、`CancellationTokenSource`、Win32 句柄的 Service，验证 `Stop()` 或 `Dispose()` 后的句柄归零。

### 8.2 测试模板样例

```csharp
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.MyFeature;
using CarroDesk.Modules.MyFeature.Models;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class MyFeatureModuleTests
    {
        [TestMethod]
        public void MyFeatureModule_TrayMenu_EnforcesSingleRootItem()
        {
            var module = new MyFeatureModule();
            var items = module.GetTrayMenuItems()?.ToList();

            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "必须严格遵守托盘二级收敛规范，输出单一根节点");
            Assert.AreEqual("myfeature_root", items[0].Id);
            Assert.IsTrue(items[0].Children.Count >= 2, "二级菜单应包含操作子项");
        }

        [TestMethod]
        public void MyFeatureModule_Config_DirectSerializationRoundTrip()
        {
            var configService = new ConfigService();
            configService.LoadOrCreate();

            var configMgr = new ConfigManager(configService);
            var module = new MyFeatureModule();

            var cfg = new MyFeatureConfig
            {
                Enabled = false,
                IntervalSeconds = 120,
                Hotkey = "Ctrl+Alt+P"
            };

            configMgr.SaveModuleConfig("MyFeature", cfg);
            Assert.IsNotNull(configService.GetModuleToken("MyFeature"));

            var reloaded = configMgr.GetModuleConfig<MyFeatureConfig>("MyFeature");
            Assert.IsNotNull(reloaded);
            Assert.IsFalse(reloaded.Enabled);
            Assert.AreEqual(120, reloaded.IntervalSeconds);
            Assert.AreEqual("Ctrl+Alt+P", reloaded.Hotkey);
        }
    }
}
```

---

## 9. 开发红线与反模式（Anti-Patterns）

| 禁忌反模式 | 为什么禁止 | 正确做法 |
| :--- | :--- | :--- |
| **❌ 声明 `public static Module Instance`** | 产生跨模块硬依赖、循环引用、使单测完全无法脱机运行。 | 通过微内核构造注入或 `Context.GetService<T>()`。 |
| **❌ 向 `AppSettings` 塞模块私有字段** | 破坏模块自治，引起宿主与模块强耦合及合并冲突。 | 独占 `MyFeatureConfig`，由 `ConfigManager` 泛型读写。 |
| **❌ 跨模块直接引用其它模块内部类** | 形成蜘蛛网式网状依赖，导致模块无法独立替换与重构。 | 将共享能力提升至 `src/Host/Services/` 或定义在 `Core` 契约接口中。 |
| **❌ 模块直接操作 WPF 托盘图标组件** | 造成托盘状态竞争、闪烁与内存泄漏。 | 仅输出 `TrayMenuItem` 数据，由 `MenuProjectionEngine` 统一渲染。 |
| **❌ 在 `Initialize()` 中执行耗时操作** | 阻塞宿主托盘初始化，导致界面无响应与启动卡顿。 | 轻量初始化，后台或耗时逻辑在 `OnStart()` 异步触发。 |
| **❌ 空 Catch 静默吞异常** | 隐藏严重故障，导致系统异常难以排查与数据损坏。 | `catch (Exception ex)` 必须记录日志并添加注释说明处置意图。 |
| **❌ 将业务持久化数据塞入 `config.json`** | 配置文件体积膨胀，降低保存性能，存在配置并发破坏风险。 | 业务数据独立持久化到 `ConfigService.DirPath/data/{Module}/`。 |
| **❌ 在后台线程直接访问系统剪贴板或用死循环硬轮询** | 触发 Win32 COM/STA 线程安全异常，CPU 占用居高不下。 | 采用 `AddClipboardFormatListener` 消息通知 + `Dispatcher` 调度。 |
| **❌ 浮动小窗每次唤出 `new` / 关闭时直接 `Close()`** | 频繁重建非托管 HWND 引发界面卡顿闪烁及内存碎片。 | 维持常驻单例浮窗，通过 `ShowAndActivate()` 与 `Hide()` 切换显隐。 |

