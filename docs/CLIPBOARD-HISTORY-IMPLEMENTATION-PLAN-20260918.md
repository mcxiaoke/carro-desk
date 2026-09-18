# 剪贴板历史模块（ClipboardHistory）实施方案

> 创建日期：2026-09-18  
> 状态：待评审实施  
> 遵循规范：`docs/MODULE-DEVELOPMENT-GUIDE.md` (v1.0)  

---

## 1. 模块需求与功能规格

### 1.1 核心功能需求
1. **自动监听文本复制**：
   - 进程后台常驻监听 Windows 系统剪贴板变更事件（`WM_CLIPBOARDUPDATE`）。
   - 自动获取最新复制的纯文本并排重加入历史记录栈顶。
2. **文本自动截断**：
   - 大于 `MaxPreviewChars`（默认 100 字，常量可配置）的内容在展示预览时自动截断，并保留全文以便重新复制。
3. **滚动清理与生命周期管理**：
   - 最大条数限制 `MaxItems`（默认 1000 条，常量可配置），超过时自动剔除最旧条目。
   - 保留天数限制 `RetentionDays`（默认 90 天，常量可配置），超过设定天数的过期条目在启动及添加时自动清理。
4. **全局快捷键唤出**：
   - 支持全局热键（默认 `Win+Alt+V`，常量可配置）快速唤出剪贴板历史浮动窗口。
5. **交互与回写复制**：
   - 浮动窗口支持即时搜索过滤、键盘上下键导航、`Enter` 或鼠标双击选中条目。
   - 双击/回车后将完整内容重新写入剪切板，并自动关闭/隐藏浮窗。
   - **防自环死循环**：双击回写时，必须主动抑制自身监听器，防止回写操作被作为新条目重复记录。
6. **托盘菜单集成**：
   - 遵循指南“托盘单一根节点二级收敛契约”，提供：当前记录总数展示、打开历史窗口、清空全部历史记录、设置等子项。

---

## 2. 架构设计与工程目录

模块完全物理收敛在 `src/Modules/ClipboardHistory/`：

```text
src/Modules/ClipboardHistory/
├── ClipboardHistoryModule.cs                     # 模块入口，继承 ModuleBase<ClipboardHistoryConfig>
├── Models/
│   ├── ClipboardHistoryConfig.cs                # 强类型配置模型（存储于 config.json）
│   └── ClipboardItem.cs                         # 剪贴板历史业务实体模型
├── Services/
│   ├── IClipboardListener.cs                    # 剪贴板事件监听抽象（解耦 Win32 便于单测）
│   ├── Win32ClipboardListener.cs                # 基于 NativeWindow 的 Win32 消息泵监听器
│   ├── IClipboardHistoryStorage.cs              # 独立业务数据存储抽象
│   ├── JsonClipboardHistoryStorage.cs           # data/ClipboardHistory/history.json 异步持久化
│   ├── IClipboardHistoryService.cs              # 核心业务服务接口
│   ├── ClipboardHistoryService.cs               # 截断、去重、双击回写抑制、过期清理逻辑
│   └── ClipboardHelper.cs                       # 包含 STA 调度与 COM 锁竞争 3 次指数退避重试
└── Views/
    ├── ClipboardHistoryWindow.xaml              # 类似 Listary/Win+V 风格的快捷浮窗
    └── ClipboardHistoryWindow.xaml.cs           # 失焦隐藏、键盘导航、搜索过滤、双击回写
```

---

## 3. 详细设计与实现细节

### 3.1 配置与业务数据严格隔离
- **配置模型**（写入 `config.json` 中的 `"ClipboardHistory"` 节点）：
  ```json
  "ClipboardHistory": {
    "Enabled": true,
    "Hotkey": "Win+Alt+V",
    "MaxPreviewChars": 100,
    "MaxItems": 1000,
    "RetentionDays": 90
  }
  ```
- **业务数据持久化**（独立文件，避免污染主配置）：
  - 路径：`Path.Combine(ConfigService.DirPath, "data", "ClipboardHistory", "history.json")`
  - 格式：纯 JSON 数组，每个条目包含 `Id`, `FullText`, `PreviewText`, `TextLength`, `CopiedAt`, `Hash`。
  - 存储策略：在内存中维持 List，每次变更触发防抖异步写入临时文件后原子替换（Atomic Replace），确保持久化安全。

### 3.2 Win32 原生监听与 STA 异常隔离
- **Win32 API**：
  - `AddClipboardFormatListener(IntPtr hWnd)`
  - `RemoveClipboardFormatListener(IntPtr hWnd)`
- **NativeWindow 消息泵**：
  - 继承 `System.Windows.Forms.NativeWindow` 创建隐藏消息句柄，捕获 `WM_CLIPBOARDUPDATE (0x031D)`，触发 `ClipboardUpdated` 事件。
- **剪贴板访问保护（`ClipboardHelper`）**：
  - 剪贴板读取通过 `Context.Dispatcher` 确保在 STA 线程运行。
  - 加入重试机制（循环 3 次，间隔 50ms），捕获处理 `COMException (CLIPBRD_E_CANT_OPEN, 0x800401D0)`。

### 3.3 截断、去重与自动清理规则（`ClipboardHistoryService`）
1. **自动截断**：
   - 若文本长度 `> Config.MaxPreviewChars`，提取前 100 字并附加 `...` 生成 `PreviewText`；
   - 剔除换行符用于单行列表预览，保留完整的 `FullText` 用于回写。
2. **去重与顶置**：
   - 使用 MD5 计算文本哈希。若当前复制内容已在历史中存在，更新其 `CopiedAt` 时间并提升到列表首位（MRU 机制），不重复新增。
3. **清理规则（双重淘汰）**：
   - 规则 A（过期淘汰）：`CopiedAt < DateTime.Now.AddDays(-Config.RetentionDays)` 的条目予以移除。
   - 规则 B（超额淘汰）：若列表条数超过 `Config.MaxItems`，裁剪尾部超出部分。
4. **防自环循环抑制（Self-Feedback Suppression）**：
   - 当用户在窗口双击某项回写剪贴板前，设置 `_suppressedHash = item.Hash`；
   - 当紧接着收到系统 `WM_CLIPBOARDUPDATE` 时，比对哈希一致则主动忽略并重置标记，防止用户重新复制旧条目时生成重复记录。

### 3.4 快捷浮窗交互设计（`ClipboardHistoryWindow`）
- **无边框阴影小窗**：宽度 420px，高度 520px，位于鼠标光标右下方或主屏幕居中。
- **搜索框**：顶部文本框支持实时过滤历史条目。
- **键盘导航**：
  - `Up` / `Down`：切换选中项；
  - `Enter`：将当前选中项回写剪切板并关闭隐藏；
  - `Esc`：直接隐藏浮窗；
- **失焦关闭**：
  - 注册 `Window.Deactivated` 事件，用户切换到其他软件时自动 `Hide()`。
- **生命周期优化**：
  - 模块常驻维护单例窗口，快捷键唤起时调用 `ShowAndActivate()`，关闭时仅 `Hide()`，避免重复销毁创建 HWND。

### 3.5 托盘菜单设计（二级收敛契约）
```csharp
public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
{
    var root = new TrayMenuItem
    {
        Id = "clipboard_root",
        Header = $"{Name} ({_service?.Count ?? 0})",
        ToolTip = $"{Name} (快捷键: {Config.Hotkey})"
    };

    root.Children.Add(new TrayMenuItem
    {
        Id = "clipboard_open",
        Header = Loc.T("Clipboard.OpenWindow", "打开剪贴板历史..."),
        ClickAction = SummonWindow
    });

    root.Children.Add(new TrayMenuItem
    {
        Id = "clipboard_toggle",
        Header = Loc.T("Clipboard.AutoRecord", "自动记录复制内容"),
        IsChecked = Config.Enabled,
        ClickAction = ToggleEnabled
    });

    root.Children.Add(TrayMenuItem.Separator());

    root.Children.Add(new TrayMenuItem
    {
        Id = "clipboard_clear",
        Header = Loc.T("Clipboard.ClearAll", "清空历史记录"),
        ClickAction = ClearHistory
    });

    yield return root;
}
```

---

## 4. 单元测试策略与验证

新建 `tests/CarroDesk.Tests/ClipboardHistoryModuleTests.cs`，涵盖脱机与逻辑全场景：

1. **`ClipboardHistory_TruncationRule_TruncatesPreview_PreservesFullText`**：
   - 验证超过 100 字文本的自动截断为 100 字 + "..."，同时完整文本保持不变。
2. **`ClipboardHistory_MaxItemsRetention_EvictsOldestItems`**：
   - 设定 MaxItems = 3，连续写入 5 条，断言历史总数严格为 3，且保留最新 3 条。
3. **`ClipboardHistory_ExpirationRetention_RemovesExpiredItems`**：
   - 写入超过 90 天的时间戳条目，验证清理后自动移除。
4. **`ClipboardHistory_Deduplication_MovesExistingToTop`**：
   - 重复录入相同文本，验证不产生新条目，并将已有项更新时间戳提升至索引 0。
5. **`ClipboardHistory_SelfFeedbackLoop_IsSuppressed`**：
   - 测试通过 `SuppressNext()` 模拟双击回写时，不触发重新入库。
6. **`ClipboardHistory_TrayMenu_SingleRootContract`**：
   - 验证托盘菜单严格输出 1 个根节点，且包含必要的控制子项。
7. **`ClipboardHistory_Config_DirectSerializationRoundTrip`**：
   - 验证 `ConfigManager` 对 `ClipboardHistoryConfig` 原生配置的读写往返无损。

---

## 5. 实施分步计划（SOP 对照）

- **步骤 1**：创建目录 `src/Modules/ClipboardHistory/`（`Models`, `Services`, `Views`）。
- **步骤 2**：编写 `Models/ClipboardHistoryConfig.cs` 与 `Models/ClipboardItem.cs`。
- **步骤 3**：实现 `Services/`：
  - `IClipboardListener.cs` 与 `Win32ClipboardListener.cs`
  - `IClipboardHistoryStorage.cs` 与 `JsonClipboardHistoryStorage.cs`
  - `ClipboardHelper.cs`（STA 调度与重试）
  - `IClipboardHistoryService.cs` 与 `ClipboardHistoryService.cs`
- **步骤 4**：编写 `Views/ClipboardHistoryWindow.xaml` 与 `.xaml.cs`。
- **步骤 5**：编写 `ClipboardHistoryModule.cs`，实现生命周期、热键绑定、托盘菜单。
- **步骤 6**：在多语言文件 `zh-CN.json`、`en-US.json` 中增加对应键值。
- **步骤 7**：在 `App.xaml.cs` 注册新模块，并在 `config.sample.json` 中追加样例配置。
- **步骤 8**：编写并运行 `ClipboardHistoryModuleTests.cs`，确保所有自动化测试 100% 通过。
