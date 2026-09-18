# CarroDesk 模块架构深度评估与实施方案（修订版 v3.2）

> **目标版本**：v3.2 Modular Monolith Implementation Spec  
> **制定日期**：2026-09-18  
> **基线状态**：基于 Commit `a16fb65` 治理后的代码库状态，并吸收 `docs/MODULE-ARCHITECTURE-REVIEW-20260918-dsf.md` 的深度审查意见。  
> **核心原则**：
> 1. **直接重构，无数据迁移包袱**：不引入双读单写、旧格式兼容转换层或冗余适配器，直接落地目标结构，杜绝过渡复杂度；
> 2. **真·垂直切片单体（Vertical Slice Modular Monolith）**：单程序集下通过目录自治与架构断言强约束边界，各模块内聚独立；
> 3. **小步快跑，逐步验证**：每一步改动后运行全量单元测试，确保零回归。

---

## 1. 架构目标与总体拓扑

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        Host (微内核运行时与通用窗口)                     │
│  ServiceContainer | ModuleManager | DynamicTrayController              │
│  FloatingPanelWindow | ConfigEditorWindow (仅宿主设置)                   │
│  HotkeyService (Win32消息路由基础设施) | MenuProjectionEngine (通用投影) │
└───────────────────┬────────────────────────────────┬───────────────────┘
                    │ 消费 Core 契约                 │ 调度与分发
                    ▼                                ▼
┌────────────────────────────────────────────────────────────────────────┐
│                         Core (纯抽象层，零三方依赖)                     │
│  IModule | ModuleBase<TConfig> | IModuleContext | TrayMenuItem         │
│  IExitGuard | INotificationService | IHotkeyService | IIdleService     │
└───────────────────▲────────────────────────────────▲───────────────────┘
                    │ 实现契约                       │ 消费上下文
┌───────────────────┴────────────────────────────────┴───────────────────┐
│                     Modules (自包含业务垂直切片，禁止互引)                │
│                                                                        │
│  ScreenLockModule     TaskSchedulerModule   AudioSwitchModule          │
│  AppAutoMuteModule    MonitorProfileModule  AwakeModule                │
│                                                                        │
│  每个模块内部均内聚包含：                                                │
│  ├── [ModuleName]Module.cs (入口与契约实现)                             │
│  ├── Services/ (领域服务与控制器)                                       │
│  ├── Models/   (强类型配置与领域 DTO)                                   │
│  └── Views/    (专属设置/交互窗口，从托盘或悬浮面板直接呼出)             │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. 核心技术方案详述

### 2.1 测试保护网修复（Phase 0）
- **修复断言失效**：修正 `ConfigAndTriggerTests.cs:162` 查找 `CarroDesk.sln` 为向上溯源查找 `.git` 或 `Directory.Build.props`；找不到根路径时必须 `Assert.Fail`，禁止静默跳过。
- **全量扫描防腐**：扫描范围扩大为 `src/**`（通过白名单仅排除 `Host/` 与 `Core/`），确保任何业务模块和视图中均不出现 `App.*` 静态门面。
- **显式编译登记**：在 `tests/CarroDesk.Tests/CarroDesk.Tests.csproj` 的 `<Compile Include>` 中显式登记所有新测试文件。

### 2.2 统一通知流与单例清收（Phase 1）
- **通知流收敛**：删除 5 个模块暴露的 `NotificationCallback` 与 `BalloonNotifier` 属性，模块内部统一通过 `Context.ShowNotification(msg)` 发送通知；移除 `App.xaml.cs` 中 5 处多余的手工委托布线。
- **清除静态单例**：
  - `AppAutoMuteSettingsWindow` 构造函数强制要求传入模块实例，移除其私有字段的 `?? AppAutoMuteModule.Instance` 兜底；
  - 彻底删除 6 个模块类中残留的 `public static XxxModule Instance { get; private set; }` 废弃单例。

### 2.3 提取生产级菜单投影引擎（Phase 2）
在 `src/Host/Services/MenuProjectionEngine.cs` 提取统一的无状态静态投影引擎，彻底消除 `DynamicTrayController` 与 `FloatingPanelWindow` 的构建代码重复。引擎完整保留并整合以下 4 项生产级行为：
1. **Separator 渲染**：正确将 `TrayMenuItem.IsSeparator == true` 转换为 WPF `Separator`。
2. **动态子项更新**：统一订阅 `node.Children.CollectionChanged`，并在子项增删时触发刷新回调。
3. **健壮的异常与通知**：点击回调捕获异常时记录错误日志并发送气泡通知，杜绝静默吞异常。
4. **差异化交互策略**：参数化支持悬浮展开（FloatingPanel 专用）与普通点击展开（Tray 专用），统一执行点击后关闭子菜单回调。

### 2.4 资产划分与物理垂直切片归位（Phase 3）
明确所有灰色资产归属并完成物理移动，调整命名空间及引用：

| 资产 | 目标路径 | 归属定位 |
|---|---|---|
| `src/Services/Tasks/*` (10文件) + `Triggers/*` (9文件) | `src/Modules/TaskScheduler/Services/*` | 调度模块私有实现 |
| `src/Models/TaskDefinition.cs` | `src/Modules/TaskScheduler/Models/TaskDefinition.cs` | 调度模块领域模型 |
| `src/Services/Tasks/HotkeyService.cs` | `src/Host/Services/HotkeyService.cs` | **Host 基础设施**（消除 TaskScheduler 反向耦合） |
| `src/Services/Tasks/HotkeyHelper.cs` | `src/Host/Services/HotkeyHelper.cs` | **Host 基础设施工具** |
| `src/Services/LockController.cs` | `src/Modules/ScreenLock/Services/LockController.cs` | 锁屏模块私有实现 |
| `src/Services/KeyboardBlocker.cs` | `src/Modules/ScreenLock/Services/KeyboardBlocker.cs` | 锁屏模块私有实现 |
| `src/Services/ILockService.cs` / `ILockAppearance.cs` | `src/Modules/ScreenLock/Services/` | 锁屏模块内部契约 |
| `src/Services/IdleDetector.cs` | `src/Modules/TaskScheduler/Services/IdleDetector.cs` | 任务前置条件私有工具 |
| `src/Services/ProcessHelper.cs` / `ProcessExclusionService.cs` | `src/Services/` (保留) | **SharedKernel 共享内核** |
| 5 个模块设置窗口 (`*SettingsWindow.xaml`) | 各自 `src/Modules/*/Views/` | **模块视图就地自治** |

### 2.5 配置与安全体系终极切断（Phase 4）
- **全新配置结构（无包袱直接生效）**：
  ```json
  {
    "Host": {
      "Language": "auto",
      "AutoStart": true,
      "FloatingPanel": {
        "Hotkey": "Win+Alt+C",
        "Position": "Tray",
        "Pinned": false,
        "Locked": false,
        "X": -1.0,
        "Y": -1.0
      },
      "Security": {
        "PinSalt": "...",
        "PinHash": "..."
      }
    },
    "Modules": {
      "ScreenLock": { "IdleMinutes": 5, "ShowClock": true, "OverlayOpacity": 0.88, "UnlockOnResume": true, "ExcludeProcesses": [] },
      "TaskScheduler": { "GlobalEnabled": true },
      "AudioSwitch": { "AutoSwitchOnConnect": true },
      "AppAutoMute": { "Mode": "Blacklist", "TargetApps": [] },
      "Awake": { "Mode": "Passive", "KeepDisplayOn": true },
      "MonitorProfile": { "ActiveProfile": "Daily" }
    }
  }
  ```
- **删除冗余适配器与硬编码**：
  - 彻底删除 `IConfigRegistry.cs`；
  - 彻底删除 `ConfigManager.cs` 中的 `_adapters` 与 `_defaultFactories` 桥接逻辑；
  - 彻底删除 `ConfigService.cs` 中的 `IsCoreAppSetting`（208-232 行）硬编码字典和 4 个字符串槽；
  - `ScreenLockModule` 与 `TaskSchedulerModule` 构造函数彻底移除 `ConfigService` 依赖，回归干净的 `ModuleBase<TConfig>`。
- **PIN 归属与退出守卫闭环**：
  - PIN 唯一归属 `Host.Security`，由 `HostPinService`（`IPinService`）全权管理；
  - `ScreenLockConfig` 删除 `PinHash` 与 `PinSalt` 冗余字段；
  - `ScreenLockModule.RequestBlockExit` 直接调用 `Context.GetService<IPinService>().IsConfigured` 判断是否需要校验 PIN，杜绝空校验阻断漏洞。
- **设置中心精简化**：
  - `ConfigEditorWindow` 瘦身为纯粹的宿主通用设置（语言、开机自启、悬浮窗快捷键、PIN 设置与修改）；
  - 业务模块的专属配置完全由各模块在托盘/悬浮窗弹出的独立设置窗口自治，无需在宿主拼装复杂 UI。

---

## 3. 实施步骤与质量保障

| 阶段 | 实施内容 | 验证与卡点 |
|---|---|---|
| **Phase 0** | 修复 `ViewLayer_HasNoStaticAppReferences` 根路径查找 Bug；确保测试真实执行断言并全绿。 | `dotnet test` 76 项通过，且断言代码必定被执行。 |
| **Phase 1** | 清理 5 个模块的 `NotificationCallback`，清除 6 个模块的 `static Instance` 单例。 | `dotnet build` 0 错误，测试 100% 通过。 |
| **Phase 2** | 提炼 `MenuProjectionEngine`，替换 `DynamicTrayController` 与 `FloatingPanelWindow` 的重复构建代码。 | 悬浮窗与托盘菜单项渲染一致，测试 100% 通过。 |
| **Phase 3** | 迁移 `Tasks`、`LockController`、`HotkeyService` 与 5 个窗口至对应切片，修正命名空间与调用点。 | 项目结构清晰，无跨模块互引，测试 100% 通过。 |
| **Phase 4** | 落地 `Host` + `Modules` 独立配置结构，删除 `IConfigRegistry` 适配器，PIN 收敛于 `IPinService`。 | 配置干净持久化，退出守卫正常工作，测试 100% 通过。 |
