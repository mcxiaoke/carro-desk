# ScreenLock — 中英文双语国际化 (i18n) 最佳设计与实施方案

> 日期：2026-09-16  
> 目标：为 ScreenLock 增加优雅、免重启即时刷新、零依赖且可自由扩展的中英文多语言体系。

---

## 1. 架构目标与约束分析

1. **单文件与打包兼容**：
   - 当前项目基于 .NET Framework 4.8 + Costura.Fody，输出单个独立绿色 `.exe`。
   - **严禁使用微软默认的卫星程序集（`zh-CN/ScreenLock.resources.dll`）方案**：避免 MSBuild 隐式文化编译机制与 Costura 产生程序集探测与打包冲突。
2. **即时动态切换（无需重启）**：
   - 常驻托盘软件必须支持在托盘菜单或配置窗口切换语言后，所有已打开和后续打开的界面文本**秒级无缝自动刷新**。
3. **C# 业务与弹窗全覆盖**：
   - 项目约有近半数用户可见文本位于 C# 逻辑层（`MessageBox.Show`、`ShowBalloonTip`、托盘悬停 Tooltip、表单校验提示等），必须提供一等公民的 C# API（如 `Loc.T("key")` 与参数化格式化）。
4. **扩展性（支持第三方外挂语言包）**：
   - 默认将 `zh-CN.json`（简体中文）与 `en-US.json`（英文）作为**嵌入式资源**打包入 exe，保证单文件开箱即用。
   - 同时支持在 `%AppData%\ScreenLock\lang\` 或便携目录 `app_data\lang\` 中放置第三方 `*.json`（如 `ja-JP.json`, `zh-TW.json`），启动时自动扫描挂载，无需重新编译主程序。

---

## 2. 核心架构设计

整个国际化体系由 4 个核心构件组成：

```
ScreenLock/
├── Assets/Locales/
│   ├── zh-CN.json            [嵌入式资源] 简体中文全量字典
│   └── en-US.json            [嵌入式资源] 英文全量字典
├── Services/Localization/
│   ├── I18nService.cs        [核心管理器] 字典加载、动态语言切换、回退、外部语言包扫描、INotifyPropertyChanged
│   ├── LocExtension.cs       [XAML标记扩展] 语法糖 {loc:Loc Key}，绑定单例索引器
│   └── Loc.cs                [C#门面] 提供 Loc.T(key) 与 Loc.T(key, args...) 静态访问
```

### 2.1 语言文件格式（JSON 结构化）

采用清晰的命名空间分组，支持嵌套扁平化解析（例如 `Tray.LockNow`）：

```json
{
  "_meta": {
    "code": "zh-CN",
    "name": "简体中文"
  },
  "Common": {
    "Ok": "确定",
    "Cancel": "取消",
    "Save": "保存",
    "SaveAndApply": "保存并应用",
    "Close": "关闭",
    "ResetDefaults": "恢复默认",
    "Error": "错误",
    "Prompt": "提示",
    "Success": "成功",
    "Warning": "警告"
  },
  "Tray": {
    "Running": "运行中",
    "Locked": "已锁定",
    "Paused": "已暂停",
    "Disabled": "已禁用",
    "LockNow": "立即锁定",
    "IdleLock": "空闲锁定",
    "PauseTimer": "暂停计时",
    "AutoStart": "开机自启",
    "ConfigEditor": "配置编辑器...",
    "TaskEditor": "任务编辑器...",
    "Language": "语言 / Language",
    "LangAuto": "自动跟随系统 (Auto)",
    "Exit": "退出...",
    "BalloonIdleWarn": "空闲 {0} 分钟后自动锁定，动一下鼠标可取消",
    "BalloonPauseFormat": "已暂停自动锁定，{0:HH:mm} 后恢复"
  },
  "Lock": {
    "Title": "屏幕已锁定",
    "InputPinHint": "输入 PIN 码解锁",
    "Unlock": "解 锁",
    "CapsLockWarning": "⚠️ 大写锁定 (Caps Lock) 已开启"
  }
}
```

### 2.2 响应式服务（I18nService）
- 继承 `INotifyPropertyChanged`，对外暴露索引器：
  ```csharp
  public string this[string key] => Get(key);
  ```
- 切换语言时触发：
  ```csharp
  PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
  ```
- **Fallback 策略**：
  1. 当前语言查找词条；
  2. 若未找到，回退到默认英文/中文词条；
  3. 若均未找到，返回 `Key` 原文，绝不抛出异常或留白。

### 2.3 XAML 标记扩展（LocExtension）
WPF 界面通过声明式语法绑定到 `I18nService.Instance`：
```xml
<!-- 窗体 / 菜单 / 按钮使用示例 -->
<MenuItem Header="{loc:Loc Tray.LockNow}" Click="OnLockNowClick"/>
<Button Content="{loc:Loc Common.ResetDefaults}" Click="OnResetDefaultsClick"/>
```

### 2.4 C# 代码层调用（Loc）
后台逻辑中直接调用静态门面：
```csharp
MessageBox.Show(Loc.T("Config.SaveSuccess", ConfigService.FilePath), Loc.T("Common.Success"), MessageBoxButton.OK, MessageBoxImage.Information);
```

---

## 3. 配置与系统语言匹配规则

在 `AppSettings.cs` 中增加：
```csharp
public string Language { get; set; } = "auto";
```

- `"auto"`：启动时读取 `CultureInfo.CurrentUICulture`。若为 `zh-*`（中文系）则激活 `zh-CN`，其余所有区域默认回退至 `en-US`。
- `"zh-CN"` / `"en-US"`：用户显式指定语言。
- 托盘右键菜单增加一级或二级菜单 `语言 / Language`，可实时打勾切换。

---

## 4. 关键排坑与适配要点

1. **界面文本膨胀适配**：
   - 英文文本长度普遍比中文长 30%~60%。
   - `TaskEditorWindow` 左侧的按钮（“新增”、“复制”、“删除”）原先固定 `Width="64"`，需改为 `MinWidth="64"` 或自适应 padding，防止英文被裁切。
   - `ConfigEditorWindow` 的基础设置左栏 Label 宽度由固定的 `Width="80"` 调整为 `Auto` 或合理弹性宽度。
2. **锁屏大日期本地化**：
   - 锁屏界面的年月日与星期格式使用 `DateTime.Now.ToString(Loc.T("Lock.DateFormat"), I18nService.Instance.CurrentCulture)`。
   - 中文显示：`2026年9月16日 星期三`；英文显示：`Wednesday, September 16, 2026`。
3. **不可翻译项边界**：
   - 任务触发器底层标识（`startup`, `interval`, `daily`, `lock`, `unlock`）保持代码常量不变，仅对下拉框 UI 显示文案做翻译。
   - 日志文本（`task-*.log`、`log.txt`）保持程序级英文记录，确保工具链与解析器兼容。

---

## 5. 实施里程碑

- **M1：基础设施构建**：字典资源、`I18nService`、`LocExtension`、`Loc` 类创建，与 `AppSettings` 持久化集成。
- **M2：高频主界面迁移**：锁屏界面 (`LockWindow`)、向导 (`FirstRunWindow`)、验证弹窗 (`VerifyPinWindow`)、托盘菜单 (`TrayContextMenu`) 迁移。
- **M3：复杂编辑器迁移**：配置编辑器 (`ConfigEditorWindow`) 与任务自动化编辑器 (`TaskEditorWindow`) 迁移及布局抗挤压适配。
- **M4：后台业务逻辑与验证**：C# 中的弹窗、气泡提示迁移，中英文双语全流程功能回归与单文件构建验证。
