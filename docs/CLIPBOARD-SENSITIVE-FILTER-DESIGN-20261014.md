# 剪贴板敏感信息过滤设计（2026-10-14）

## 1. 目标与范围

用户可自行维护一组关键字；复制内容只要包含其中任意关键字，**整条丢弃**——不进入内存历史，也不写入历史文件。

已确认的范围决定（本次不做的部分，避免歧义）：

| 议题 | 决定 |
|---|---|
| 默认状态 | **关闭**。新增功能不得改变既有用户的记录行为 |
| 匹配方式 | **只做字面包含（关键词）**，不支持正则 |
| 存量历史 | **不清理**。过滤只影响此后新复制的内容 |
| 掩码保存 | **不做**。命中即丢弃，不产生"打了码的副本" |
| UI 入口 | **独立窗口**，从托盘调用；历史浮窗不加入口 |
| 历史浮窗右键"加入过滤规则" | 本次不做（后续可选增强） |

## 2. 语义边界（重要）

- 只拦本模块的入库路径（`ClipboardHistoryService.RecordText`），**不修改系统剪贴板**：清空/改写剪贴板副作用过大，且会破坏用户刚复制的正常内容。
- **不回溯**：历史中此前已记录的同内容条目保持原样。用户在"关闭过滤"状态下复制过的内容，在"开启过滤"后再复制同样内容时，本次不入库，旧条目仍在列表里。
- **不改导出语义**：`ExportHistory` 导出的是当前内存历史，因此存量命中项仍会被导出。用户已知并接受（存量不清理的直接后果）。
- 规则本身以**明文**存于 `config.json`：它只是匹配用关键字，不含被拦截的内容，因此不涉及二次加密（`EncryptStorage` 只作用于 `history.json`）。

## 3. 结构

```
Models/ClipboardFilterRule.cs         规则模型（Pattern / IgnoreCase / Enabled / Id）
Models/ClipboardHistoryConfig.cs      + FilterEnabled、FilterRules、Clone() 改深拷贝
Services/ClipboardTextFilter.cs       不可变规则集，构造一次、运行期复用
Services/ClipboardHistoryService.cs   RecordText 接入过滤 + ContentFiltered 事件
Views/ClipboardFilterSettingsWindow   独立设置窗口（.xaml + .xaml.cs）
ClipboardHistoryModule.cs             托盘开关与入口、命中日志（限流）
```

### 3.1 配置字段

```csharp
public bool FilterEnabled { get; set; }                       // 默认 false
public List<ClipboardFilterRule> FilterRules { get; set; }    // 默认空
public const int MaxFilterRules = 200;
public const int MaxFilterPatternLength = 256;
```

`ClipboardHistoryConfig.Clone()` 原为 `MemberwiseClone`（浅拷贝）。设置窗口依赖它做"保存失败回滚"，规则列表若共享引用，回滚会连同新规则一起失效，因此改为深拷贝。

### 3.2 过滤引擎

`ClipboardTextFilter` 构造时把规则集编译为三个并行数组（规则、关键字、`StringComparison`），之后只做 `text.IndexOf(pattern, cmp)`：

- 不用正则：无回溯、无超时、无难以预期的误杀；
- `Ordinal` / `OrdinalIgnoreCase` 族比较，语义确定，不受区域设置影响；
- 空关键字、停用规则、`null` 规则在构造期剔除——手工编辑过的坏配置不会让模块异常；
- 规则集为不可变引用，在 `Start` / `ApplyConfig` 时重建，整个运行期复用（避免每次复制都重新解析规则）。

性能：最坏情况（200 条规则全部未命中 × `MaxTextLength` 100 万字符）仍在几十毫秒量级，与既有预览文本生成同一档次；实际用法通常只有个位数规则。

## 4. 接入点

`ClipboardHistoryService.RecordText` 是唯一入库入口，过滤插在**开关检查之后、`MaxTextLength` 截断之前**：

```
空值检查 → 模块开关 → 【敏感过滤：对原始文本匹配】 → 超长截断 → hash → 去重/MRU → 入库 → 清理规则 → 落盘
```

顺序含义：

- **必须在截断前**：否则关键字落在被截掉的尾部会漏检（`ClipboardFilterTests.ClipboardFilter_MatchesBeforeMaxTextLengthTruncation` 锁定该行为）；
- 命中后直接 `return`，**不动 `_items`、不落盘、不触发 `HistoryChanged`**——历史没有变化，无需通知 UI 或写盘。

命中时通过 `ContentFiltered(rule, textLength)` 事件交给模块层记日志。事件**只携带规则与字符数，不携带原文**：敏感内容不应经日志二次扩散。

模块层日志按规则 30 秒限流（`_filterHitLogAt`）：复制是高频动作，逐条刷屏没有价值。不弹通知——正常使用时不该被弹窗打断。

## 5. UI

托盘 `剪贴板历史` 根菜单新增（`GetTrayMenuItems`）：

- `敏感信息过滤（命中即不保存）`：勾选项，保存失败会回滚内存态并刷新托盘，保证与 `config.json` 一致；
- `敏感信息过滤设置...`：打开独立窗口 `ClipboardFilterSettingsWindow`。

独立窗口（`Height=580`，可缩放，避免把原历史设置窗口撑高）：

- 说明文字明确三条边界（不改剪贴板 / 不清理历史 / 不落盘）；
- 规则列表（`DisplayText` 派生展示文本，`[JsonIgnore]` 不入配置）+ 编辑区（关键字 / 忽略大小写 / 启用该规则）；
- 添加、更新选中、删除选中；保存前校验：关键字非空、长度 ≤256、条数 ≤200、重复（忽略大小写）。

窗口编辑期间只在本地 `List<ClipboardFilterRule>` 上操作，不直接改模块持有的配置实例——否则点"取消"也会污染内存态。保存成功后调用 `OnConfigReloaded()` 重建规则集；保存失败则按 `Clone()` 快照回滚。

## 6. 测试

`tests/CargoDesk.Tests/ClipboardFilterTests.cs`（需显式登记进 `CarroDesk.Tests.csproj`，该项目关闭了默认编译项）：

- 默认关闭；命中不记录且不落盘；未命中正常记录；
- `IgnoreCase` 双向；单条规则停用；总开关停用；`ApplyConfig` 热切换立即生效；
- 命中不删除存量条目（锁定既定语义）；
- 截断前匹配（关键字在截断点之后仍拦截）；
- 命中事件触发且只带字符数；未命中不触发；
- 空/停用/`null` 规则剔除；空规则集恒不命中；
- `Clone()` 深拷贝；配置 JSON 往返保留规则；
- 托盘菜单含两个过滤入口且默认未勾选；
- 设置窗口可实例化并显示（`TestEnvironment.RunInSta`）。

全量 `dotnet test`：391 通过 / 0 失败。

## 7. 未采用的方案与原因

- **正则规则**：能力更强（身份证/手机号/密钥格式），但需引入超时、扫描长度上限与更复杂的误杀治理。P1 先做语义确定的关键词；如后续需要，在 `ClipboardTextFilter` 内新增分支即可，规则模型加 `IsRegex` 即可扩展。
- **掩码保存**：命中片段替换为 `***` 后仍入库，UI 与配置都更复杂，且"打了码的历史"仍可能被误当作完整内容复用。
- **存量清理**：需要一次全量扫描 1000 条 × 规则数，并处理置顶项，用户明确不需要。
- **历史浮窗右键"加入过滤规则"**：降低配置门槛最有效的手段，但会改动 `ClipboardHistoryWindow` 的右键菜单与详情窗口选区交互，本轮按用户要求不动。
- **DPAPI 加密规则列表**：规则本身不是被保护的秘密；若将来要加密，需另设标记（`ClipboardStorageCrypto.IsEncryptedText` 依赖 magic 头，直接复用会与历史文件格式混淆）。
