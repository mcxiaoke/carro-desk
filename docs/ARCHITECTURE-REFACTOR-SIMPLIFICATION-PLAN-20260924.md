# CarroDesk 架构重构与代码精简方案

> **日期**：2026-09-24  
> **目标**：彻底消除模块间重复胶水代码与样板代码，提升系统的可靠性与可维护性，实现净代码量减少（做减法），严禁破坏既有业务逻辑与 UI 渲染交互。

---

## 一、现状审查与问题定位

经过对 Host 与 6 个业务模块（`Awake`、`AudioSwitch`、`AppAutoMute`、`ClipboardHistory`、`MonitorProfile`、`ScreenLock`）的系统化核查，当前代码库存在以下增量膨胀（Accretion Bloat）根源：

### 1. `ModuleBase<TConfig>` 职责过薄
- **快捷键样板代码 100% 重复**：全部 6 个模块均各自维护私有 `_hotkeyId`、`RegisterHotkey()`、`UnregisterHotkey()`、`try-catch`、`OnConfigReloaded` 解绑重绑、`OnStop` 注销。累计无谓代码达 250+ 行。
- **跨线程刷新托盘 Header / ToolTip 100% 重复**：6 个模块各自编写 `if (!d.CheckAccess()) d.BeginInvoke(...)`，累计 120+ 行。
- **配置保存与托盘重刷调用割裂**：各模块到处充斥着 `Context?.GetService<IConfigManager>()?.SaveModuleConfig(...)` 与 `Context?.RequestTrayRefresh()` 的保护性样板代码。
- **配置初始化缺失默认实例**：由于 `ModuleBase` 未对 `Config` 提供保底初始化，导致 `AudioSwitchModule` 等通过 `_fallbackConfig` 覆盖基类属性产生额外样板代码。

### 2. 宿主菜单装配双份维护
- `TrayContextMenu.xaml.cs` 与 `FloatingPanelWindow.xaml.cs` 各自硬编码实现了宿主通用管理项（开机自启、配置目录、重载配置、语言切换、安全退出），存在代码复制与未来维护分叉风险。
- `FloatingPanelWindow.xaml.cs` 内部私有复制了 `MenuProjectionEngine` 中已有的 `AttachHoverBehavior` 与 `CloseSiblingSubmenus`。

---

## 二、重构原则与准绳

1. **框架吸收胶水，业务专注规则**：
   将所有模块千篇一律的通用生命周期（快捷键绑定、配置存取、托盘线程安全刷新、通知派发）完整收敛至 `ModuleBase<TConfig>`。
2. **零功能破坏（Non-Breaking）**：
   重构前后所有模块对外暴露的菜单项、快捷键触发行为、配置持久化格式、通知气泡完全一致。
3. **单向数据流与无状态泄漏**：
   热键注销由基类严格保证成对释放，杜绝内存泄漏和热键失效悬挂。
4. **净代码减少（Net Line Reduction）**：
   若重构后代码行数未减少或功能受损，判定为重构不合格。

---

## 三、分阶段实施路线图

### Phase 1：增强 `ModuleBase<TConfig>` 基础设施
- **`Config` 默认保底实例**：初始化为 `new TConfig()`，彻底杜绝子类 `fallbackConfig` 黑魔法。
- **声明式快捷键生命周期托管（Managed Hotkey）**：
  提供 `RegisterManagedHotkey(Func<string> hotkeyGetter, Action action)`，由基类自动负责：
  - `OnStart` 注册；
  - `OnConfigReloaded` 自动侦测按键序列变动并重绑；
  - `OnStop` / `Dispose` 自动成对注销；
  - 支持单一模块多热键注册（兼容 `MonitorProfileModule`）。
- **统一线程安全托盘与通知辅助方法**：
  - `SetTrayRoot(TrayMenuItem root)`
  - `UpdateTray(string header, string toolTip = null)` 内置 Dispatcher 安全调度；
  - `SaveConfig()` 内置统一容错调用；
  - `RequestTrayRefresh()` 内置调用。

### Phase 2：重构 6 大业务模块适配新 `ModuleBase`
- `AwakeModule`：清理私有热键管理、托盘线程切换与辅助调用。
- `AudioSwitchModule`：移除 `_fallbackConfig` 覆盖与热键/托盘胶水代码。
- `AppAutoMuteModule`：移除重复热键与托盘线程调度代码。
- `ClipboardHistoryModule`：移除热键注册胶水代码。
- `MonitorProfileModule`：将 `List<int> _registeredHotkeyIds` 替换为基类托管热键。
- `ScreenLockModule`：移除重复热键、托盘线程切换与辅助调用。

### Phase 3：收敛宿主通用管理菜单（HostMenuActions）
- 提取通用的宿主菜单逻辑组件 `HostMenuActions`，承接开机自启、配置目录、重载配置、语言切换与安全退出。
- `TrayContextMenu.xaml.cs` 与 `FloatingPanelWindow.xaml.cs` 统一接入该组件，消除两处重复逻辑与私有死代码。

---

## 四、验证标准与验收机制

1. **测试用例保障**：全量 186 项测试必须 100% 通过（0 失败，0 错误）。
2. **代码量核对**：通过 `git diff --stat` 核对重构带来的净删减行数（目标净减 300+ 行）。
3. **运行态核对**：编译输出运行，确保托盘菜单展开、动态刷新、快捷键响应、悬浮窗位置与状态无异常。
