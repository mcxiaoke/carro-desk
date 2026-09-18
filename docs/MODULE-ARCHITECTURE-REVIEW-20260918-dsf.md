# CarroDesk 模块架构方案审查意见（2026-09-18）

> **审查对象**：`docs/MODULE-ARCHITECTURE-PROPOSAL-20260918.md`（v3.1 Architecture Proposal）
> **审查基线**：`git a16fb65`（2026-09-18 17:05:13 +0800）；工作区另含未提交的 `CODE-REVIEW-20260918-bd.md`、`CODE-REVIEW-20260918-gm.md`、`FIX-PLAN-20260918.md`
> **审查视角**：业界模块化标准（Modular Monolith / Vertical Slice 的边界强制与契约面实践）+ 存量项目落地可行性
> **审查方式**：所有论断以源码、构建配置、测试工程实读为依据，不采信方案文档的自述；对无法实机验证的部分显式标注证据限制

---

## 0. 总体结论

**方案方向正确，问题诊断基本属实，但不足以按原文一次性实施。**

三点判断：

1. **诊断质量高**。第 2 章列出的 5 类痛点（配置"一国两制"、物理包结构割裂、菜单投影重复、通知与 DI 半截子、设置中心无扩展点）经逐条核对，**全部真实存在**，且有明确代码证据。第 4 章的 5 个方案指向的也是真实问题。
2. **存在 1 处实质性误判 + 4 处关键执行缺口**。误判是 §1.2 与 §6.3 把 `ViewLayer_HasNoStaticAppReferences` 当作"永久回归保护"，该测试在当前仓库状态下**恒为空过（零覆盖）**（详见 §2.1）。执行缺口集中在配置迁移白名单、PIN 归属、XAML 命名空间策略、测试工程登记机制——这四项任一处理不当都会导致"编译通过但功能回退"或"静默丢配置"。
3. **与业界标准存在一处结构性差距**：本项目是**单程序集**，编译器可见性这一层（业界公认最强的一层）完全用不上，模块边界只能靠架构测试的文本扫描兜底。方案没有讨论这个根本约束，却使用了"微内核扩展点""真·自治"这类暗示强边界的措辞，容易让后续执行者高估边界的实际强度。

**建议**：按 §6 的修订路线图分 6 个阶段（原 4 阶段拆分 + 顺序调整）实施，并把 §5 列出的 9 项条款在开工前修正。**不建议**按原文的 4 阶段一次性推进，主要因为 Phase 4 把三件互不独立的高风险变更捆在一起，且缺少配置双读过渡与回滚路径。

---

## 1. 事实核对：提案论断逐条验证

| # | 提案论断 | 实测结果 | 判定 |
|---|---|---|---|
| 1 | 最近 7 个 commit `b4a6687`~`a16fb65` | `git log` 完全一致，7 条 commit message 与提案文本逐字吻合 | ✅ 准确 |
| 2 | 76 项单测 | `grep -c "\[TestMethod\]"` = **76** | ✅ 准确 |
| 3 | 视图层已彻底消除 `App.*` 静态门面 | `grep "App\.\(Config\|Modules\|Services\|CurrentApp\|IsShuttingDown\)" src/Views/` → **0 命中**（`temp/backups/` 下的旧副本除外，不参与编译） | ✅ 准确 |
| 4 | `ConfigEditorWindow` 等 6 个视图已改依赖注入 | `TrayContextMenu` 构造函数注入 `ConfigManager/ServiceContainer/Action*`；`ConfigEditorWindow(IPinService, ConfigManager, Action)` | ✅ 准确 |
| 5 | `ModuleManager.StartAll` 有 `Initialized` 状态守卫 | `ModuleManager.cs:90` `if (module.Status != ModuleStatus.Initialized) continue;` | ✅ 准确 |
| 6 | 托盘菜单过滤 `Faulted` 模块 | `ModuleManager.cs:163`；`DynamicTrayController` 另有 `GetItemsGuarded` | ✅ 准确 |
| 7 | `TaskSchedulerService` 已剥离全局 `App` 依赖 | 构造函数为 `(IIdleService, IConfigManager, INotificationService, Action<string>)`，无 `App` 引用 | ✅ 准确 |
| 8 | §2.1 `ScreenLock`/`TaskScheduler` 配置寄生在 `AppSettings` | `AppSettings.cs:10-21` 含 `IdleMinutes/ShowClock/OverlayOpacity/PinHash/PinSalt/ExcludeProcesses/TasksEnabled/UnlockOnResume` | ✅ 准确 |
| 9 | §2.1 两模块构造函数强注入宿主私有 `ConfigService` | `ScreenLockModule.cs:41-48`、`TaskSchedulerModule.cs:27-33` | ✅ 准确 |
| 10 | §2.1 `ConfigService` 残留 `IsCoreAppSetting` 与 `AudioSwitchJson` | `ConfigService.cs:208-232`（16 项硬编码）、`:72-75`（4 个字符串槽） | ✅ 准确，但**低估范围**（见 §3.1） |
| 11 | §2.2 `HotkeyService.cs` 错置于 `src/Services/Tasks/`，命名空间 `CarroDesk.Services.Tasks` | 属实；`HotkeyTrigger.cs:24,45` 直连 `HotkeyService.Instance` | ✅ 准确 |
| 12 | §2.2 `src/Services/Tasks/*` 共 10 个文件 | `ls` = 10 个 `.cs`（`Triggers/` 子目录 9 个另计，合计 19） | ✅ 准确 |
| 13 | §2.2 `LockController.cs`、`KeyboardBlocker.cs` 滞留在 `src/Services/` | 属实 | ✅ 准确 |
| 14 | §2.2 5 个模块设置窗口平铺在 `src/Views/` | `AppAutoMuteSettingsWindow`、`AwakeSettingsWindow`、`MonitorProfileSettingsWindow`、`TaskEditorWindow`、`LockWindow` 均在 `src/Views/` | ✅ 准确 |
| 15 | §2.3 两处 `CreateVisual` 递归构建逻辑完全重复 | `DynamicTrayController.cs:266-319` vs `FloatingPanelWindow.xaml.cs:566-618`，绑定集合一致 | ✅ 准确（提案写的 `250-320` 行号有偏移，指向区域正确） |
| 16 | §2.3 两处均未提供注销机制 | `DynamicTrayController.cs:306`、`FloatingPanelWindow.xaml.cs:612` 的 `Children.CollectionChanged` 订阅均无退订 | ⚠️ 表述不精确（见 §3.4） |
| 17 | §2.4 5 个模块各自声明 `NotificationCallback`/`BalloonNotifier` | `ScreenLock=BalloonNotifier` + 其余 4 个 `NotificationCallback`，共 5 处 | ✅ 准确 |
| 18 | §2.4 `App.xaml.cs` 5 处手工委托注入 | `App.xaml.cs:142,153,159,165,171` | ✅ 准确 |
| 19 | §2.4 各模块保留 `public static XxxModule Instance` | 6 个模块全部命中（含 `TaskSchedulerModule`） | ✅ 准确 |
| 20 | §2.5 `ConfigEditorWindow` 死绑模块字段 | `.xaml.cs:61-86, 214-220, 262-279` 直读写 `IdleMinutes/ShowClock/OverlayOpacity/UnlockOnResume/TasksEnabled/ExcludeProcesses` | ✅ 准确 |
| 21 | §3 模块间禁止互引 | 6 个模块之间**确实无** `using CarroDesk.Modules.*` 互引 | ✅ 准确（但存在宿主侧反向依赖，见 §3.5） |
| 22 | §5 Phase 1 "零破坏" | 含 `MenuProjectionEngine` 抽取，而该抽取涉及行为差异（见 §5.2），非零破坏 | ❌ 分类不准 |

**核对结论**：22 项论断中 20 项准确、1 项表述不精确、1 项分类不准。**方案的现状调研是可信的**，这在一份架构提案里是难得的。问题不在诊断，在方案与执行细节。

---

## 2. 实质性误判

### 2.1 🔴 P0 · `ViewLayer_HasNoStaticAppReferences` 当前恒为空过，不构成回归保护

提案 §1.2 称该测试"对视图层静态依赖建立永久回归保护"，§6.3 又把架构断言列为质量卡点。**实测该测试无效。**

`tests/CarroDesk.Tests/ConfigAndTriggerTests.cs:161-169`：

```csharp
string solutionRoot = AppDomain.CurrentDomain.BaseDirectory;
while (!string.IsNullOrEmpty(solutionRoot) && !File.Exists(Path.Combine(solutionRoot, "CarroDesk.sln")))
{
    var parent = Directory.GetParent(solutionRoot);
    if (parent == null) break;
    solutionRoot = parent.FullName;
}
string viewsDir = Path.Combine(solutionRoot, "src", "Views");
if (Directory.Exists(viewsDir))   // ← 为 false，整段断言被跳过
```

而仓库**不存在 `CarroDesk.sln`**，只有 `CarroDesk.slnx`：

```
$ ls -la *.sln *.slnx
-rw-r--r-- 1 mcxiaoke 197121 65 Sep 16 19:06 CarroDesk.slnx
$ find . -maxdepth 3 -name "*.sln"      # 空
```

**推导链**（封闭逻辑）：测试输出目录为 `tests/CarroDesk.Tests/bin/Debug/net48/`，逐级上溯 `net48 → Debug → bin → CarroDesk.Tests → tests → CarroDesk → Projects → Home → C:\`，全程无 `CarroDesk.sln`；`Directory.GetParent("C:\")` 返回 `null` → `break`。此时 `solutionRoot = "C:\"`，`viewsDir = "C:\src\Views"`，`Directory.Exists` 为 `false`，**`if` 块整体跳过，测试无任何断言即通过**。

**影响**：
- 提案视为"已完成收益"的 P1-2（静态门面清零）**没有任何自动保护**。当前 `grep` 结果为 0 是人工核查的结论，不是测试保障的结论——这两者在后续 5 个阶段的多次目录迁移中差别巨大。
- 提案 §6.3 "扩充 `ArchitectureTests.cs`" 的基座不存在：`ArchitectureTests.cs` **在仓库中不存在**（`find . -name "*Architecture*"` 无结果），该测试实际内联在 `ConfigAndTriggerTests.cs` 末尾。提案引用了一个不存在的文件。

**修正要求**（必须在任何迁移开工前完成）：
1. 把 `CarroDesk.sln` 改为 `CarroDesk.slnx`，或改用其他可靠的根定位（如从 `Assembly.Location` 上溯查找 `.git` 目录 / `Directory.Build.props`），**并让定位失败时 `Assert.Fail` 而非静默跳过**——"找不到根就当通过"是这类测试最典型的失效模式。
2. 扫描范围从 `src/Views` 扩展为 `src/**`，按"目录白名单"排除 `Host/`、`Core/`，而不是按"仅扫描 Views"。否则 §2.2 把 5 个窗口迁出 `src/Views` 后，保护面会静默缩到 0。
3. 同理，`tests/CarroDesk.Tests/CarroDesk.Tests.csproj` 设置了 `EnableDefaultCompileItems=false` 并**逐文件显式 `<Compile Include>`**。新增测试文件若不同步登记，会**静默不参与编译**（无告警）。提案 §6 计划新增 `MenuProjectionEngineTests`、`ConfigSeparationTests`，但未提这条登记要求——这是本项目特有的、极易踩的坑。

> **证据限制**：审查环境未安装 `dotnet` CLI（`dotnet --version` → command not found），未能实机运行测试套件。上述结论基于构建配置与路径逻辑的确定性推导，**建议实施前用一次实机运行确认**（预期该测试显示"通过"但无断言执行）。

---

## 3. 关键执行缺口（提案未覆盖或覆盖不足）

### 3.1 🔴 P0 · 配置迁移缺"白名单"替代方案，直接删 `IsCoreAppSetting` 会污染配置

提案 §4 方案 1 要求"`ConfigService` 移除 `AudioSwitchJson`、`IsCoreAppSetting` 等所有模块硬编码"。方向正确，但 `IsCoreAppSetting` **正是当前的兼容层**，不是纯粹的垃圾代码：

`ConfigService.cs:167-180` 用该字典决定"哪些顶层字段不落入 `_moduleConfigs`"。若直接删除：

```csharp
foreach (var prop in obj.Properties())
{
    if (IsCoreAppSetting(prop.Name)) continue;   // ← 删掉这行
    _moduleConfigs[prop.Name] = val;             // 于是 IdleMinutes/PinHash/AutoStart… 全被当作"模块配置"收进来
}
```

`Save()`（`:296-299`）再把 `_moduleConfigs` 整体写回根对象 → **旧配置的每个扁平字段都会以"模块配置"身份被重复写回**，同时 `Modules` 段出现以字段名（而非模块名）为键的垃圾节点。

**要求**：迁移必须写成显式的、可测试的两步流程，并落成代码而非文档承诺：
1. **识别**：`Host` 段字段白名单（`Language/AutoStart/FloatingPanel*/PinSalt/PinHash`）+ `Modules` 段键白名单（6 个模块 Id）；
2. **搬迁**：检测到扁平字段 → 按映射表写入新位置 → 原子写回 → 保留 `.migrated-{timestamp}` 备份；
3. **幂等**：迁移后再次启动不得重复迁移；`Host`/`Modules` 双存在时以 `Modules` 为准；
4. **失败可回退**：迁移中途异常不得写坏原文件（现有 `AtomicFile` + `config.corrupt-*` 机制可复用）。

**提案完全没有给出这个映射表**，而它是整个 Phase 4 的成败点。

### 3.2 🔴 P0 · PIN 归属自相矛盾，会破坏退出守卫

提案 §4 方案 1 的目标 JSON 把 PIN 放在 `Host.Security.PinSalt/PinHash`，`Modules.ScreenLock` 段**不含 PIN**。但：

- `ScreenLockConfig.cs:8-9` 仍定义 `PinHash`/`PinSalt`；
- `ScreenLockModule.RegisterConfig` 适配器读写 `s.PinHash`/`s.PinSalt`（`:59-60, 73-74`）；
- **`ScreenLockModule.RequestBlockExit()`（`:256`）用 `Config.PinHash/PinSalt` 判断是否阻止退出**。

若只把存储位置改到 `Host.Security` 而不改 `ScreenLockConfig` 与 `RequestBlockExit`，退出守卫会**永远读到空 PIN → 永远不阻止退出**（安全功能静默失效，无异常、无日志）。反之若保留 `ScreenLockConfig.PinHash`，则 PIN 在配置里存两份，出现"改 PIN 后一处生效一处不生效"。

**要求**：PIN 单一归属 `Host.Security`（与 `HostPinService` 一致），`ScreenLockConfig` 删除两个字段，`RequestBlockExit` 改经 `IPinService.IsConfigured` 判断（该接口已有此成员，`HostPinService.cs:25-28` 已实现）。**提案需把这条列为 Phase 4 的显式前置项**，并配一条回归测试（"有 PIN 时退出被阻止 / 无 PIN 时放行"）。

### 3.3 🟠 P1 · XAML 迁移的命名空间策略缺失（决定工作量与风险）

提案 §2.2 要把 5 个 `*Window.xaml/.xaml.cs` 从 `src/Views/` 迁到 `src/Modules/*/Views/`，但**没有给出命名空间策略**，而这决定了两种完全不同的工作量与风险：

| 策略 | 改动量 | 后果 |
|---|---|---|
| **A. 保留 `CarroDesk.Views` 命名空间**，只移文件 | 每个文件改 0~1 行 | 目录与命名空间不一致，破坏"目录即命名空间"约定，未来更难用命名空间做架构断言 |
| **B. 改为 `CarroDesk.Modules.Xxx.Views`** | 每个窗口改 `x:Class`、`.xaml.cs` 的 `namespace`、以及**所有引用该窗口的调用点** | 一致性最好；`{loc:Loc}` 等 `xmlns:clr-namespace:CarroDesk.Services.Localization` 引用不受影响（指向 Services 而非 Views），但需逐一核对 |

补充事实：`src/CarroDesk.csproj` **未设置** `EnableDefaultCompileItems=false`，也未显式 `Page Include`，走 SDK 通配——所以**移动 XAML 文件本身不会漏编译**（这一点比测试工程友好）。真正的工作量在命名空间与调用点（`App.xaml.cs`、`TrayContextMenu.xaml.cs`、各模块、`TaskEditorWindow` 等）。

**要求**：提案明确选 B（推荐，与"垂直切片自治"目标一致），并列出受影响的调用点清单；若选 A，需在文档中说明这是刻意的过渡妥协及退出条件。

### 3.4 🟡 P2 · `MenuProjectionEngine` 抽取会引入 4 项行为回退

提案 §2.3 的示例代码是**功能子集**，直接照搬会回退现有能力：

| 现有能力 | 位置 | 提案示例 | 后果 |
|---|---|---|---|
| `Separator` 分支 | `DynamicTrayController.cs:272-275`、`FloatingPanel.cs:569` | **示例中缺失** | 分隔线渲染为空白 `MenuItem` |
| `Children.CollectionChanged` → 触发重建 | `DynamicTrayController.cs:306`、`FloatingPanel.cs:612` | **示例中完全消失** | 动态子项（任务列表、音频设备列表、最近运行记录）**不再刷新** |
| `ClickAction` 异常处理 | `DynamicTrayController.OnToggleClick:250-262` 记日志 + 气泡通知 + `_nodeOwner` 归属 | 示例 `catch { }` **静默吞** | 用户点击无反馈、故障不可诊断，**低于现状** |
| hover 自动展开 / 关闭同级 / 关闭后收面板 | `FloatingPanel.AttachHoverBehavior:620-658`、`CloseAllTopLevelSubmenus`、`DismissIfNotPinned` | 示例仅 `AttachHoverBehavior` 骨架 | 浮层面板交互退化 |

另外两处 `CreateVisual` **并非"完全相同的递归构建逻辑"**：`DynamicTrayController` 额外有 `IsOpen` 延迟重建（`:91-113`）、`TrimEdgeSeparators`/`CollapseConsecutiveSeparators`（`:333-364`）、后台线程 `PropertyChanged` 检测（`:309-316`）；`FloatingPanel` 额外有面板关闭逻辑。**抽取必须把差异显式参数化**（如传入 `onItemExecuted`、`errorHandler`、`hoverPolicy` 委托），否则就是把一个可维护性问题换成一组行为回归。

顺带修正 §2.3 的表述：`DynamicTrayController` **并非"完全没有 Detach"**——`DetachVisual`（`:216-240`）会 `Click -=`、`ClearAllBindings`、`Items.Clear()`；缺的是 `CollectionChanged`/`PropertyChanged` 的**事件订阅退订**。这个区别在写修复时很重要（不是从零加机制，而是补两行退订）。

### 3.5 🟠 P1 · 迁移清单不完整，漏掉 9 项"灰色资产"

提案 §2.2 的迁移表只覆盖了最显眼的文件，以下资产**归属未定**，实施时必然卡住：

| 资产 | 现位置 | 被谁用 | 问题 |
|---|---|---|---|
| `TaskDefinition.cs` | `src/Models/` | 任务模块全家 + `TaskEditorWindow` + 3 个测试 | 提案表把它并入 `Services/`，但它是**领域模型**，应进 `Models/`（与提案自己的架构图矛盾） |
| `IdleDetector.cs` | `src/Services/`（static） | **`TaskConditionEvaluator`、`IdleTrigger`**（任务模块）；`ScreenLockModule` 仅"复刻语义" | 实际服务对象是 **TaskScheduler 而非 ScreenLock**，归属与提案叙述相反 |
| `ProcessExclusionService.cs` | `src/Services/`（static） | `ScreenLockModule` + **`App.xaml.cs:244`** | 跨模块/宿主共享，归属未定 |
| `ProcessHelper.cs` | `src/Services/`（static） | AppAutoMute、Awake、ProcessExclusionService、**3 个 View**、1 个测试 | 提案一句话"留在 Services"，但它是被模块直接 static 调用的共享工具——**与"模块禁止互引/自治"存在张力，需明确定位为 SharedKernel** |
| `HotkeyHelper.cs` | `src/Services/Tasks/` | `TaskDefinition`、`HotkeyService`、`AppAutoMuteSettingsWindow`、`TaskEditorWindow` | 提案只把 `HotkeyService` 上浮 Host，**漏了 Helper** → 上浮后 `HotkeyService` 反向依赖旧 `Services/Tasks` |
| `TaskLogger.cs` | `src/Services/Tasks/`（static） | `TaskRunner`、`TaskSchedulerService` | 静态依赖 `ConfigService.LogsDirPath`（宿主静态）→ 模块迁入后仍耦合宿主 |
| `ILockService.cs` / `ILockAppearance.cs` | `src/Services/` | `LockController`、`LockWindow` | 是 ScreenLock 的契约，应随模块下沉，提案未列 |
| `IPinService.cs` / `PinService.cs` | `src/Services/` | `HostPinService`、`VerifyPinWindow`、`ConfigEditorWindow`、`LockController` | PIN 已决策下沉 Host，但 `PinService`/`PinGuard` 仍被 `LockController` 直接 `new`（`:22-24`）→ 职责重叠 |
| `StringOrStringListConverter.cs` | `src/Models/Converters/` | **仅** `AppSettings.ExcludeProcesses` | 随 `ExcludeProcesses` 迁入 ScreenLock 后，此 Converter 的归属需同步决定 |

**要求**：提案补一张"资产归属总表"，对每一项给出 `Host / Modules.Xxx / SharedKernel` 三选一，并说明 `SharedKernel` 的准入标准（建议：无状态、无 WPF 依赖、被 ≥2 个模块使用）。业界通行做法是 SharedKernel **保持极小**，`ProcessHelper` 这类"什么都能往里塞"的工具类最容易被滥用成隐性耦合通道。

### 3.6 🟠 P1 · 单程序集约束下，边界强度被高估

这是与业界标准的**结构性差距**，也是全文最重要的一条。

业界（Modular Monolith / Vertical Slice 的成熟实践）把边界强制分三层，按强度递减：

1. **编译器层**（最强）：实现类型 `internal` + 模块间只引用对方 `*.Contracts` 程序集。越界 = 编译错误 `CS0234`，**在写代码的当下就失败**，且只能通过 `.csproj` 加引用来绕过，而 `.csproj` 改动在 review 中无法忽略。
2. **架构测试层**（次强）：NetArchTest / ArchUnit 规则 + "每个模块只暴露一个 public 类型"的探测。
3. **约定层**（最弱）：命名与目录规范。

本项目：**单一 `src/CarroDesk.csproj`**。因此第 1 层**完全不可用**——同程序集内 `internal` 不构成任何屏障，模块间互引只能靠第 2/3 层。提案的"模块间绝对禁止互相引用"仅由文本扫描测试保障，属于**业界公认最弱的一层**。

**要求**（二选一，必须显式决策并写入文档）：
- **选项 1（推荐，务实）**：接受"单程序集 + 架构测试"定位，在文档中**明确写出边界强度等级**（"约定 + 测试强制，非编译期强制"），并把测试的覆盖面做扎实（命名空间前缀可区分 + 失败信息带违规类型名）。同时用 `internal` 收敛模块实现类可见性——虽不能跨程序集阻止，但能让"某类型是否属于模块公开面"变得可枚举、可断言。
- **选项 2（重）**：按模块拆程序集。对 `net48 + WPF + Costura.Fody` 单文件发布有实际成本（Costura 嵌入多个程序集、`pack://` 资源 URI 跨程序集、XAML 资源字典合并、启动期程序集加载顺序），需先做一次技术验证再决策。**不建议在本轮做**，但应在文档中记录为"已评估、暂不采用"及触发条件。

### 3.7 🟡 P2 · 术语向"插件化"漂移，与既有规范决策冲突

`HOST-PLUGIN-DECOUPLING-SPEC-v3.md` §0.2 明确规定："不再使用'插件（Plugin）'，统一使用'内置模块（Module）'"；§2.3 明确"外部 DLL 插件：**明确暂不支持**"；§9 列出"非目标（本期不做）"。

提案却使用"**微内核扩展点**""**真·垂直切片自治微内核架构**"作为标题级措辞。这些词在业界语境下强烈暗示"可插拔的第三方扩展"，与 v3 的既有决策相悖，也容易让后续执行者误判本期范围（例如去做程序集隔离、去做外部 DLL 加载）。

**要求**：标题与正文改用与 v3 一致的措辞（如"模块化单体（Modular Monolith）内的垂直切片收敛"），并在文档开头补一句范围声明，明确"本期不含外部插件/程序集隔离"。

### 3.8 🟡 P2 · 与既有文档的关系未声明，存在重复规划

`docs/MODULAR-HOST-DESIGN.md` §8 已给出几乎相同的目标目录结构（`Core/` + `Host/{Services,Views}` + `Modules/{ScreenLock,TaskScheduler,AudioSwitch,AppAutoMute}`，含 `Services/`、`Models/`、`Views/` 分层），§9 给出了阶段路线图。提案 §2.2 的迁移表与之高度重合，**却完全没有引用它**。

`HOST-PLUGIN-DECOUPLING-SPEC-v3.md` §0.1 建立了"取代声明"机制。提案作为 v3.1，应当说明：
- 取代谁、继承谁；
- 与 v3 §3.6（配置兼容迁移 + 原子写 + 合并写刷盘口径）、§4.1-4.5（托盘引擎双锚点/绑定+重建/线程防抖/状态卡砍掉）、§9（非目标）的关系——是沿用、修订还是推翻。

**要求**：补一节"与既有规范的关系"，逐条对齐。否则后续执行者面对三份文档（v2.0 / v3.0 / v3.1）无法判断哪个是最新意图，这本身就是最高频的返工来源。

### 3.9 🟡 P2 · 新增测试的落地条件未验证

提案 §6 要新增 `MenuProjectionEngineTests` 验证"菜单构建与点击行为转换"。但 `MenuItem` 是 `DispatcherObject`，**必须在 STA 线程创建**。现状：76 个测试全部为 `[TestMethod]`，测试工程**无任何 STA 测试基础设施**（`grep` 无 `STATestMethod`/`STAThread`）。

MSTest 3.4.3（当前引用版本）支持 `[STATestMethod]`，但需要：
1. 确认在 `net48 + UseWPF=true` 下可用；
2. 新增测试文件必须手工登记进 `CarroDesk.Tests.csproj`（`EnableDefaultCompileItems=false`，见 §2.1）。

**要求**：提案把"新增 UI 相关测试"标注为**需先做技术验证**，并给出降级方案（若 STA 测试不可行，则把 `MenuProjectionEngine` 的**纯逻辑部分**——节点树 → 中间描述模型的转换——抽成无 WPF 依赖的纯函数并单测，WPF 渲染部分靠人工冒烟）。

---

## 4. 与业界标准的对照小结

| 业界标准要素 | 本项目现状 | 提案覆盖 | 差距 |
|---|---|---|---|
| 模块边界靠编译器强制（`internal` + 独立程序集） | 单程序集，不可用 | ❌ 未讨论 | **结构性差距**，见 §3.6 |
| 每模块单一公开组合面 + Contracts 面 | `IModule` 是唯一契约 ✅；但模块类全 `public`，`TaskSchedulerModule.Scheduler` 被宿主穿透取用（`App.xaml.cs:149`） | ⚠️ 未提 | 建议：模块自己在 `OnStart` 注册其服务，宿主不伸手取模块内部对象 |
| 实现细节默认 `internal` | 未使用 | ❌ 未提 | 与 §3.6 选项 1 合并处理 |
| 数据/配置归属明确（谁拥有哪一段、谁能写） | 提案给了 JSON 结构 ✅ | ⚠️ 缺"写入权"规则与迁移算法 | 见 §3.1 |
| 模块可独立测试 | 部分达成（`TaskSchedulerService` 可脱机构造 ✅） | ⚠️ 未系统化 | 建议把"模块可脱机实例化并跑生命周期"列为模块验收项 |
| 边界违规的失败信息可定位到具体类型 | 现有测试只报"匹配数 N" | ⚠️ 未提 | 断言失败信息应带**文件名 + 违规文本**，否则 5 个阶段的迁移中排查成本很高 |
| 增量演进、每步可编译可运行 | v3 §7 有此承诺 | ⚠️ Phase 4 违反（三件事捆绑） | 见 §6 |
| 非目标显式声明 | v3 §9 有 ✅ | ❌ 提案无 | 建议补 |

---

## 5. 需要修正或降级的具体条款

| 编号 | 提案条款 | 问题 | 处置建议 |
|---|---|---|---|
| 5.1 | §1.2 "静态门面永久回归保护" | 该测试恒空过（§2.1）；且 `ArchitectureTests.cs` 不存在 | **降级为待办**：先修复测试，再宣称有保护 |
| 5.2 | §5 Phase 1 "轻量清理（零破坏）" | 含 `MenuProjectionEngine` 抽取，行为敏感，非零破坏 | **重新分类**：Phase 1 只留"通知统一 + 删静态单例"；菜单引擎抽取独立成阶段并配行为对照清单 |
| 5.3 | §4 方案 3 示例代码 | 丢 `Separator`、丢 `CollectionChanged`、异常静默吞（§3.4） | **修正示例**：补全 4 项能力，异常处理**不得低于**现状（记日志 + 通知） |
| 5.4 | §4 方案 1 配置结构 | 未给迁移映射表；PIN 归属与 `ScreenLockConfig` 矛盾（§3.1/§3.2） | **补齐**：迁移算法 + 白名单 + PIN 单一归属 + 幂等与回滚 |
| 5.5 | §4 方案 4 `ISettingsSectionProvider` | ① 把 `System.Windows.FrameworkElement`（PresentationFramework）写进 Core 契约，使契约层进一步绑定 WPF 视图类型——Core 现仅需 `Dispatcher`（WindowsBase）与 `ICommand`，加入 `FrameworkElement` 后任何无 UI 环境的消费/测试都不再可行；② `ApplySettings/ResetSettings` 无参数无返回值，无法对接持久化，也接不上 `ConfigEditorWindow` 现有"编辑副本→校验→提交"模式（校验失败无处回传）；③ `SectionOrder` 与 `IModule.Order` 语义重叠，双排序源会产生不一致 | **重设计**：Core 只放 `ISettingsSectionDescriptor`（Id/Title/Order + 视图工厂委托，返回 `object`）；`Apply` 改为返回校验结果（`bool` + 错误文本）以便宿主展示；`Order` 明确复用 `IModule.Order` 或说明二者的分工，不引入第二个排序源 |
| 5.6 | §4 方案 4 "无需修改一行 XAML 或 C#" | 过度承诺。`ConfigEditorWindow` 现有字段（`IdleMinutes` 等）被 `LockController`（`ILockAppearance` 活引用）与退出守卫消费，迁移涉及活引用语义重构 | **改写为**："新增模块无需改动设置中心；存量两模块的迁移需一次性的活引用重构" |
| 5.7 | §5 Phase 4 | 三件高风险变更捆绑（改存储结构 + 删 `IConfigRegistry`/`_adapters` + 引入设置扩展点） | **拆分**为 4a（配置结构 + 迁移，含双读单写过渡）/ 4b（删 registry/adapters）/ 4c（设置中心），每步独立可发布 |
| 5.8 | §6.1 "编译零告警（0 Warning）" | net48 + WPF + Costura/Fody + 自定义 `GenerateCustomVersion` target 下难以长期维持；作为每阶段硬门槛会阻塞交付 | **降级为**"不新增 Warning"（以基线 commit 的告警数为准），并允许在 `Directory.Build.props` 记录豁免清单 |
| 5.9 | §6.3 "扩充 `ArchitectureTests.cs`，断言各模块不含 `public static ... Instance`" | 断言范围未限定会误伤 `I18nService.Instance`（XAML `{loc:Loc}` 依赖，刻意保留）、`HotkeyService.Instance`（`App.xaml.cs:123` 仍用于注册接口）、`FloatingPanelWindow.Instance` | **限定范围**为 `src/Modules/**`；并明确 `HotkeyService.Instance` 的处置是"消除模块侧直访"而非"删除单例本身" |

---

## 6. 建议的修订版实施路线图

原则：**先修保护网 → 再做零风险清理 → 再动文件 → 最后动数据**。文件迁移与数据迁移必须分开，且各自独立可发布。

```text
Phase 0（新增 · 保护网修复，必须先做）
  ├── 修复 ViewLayer_HasNoStaticAppReferences：根定位改为 .git/Directory.Build.props 上溯，
  │   定位失败即 Assert.Fail（禁止静默跳过）
  ├── 扫描范围扩为 src/** + 目录白名单，失败信息带文件名与违规文本
  ├── 建立"不新增 Warning"基线并记录当前告警数
  └── 确认测试工程显式 <Compile Include> 登记流程（新增测试文件必登记）
  ※ 完成标志：故意在 src/Modules 里写一行跨模块引用，测试必须变红

Phase 1（原 Phase 1 缩减 · 零行为变更）
  ├── 统一通知流至 Context.ShowNotification，移除 5 处 NotificationCallback/BalloonNotifier
  │   与 App.xaml.cs 的手工接线
  ├── 先为 AppAutoMuteSettingsWindow 强制注入模块实例（消除 :34 的 Instance 兜底），
  │   再删除 6 个模块的 public static XxxModule Instance
  └── 每步跑 76 项测试

Phase 2（基础设施归位 · 文件迁移，不动数据）
  ├── HotkeyService + HotkeyHelper 一并上浮 Host/Services/，修正命名空间
  ├── HotkeyTrigger 改为注入 IHotkeyService（消除 Instance 直访）
  ├── LockController / KeyboardBlocker / ILockService / ILockAppearance 下沉
  │   Modules/ScreenLock/Services|Contracts（同步处理其对 ConfigService 的依赖）
  └── 明确 ProcessHelper / ProcessExclusionService / TaskLogger 的 SharedKernel 定位

Phase 3（切片收敛 · 文件迁移，XAML 命名空间一次改到位）
  ├── 选定并落实 XAML 命名空间策略（推荐 B：CarroDesk.Modules.Xxx.Views），列调用点清单
  ├── src/Services/Tasks/*（10 文件 + Triggers/ 9 文件）→ Modules/TaskScheduler/{Services,Models}
  │   TaskDefinition.cs → Modules/TaskScheduler/Models/
  └── 5 个模块设置窗口 → 各 Modules/*/Views/
  ※ 完成标志：src/Services 仅剩 AutoStart/ProcessHelper/Localization/Pin 系共享能力

Phase 4a（配置结构 + 迁移 · 数据变更，最高风险）
  ├── 设计并实现扁平→Host/Modules 的迁移映射表（含白名单、幂等、备份、失败回滚）
  ├── 双读单写过渡：读时兼容新旧两形态，写时只写新形态（保留一个版本的过渡期）
  ├── PIN 单一归属 Host.Security；ScreenLockConfig 删 PinHash/PinSalt；
  │   RequestBlockExit 改经 IPinService.IsConfigured
  └── 回归：新增 ConfigSeparationTests + 退出守卫测试 + 既有 JsonMigrationTests 适配

Phase 4b（清理 Adapter 妥协）
  ├── 删除 IConfigRegistry / ConfigManager._adapters / _defaultFactories
  ├── ConfigService 清除三处模块硬编码：IsCoreAppSetting(208-232)、
  │   4 个字符串槽(72-75, 286-293)、GetModuleToken/SetModuleToken 分支(241-244, 255-258)
  └── ScreenLockModule / TaskSchedulerModule 构造函数移除 ConfigService 依赖

Phase 4c（设置中心）
  ├── 重设计 ISettingsSectionProvider（见 §5.5），先做 ScreenLock 一个试点
  ├── ConfigEditorWindow 收缩为"语言/开机自启/PIN 安全"三项宿主通用设置
  └── 其余模块设置窗口保留为独立窗口（本期不做全量迁移，避免一次性大改 UI）
```

**关键顺序变更说明**：
- 原 Phase 4 拆为 4a/4b/4c，**数据迁移与代码清理分离**——4b 是纯代码清理（可随时回退），4a 是数据变更（不可逆，需备份）。
- 原 Phase 1 中的菜单引擎抽取**移出 Phase 1**（它不是零破坏），建议在 Phase 3 之后、4a 之前单独做，并先写一份"Tray 与 FloatingPanel 行为差异对照表"（业界称迁移前必须落盘的对照产物），无对照表不开工。

---

## 7. 建议明确不做 / 维持现状

与 v3 §9 的非目标保持一致，并补充本次审查新识别的项：

1. **不引入外部 DLL 插件机制**（沿用 v3 §2.3）。
2. **不拆分程序集**（本轮），但记录为"已评估、暂不采用"，触发条件：模块数 > 10，或出现需要独立版本演进的模块。
3. **不引入 MEDI 等第三方 DI 容器**（沿用 v3 §3.8，理由与体积/绑定重定向分析仍成立）。
4. **不引入 NetArchTest 等新依赖**：沿用现有"源码文本 + 正则扫描"风格即可满足单程序集下的边界断言，避免为测试引入新的包依赖链。
5. **不做托盘状态卡/四态聚合**（沿用 v3 §4.4 已决策）。
6. **不重写 `I18nService.Instance` 静态单例**：XAML `{loc:Loc}` 标记扩展依赖它，改造收益低、风险高。
7. **不在本轮统一 6 个模块的设置窗口为 UserControl**：4c 只做 1 个试点 + 宿主设置收缩，全量迁移留给后续迭代（避免 UI 大改与数据迁移叠加）。
8. **不追求 `dotnet build` 0 Warning**（见 §5.8）。

---

## 8. 证据索引

**构建与工程配置**
- `src/CarroDesk.csproj`：`net48` / `LangVersion 7.3` / `UseWPF+UseWindowsForms` / SDK 默认通配（未设 `EnableDefaultCompileItems`）
- `tests/CarroDesk.Tests/CarroDesk.Tests.csproj`：`EnableDefaultCompileItems=false` + 12 条显式 `<Compile Include>`；MSTest 3.4.3
- `CarroDesk.slnx`：仅含 `src/CarroDesk.csproj`（测试项目未入解决方案）；**仓库无 `CarroDesk.sln`**
- `Directory.Build.props`：`GenerateCustomVersion` target 调用 `git rev-parse`

**契约与宿主**
- `src/Core/IModule.cs`、`src/Core/ModuleBase.cs`、`src/Core/IModuleContext.cs`、`src/Core/IConfigManager.cs`、`src/Core/IConfigRegistry.cs`、`src/Core/IHotkeyService.cs`、`src/Core/IExitGuard.cs`
- `src/Host/Services/ModuleManager.cs`（`:90` 状态守卫、`:163` Faulted 过滤、`:204-242` ModuleContext）
- `src/Host/Services/ServiceContainer.cs`、`src/Host/Services/DynamicTrayController.cs`
- `src/App.xaml.cs`（`:123` HotkeyService 注册、`:140-173` 模块构造与手工接线、`:149` 穿透取 `Scheduler`、`:380-407` 退出守卫）

**配置体系**
- `src/Services/ConfigService.cs`（`:72-75` 4 个字符串槽、`:167-180` 白名单过滤、`:208-232` `IsCoreAppSetting`、`:236-259` 模块名硬编码分支、`:286-293` 槽同步）
- `src/Host/Services/ConfigManager.cs`（`:13-32` Adapter、`:35-38` 注册表、`:78-159` 三级回退）
- `src/Models/AppSettings.cs`、`src/Modules/ScreenLock/Models/ScreenLockConfig.cs`、`src/Modules/TaskScheduler/Models/TaskSchedulerConfig.cs`

**模块与视图**
- `src/Modules/ScreenLock/ScreenLockModule.cs`（`:14` 静态单例、`:39` BalloonNotifier、`:41-48` ConfigService 注入、`:50-81` RegisterConfig 适配器、`:256` RequestBlockExit 读 PIN）
- `src/Modules/TaskScheduler/TaskSchedulerModule.cs`（`:17` 静态单例、`:27-58` 同上、`:269` TaskEditorWindow 构造）
- `src/Services/LockController.cs`（`:15, 22-24, 61-62` ConfigService + PinService/KeyboardBlocker 直接 new、`ShowClock/OverlayOpacity` 活引用）
- `src/Views/TrayContextMenu.xaml(.cs)`、`src/Views/FloatingPanelWindow.xaml.cs`（`:566-618` CreateVisual、`:620-658` hover）、`src/Views/ConfigEditorWindow.xaml(.cs)`
- `src/Views/AppAutoMuteSettingsWindow.xaml.cs`（`:34` `?? AppAutoMuteModule.Instance` 兜底、`:231` NotificationCallback）

**任务子系统**
- `src/Services/Tasks/`（10 个 `.cs`）+ `Triggers/`（9 个 `.cs`）
- `src/Services/Tasks/HotkeyService.cs`（`:27` Instance）、`Triggers/HotkeyTrigger.cs`（`:24, 45` 直连 Instance）
- `src/Models/TaskDefinition.cs`（`:206` 使用 `HotkeyHelper`）

**测试**
- `tests/CarroDesk.Tests/ConfigAndTriggerTests.cs`（`:157-181` 失效的视图层断言）
- `tests/CarroDesk.Tests/*.cs`：`[TestMethod]` 计数 = 76

**既有规范文档**
- `docs/MODULAR-HOST-DESIGN.md`（§8 目标目录结构、§9 路线图）
- `docs/HOST-PLUGIN-DECOUPLING-SPEC-v3.md`（§0.1 取代声明、§0.2 术语、§2.3 外部插件非目标、§3.6 配置兼容与原子写、§3.8 ServiceContainer 决策、§4.1-4.5 托盘引擎、§7 路线图、§9 非目标）
- `docs/FIX-PLAN-20260918.md`（§2.4 节点订阅隐患的既有定级、§2.6 行号漂移提示、§3.11 HotkeyService 静态单例）

**业界参考**
- Enforcing Module Boundaries in .NET: How to Keep a Modular Monolith Actually Modular（DEV Community，2026-08-03）— 三层强制（编译器 `internal` + Contracts 程序集、架构测试、"每模块只暴露一个 public 类型"探测）
- Part 1. Enforcing True Module Boundaries in a .NET Modular Monolith（FullStack City，2026-01-21）— "Folders do not enforce boundaries. Assemblies do."；`InternalsVisibleTo` 用于测试
- Where Vertical Slices Fit Inside the Modular Monolith Architecture（Milan Jovanović，2026-06-02）— 模块边界（宏观）与切片（微观）的分层职责
- Building a Modular Monolith With Vertical Slice Architecture in .NET（DEV Community，2025-04-16）— 模块 `PublicApi` 契约面与内部实现隔离
- Modular Monolith Architecture Cheat Sheet（2026-05-17）— 边界强制技术谱系与"模块拥有四件事（业务规则/数据/实现/小契约面）"

**证据限制**
1. 审查环境无 `dotnet` CLI，**未实机运行**构建与测试；§2.1 的结论为确定性路径推导，建议实施前实机确认一次。
2. 未使用浏览器/外部资料核对项目自身的第三方依赖版本兼容性（Costura/Fody + net48），§3.6 选项 2 的成本评估需专项验证。
