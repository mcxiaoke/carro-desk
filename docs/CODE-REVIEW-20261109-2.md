# CarroDesk 全面审查报告（2026-11-09 · 综合版）

> 审查基线：工作树 HEAD `4a2b7fb`，`net10.0-windows` / WPF，v1.4.x。
> 审查时间：2026-11-09 22:35–22:58（GMT+8）。
> 审查范围：`src/` 180 个 `.cs/.xaml`（36,447 行）、`cli/` 1 个 `.cs`、`tests/` 47 个 `.cs`（10,758 行）、工程配置、发布脚本、安装脚本、`docs/` 主要文档。
> 实证基线：`dotnet build CarroDesk.slnx` **0 警告 0 错误**（2.83s）；`dotnet test CarroDesk.slnx --no-build` **411 通过 / 0 失败 / 0 跳过**（50s）。
> 方法：三个独立视角（工程架构与工程化 / 代码质量与健壮性 / 界面与人机交互）逐文件通读，主审再对高风险结论做行号级回读与命令行实证复核；量化数据由脚本统计。
> 标注：【实证】= 本次读到源码确认；【交叉实证】= 两个以上独立视角结论一致；【待验证】= 需真机/多屏/权限环境复现，不作定论。
>
> **与同日 `docs/CODE-REVIEW-20261109.md` 的关系**：该文件是本次审查期间出现的另一份独立审视报告（未纳入 Git）。本报告不覆盖它，而是按用户要求的四个维度（工程架构 / 代码质量 / 界面与人机交互 / 易用性）做系统评估，并**对其中 4 条高风险结论做了源码回读复核**（见 §3.3、附录 B），两份报告结论互补、可对照阅读。

---

## 0. 结论摘要

**整体判断**：工程底座质量高于同规模个人桌面工具项目——契约层（`Core`）零越层依赖、`ModuleBase` 生命周期幂等且失败逆序清理、IPC 命令内核六步短路校验、`AtomicFile` 原子替换+回滚+自愈、剪贴板 DPAPI 加密、411 项测试覆盖 IPC/安全/状态机。构建与测试基线干净。

**当前问题不在"架构烂"，而在四条主线**：

1. **失败路径被静默吞掉**：233 处空 `catch`（分布在 62 个文件）；锁屏失败、热键注册失败、配置保存失败、剪贴板落盘失败均无用户可见反馈 → 用户以为功能已生效。
2. **契约一致性漂移**：CLI 仍引用整个 WPF 主程序集、文档仍写 net48/Costura、能力表声明 `requiresPin=false` 而运行时动态要求 PIN、MCP 在宿主未启动时永久缓存空工具表、托盘与浮窗菜单分叉、任务管理与编辑窗口完全绕过本地化。
3. **UI/UX 是明显短板**：无全局资源字典（5 套按钮样式、3 个主蓝值）、深色主题零适配、无障碍关键字全仓 0 命中、多数设置窗口无脏检查与行内校验、任务管理器 10 列无排序无默认排序。
4. **新修复引入回归**：2026-10-25 的 7 批修复基本可信落地，但 `4a2b7fb`（任务管理器大盘列重构）引入 4 处生命周期/时序缺陷，2026-10 的编辑器重构引入 wait 模式 `KillWithHost=false` 与重复补跑。

**最值得优先做的 5 件事**：

| 序 | 动作 | 理由 | 成本 |
|---|---|---|---|
| 1 | 任务配置读取失败不再无条件重写 `[]`，并把 `Errors/SchemaVersion/ReadOnly` 传到窗口、保存前检测外部修改 | 唯一可能造成"任务全丢"的路径 | 中 |
| 2 | 让失败可见：锁屏/热键/配置保存/剪贴板落盘失败统一气泡 + Error 日志 + 菜单态 | 覆盖 4 类"以为生效"的静默失效 | 低 |
| 3 | 建全局 `Theme.xaml`，收敛样式/颜色；任务管理与编辑窗口补齐本地化键 | 视觉与双语一致性，改动集中 | 中 |
| 4 | 修 MCP 空表缓存、能力 PIN 元数据、`wait` 模式 `KillWithHost`、AppAutoMute 白名单空保护 | 对外接口可用性与进程治理 | 低 |
| 5 | 删除 1,407 行死代码 `TaskEditorWindow`，同步 DESIGN/README/release.py 的技术栈与打包表述 | 一次性降低维护面与误导 | 低 |

---

## 1. 审查基线

| 指标 | 实测值 |
|---|---|
| `src` 源文件 | 180 个 `.cs/.xaml`，36,447 行（排除 `bin/obj`） |
| `tests` | 47 个 `.cs`，10,758 行，411 个用例全通过 |
| 超 500 行文件 | **15 个**，合计 11,631 行（占 `src` 约 32%）；最大 `TaskEditorWindow.xaml.cs` 1,407 行（且已无生产入口） |
| 空 `catch` | **233 处 / 62 个文件** |
| 语言包 | `zh-CN.json` / `en-US.json` 各 **838 键**，键集合完全对齐 |
| `AutomationProperties` / `TabIndex` / `DynamicResource` / `FocusVisualStyle` / `AppsUseLightTheme` | **全部 0 命中** |
| 编译期门禁 | 三个 csproj 全 `<Nullable>disable</Nullable>`，无 `TreatWarningsAsErrors`、无 `EnableNETAnalyzers`、无 `.github/workflows` |
| 发布形态 | `scripts/release.py:99-105` 用 `PublishSingleFile` + `--self-contained false`（框架依赖），Costura/Fody 包引用已注释 |

**局限**：未做真机运行验证（多显示器非 100% 缩放、真实服务 DACL、受限账户睡眠、读屏软件）；相关条目标【待验证】并给出验证方法。`docs/old/`、`temp/`、`release/` 未纳入。

---

## 2. 工程架构

### 2.1 做得好的部分【实证】

- `src/Core` 全部 `.cs` 无 `using CarroDesk.(Host|Modules|Views|Services)`，`IModuleContext.cs:9-13` 只暴露 `GetService/ShowNotification/RequestTrayRefresh`，模块无法向容器注册服务（瘦上下文 pull 模型）。
- `Core/ModuleBase.cs:15,20,55-57,76-130` 状态读写加锁、Start/Stop 幂等，`:114` 启动异常逆序清理并标记 `Faulted`。
- `Host/Commands/CommandHost.cs:95-125` 固定校验序：能力存在 → 模块 Running → 参数 schema → PIN → 限流 → 执行，任一步短路；`CommandRateLimiter`、`CommandAudit`（参数只记摘要、PIN 单独提取）分层清晰。
- `Common/AtomicFile.cs:25-57,69-93` 临时文件 + `File.Replace` + bak 回滚 + 损坏自愈。
- `ClipboardStorageCrypto.cs:63` 用 `ProtectedData.Protect(..., CurrentUser)` 加密，历史"剪贴板明文落盘"已闭环。
- `Directory.Build.props:43-98` 统一版本（Debug 日增 / Release 固定 + Git commit 溯源）。

### 2.2 问题清单

| 编号 | 级别 | 问题 | 证据 | 影响 | 改进 |
|---|---|---|---|---|---|
| **G1** | 高 | CLI 引用整个 WPF 主程序集，与自身注释相反 | `cli/CarroDesk.Cli/CarroDesk.Cli.csproj:12` `<ProjectReference Include="..\..\src\CarroDesk.csproj" />`；`Program.cs:2-3,12` 只用到 4 个类型，注释却写「无 WPF 负担」；实测 publish 目录含 `CarroDesk.exe`、`CarroDesk.runtimeconfig.json`、样例 json | 宿主升级强制 CLI 重发；单文件目标被稀释；产物耦合 | 【重构】抽 `CarroDesk.Protocol` 无 WPF 类库承载 `ControlArgs/FrameCodec/RpcProtocol/PipeRpcClient/McpStdioServer`，src 与 cli 共同引用 |
| **G2** | 中 | 契约层方向反转：模块反向依赖 Host | `Host/Commands/CommandErrorCodes.cs:1`；`Modules/ServiceControl/ServiceControlModule.cs:6`、`Modules/Awake/AwakeModule.cs` 直接 `using CarroDesk.Host.Commands` | 无法抽出 Core/能力协议程序集 | 把 `CommandErrorCodes/CommandDescriptor/CommandResult` 下沉 `Core/Commands` |
| **G3** | 中 | 命名空间与目录系统性错配 | `Modules/TaskScheduler/Services/TaskSchedulerService.cs:15` → `CarroDesk.Services.Tasks`；`Host/Services/HotkeyService.cs:9` 同名空间；`Modules/TaskScheduler/Models/TaskDefinition.cs:7` → `CarroDesk.Models`；`Modules/ScreenLock/Views/LockWindow.xaml.cs:12` → `CarroDesk.Views`；`IdleDetector.cs:4` → `CarroDesk.Services` | 按目录推断类型出处失效，重构易漏 | 一次性批量重命名为 `CarroDesk.Modules.<模块>.<层>` |
| **G4** | 中 | 服务层直访 `Application.Current` | `Modules/ScreenLock/Services/LockController.cs:48,114,134`、`Host/Services/AudioService.cs:81`、`TaskSchedulerService.cs:1224`；对照 `AwakeService.cs:257` 已注入 `_dispatcher` | 业务服务无法脱离 WPF/STA 单测；历史曾出现 Dispatcher 关闭后 `Invoke` 永久阻塞 | Dispatcher 构造注入，与 `AwakeService` 对齐 |
| **G5** | 中 | 1,407 行旧任务编辑器已是死代码 | 全仓 `new TaskEditorWindow` 仅命中自身构造函数定义（`TaskEditorWindow.xaml.cs:32`）；`tests/` 零引用；生产入口为 `TaskManagerWindow.xaml.cs:319,333,354` → `TaskEditDialog`，托盘入口 `TaskSchedulerModule.cs:188` → `TaskManagerWindow` | 参与编译/维护/审查；两套编辑器字段集不同，重新挂回会静默丢字段 | 确认无反射后删除 `.xaml` + `.xaml.cs`，或标 `[Obsolete]` 说明 |
| **G6** | 中 | 构建/发布契约漂移 | `README.md:41`、`docs/DESIGN.md:42,175`、`scripts/release.py:120`、`CarroDesk.Cli.csproj:21-22` 仍写 Costura；实际 `release.py:99-105` 为 `PublishSingleFile`；`src/FodyWeavers.xml:2`、`cli/CarroDesk.Cli/FodyWeavers.xml:2` 的 `<Costura />` 与两份 `.xsd` 成死配置；`CleanCliOutputJunk` 删除的 `CarroDesk.exe.config` 在 net10 根本不生成 | 新人按错误文档排障；框架依赖发布需 .NET 10 Desktop Runtime，但安装器无前置检查（DESIGN 仍称"免安装运行时"） | 统一改「.NET PublishSingleFile（框架依赖）」；删 4 个 Fody 残留与失效 Target；安装器加运行时检测 |
| **G7** | 中 | 无编译期质量门禁、测试文件需手工登记 | 三个 csproj 均 `Nullable=disable`，无 analyzer / `TreatWarningsAsErrors`；`tests/CarroDesk.Tests.csproj:9-60` `EnableDefaultCompileItems=false` + 47 条手写 `<Compile Include>`；仓库无 CI workflow | "0 警告"依赖人工；新增测试文件漏登记即静默不编译 | 测试项目先开 `Nullable` + `AnalysisMode=Recommended` 并恢复默认 glob；加 build+test 两步 CI |
| **G8** | 中 | 两套重载机制 + 死事件 | `ConfigManager.ConfigReloaded`（`Host/Services/ConfigManager.cs:26,41`）无生产订阅者，实际由 `App.xaml.cs:369` 的 `ReloadConfig → Modules.ReloadAll` 驱动；`ConfigService.cs:171-186` 用自定义 `JsonSerializerSettings`，`ConfigManager.cs:119-140` 用 `JToken.FromObject` 默认设置 | 同一字段在宿主节/模块节可能序列化不同；新增模块易漏接重载 | 二选一保留重载链；共用一份 `JsonSerializerSettings` |
| **G9** | 中 | 全局静态状态与测试污染 | `Services/Localization/Loc.cs:10-38`（`I18nService.Instance`）、`App.xaml.cs:131-138`、`TaskConfigService/TaskLogger/TaskRunner` 等 20+ 静态类；`UiRenderingTests.cs:244` 直接 `TaskConfigService.Save(sampleTasks)` 写真实文件且无 `TestCleanup` | 单例语言状态、静态配置写入跨用例泄漏 | i18n 经 `IModuleContext.GetService<T>()`；测试统一 `TestCleanup` 还原 |
| **G10** | 中 | 文档与实现漂移（详见 §5） | `docs/DESIGN.md:5,43,157,175,176` 仍写 ".NET Framework 4.8 + C# 7.3"、"291 通过"、`Dictionary<name, RunSlot>`；README:40 已在历史 L25 修正，DESIGN 漏改 | 文档可信度被单点破坏 | 按现状重写 DESIGN §2/§8，测试数改由 CI 生成 |

---

## 3. 代码质量

### 3.1 新提交专项（`4a2b7fb` / `7ae5097`）【实证】

`4a2b7fb`（任务管理器 10 列重构 + 内存列 + 3s 采样）**实现正确之处**：`ReadWorkingSetBytes` 用 `using` 释放 `Process`；`_perfSampling` 用 `Interlocked` 防重叠；仅窗口可见时采样；刻意不调 `Process.HasExited`（避免受限进程抛"拒绝访问"，`CHANGES-20261109.md:16` 已记录）。**但引入 4 处缺陷**：

| 编号 | 级别 | 问题 | 证据（`TaskManagerWindow.xaml.cs`） |
|---|---|---|---|
| C1 | 中 | 最小化/关闭只 `Stop()` 两个 `DispatcherTimer`，不取消已在 `Task.Run` 飞行的采样轮次，仍逐个读 `WorkingSet64` 并回投，与 `CHANGES-20261109.md:15`「最小化即停止/后台零读取」承诺不符 | `:77-91`、`:129-156` |
| C2 | 中 | 采样回投无"窗口已关闭"门闩；`OnSchedulerStateChanged` 的 `Dispatcher.BeginInvoke`（`:185-195`）无 try/catch，Dispatcher 已 shutdown 时抛 `InvalidOperationException` | `:63-71`（`Closed` 只停计时器、退订，无 `_closed` 标志）、`:139-149`（采样路径有 catch）、`:185-195` |
| C3 | 中 | `Interlocked.Exchange(_perfSampling,1)` 把"状态跃迁/列表重建后的立即补采"静默吞掉；`UpdateSnapshot` 先清 `_memoryBytes=null`（`:534`），内存列最长等下一个 3s tick 才恢复；旧 `results` 还会写回已重置项 | `:110`、`:193`、`:531-546` |
| C4 | 中 | `SearchBox_TextChanged` 每字符 `_items.Clear()` 全表重建并触发一次采样；选中仅按名字恢复、滚动位置丢失，飞行采样结果回写到已移除的孤立 ViewModel | `:225-232`、`:242`、`:297-300` |

`7ae5097`（剪贴板敏感词过滤）**两处安全缺口**：

- **C5 [高]** 过滤只拦新记录，不追溯清理历史：`ClipboardHistoryService.cs:65-85,93-94` 的 `ApplyConfig` 只重建规则集并重算 `PreviewText`，注释明写"不改动历史中此前已存在的同内容条目"。用户先复制密码、后启用过滤规则 → 敏感内容仍在 `history.json`，可查看可回写。建议规则变更时重扫 `_items` 删除命中项并落盘，或提供一次性"清理历史命中项"入口。
- **C6 [中]** 规则数量/关键字长度只在 UI 校验（`ClipboardFilterSettingsWindow.xaml.cs:115,183`），`ClipboardTextFilter.cs:40-61` 服务层无裁剪；手工编辑配置塞入数千条规则时，每次复制做 O(规则数×文本长度) 匹配，且 `RecordText`（含全量深拷贝 `Snapshot`）跑在 UI 线程（`ClipboardHistoryModule.cs:399-403`）→ 界面卡顿。

### 3.2 仍存在的高风险缺陷【交叉实证】

以下 6 条由同日独立报告提出、本次已回读源码确认，属于**历史两轮 review 未覆盖的新缺陷**：

| 编号 | 级别 | 问题 | 复核证据 |
|---|---|---|---|
| **C7** | 严重 | `TaskConfigService.Load` 的 `catch (Exception)` 无条件调用 `RecoverFromCorruptFile` → `AtomicFile.WriteAllText(FilePath, "[]")`。**任何异常**（磁盘瞬时 IO、杀软占用、权限）都被当成"文件损坏"重写为空数组；再叠加 `TaskManagerWindow.xaml.cs:200-202` `try { Load(); } catch { }` 丢弃 `Errors`、任何一次保存整表覆盖，形成三条静默数据丢失路径 | `TaskConfigService.cs:198-202,253-274`；`TaskManagerWindow.xaml.cs:200-202,496`；`TaskConfigService.cs:291-318` |
| **C8** | 高 | 新编辑器把 wait 模式的 `KillWithHost` 恒写 false：`KillWithHost = isDetach && (KillWithHostBox.IsChecked ?? true)`。wait 任务不建 JobObject，宿主退出后进程继续存活但不再被跟踪，下次启动可再拉一份 | `TaskEditDialog.xaml.cs:550`（本次回读确认） |
| **C9** | 高 | MCP 在宿主未启动时把空能力表写入 `_capabilities` 并缓存，`EnsureCapabilities` 首行 `if (_capabilities != null) return` → 整个会话 0 个工具且不恢复；`docs/USAGE.md:213` 却称"客户端无需重启即可自动恢复" | `McpStdioServer.cs:141-150`（本次回读确认） |
| **C10** | 高 | `services.start/stop` 能力声明 `RequiresPin=false`，但 `RequiresPinFor = PinPolicyFor(...)` 运行时按服务动态要求 PIN；`host.capabilities.list` 与 MCP `inputSchema` 只读 `RequiresPin` → 调用被 -32002 拒绝，模型反复猜 PIN 会触发 PinGuard 全局封锁 | `ServiceControlModule.cs:132-133,157-158`；`McpStdioServer.cs:212-214` |
| **C11** | 高 | AppAutoMute 白名单为空时静音所有非前台音频：空名单保护 `if (!isWhitelist && empty) return` 只覆盖黑名单；白名单分支 `ContainsProcess(空)=false` 导致除当前前台外全部被静音 | `AppAutoMuteModule.cs:167-171,214-231`（本次回读确认） |
| **C12** | 严重 | 锁屏失败对用户完全静默（含"未配置 PIN"）：`LockSafe()` 在 `Enabled=false`/`Controller==null`/未配 PIN 时都只 `return false`，托盘"立即锁定"与热键路径丢弃返回值 → 用户误以为桌面已受保护 | `ScreenLockModule.cs:306-317,459-467`；`LockController.cs:216-233` |

另经代码推演（同日报告，本轮未逐行复核，标【待验证】）：wait 模式重试循环 `ActiveInstances` 计数漂移（`TaskSchedulerService.cs:642,670`）、`CatchUpMissed` 当天已正常运行后重载再补跑（`:330-345,502`）、重启固定 `Task.Delay(300)` 与异步 `OnStopCompleted` 槽位不同步（`:420-426,1078,1092-1106`）、`OnAbandon()` 在 `WaitingRetry` 下是空操作导致 FSM 卡死（`:957-978`；`TaskStateMachine.cs:149,290-301`）。**建议修复前用附录 B 的方法做实机复现**。

### 3.3 本轮其他实证缺陷（择要）

| 编号 | 级别 | 位置 | 问题与修法 |
|---|---|---|---|
| C13 | 高 | `TaskEditDialog.xaml.cs:359,375,423-434` | detach 测试连点覆盖 `_testRunHandle`，旧进程脱管；`_testCts` 每次 new 不 Dispose。→ 新测试前先 Stop 旧句柄并 Dispose 旧 CTS，或运行中禁用按钮 |
| C14 | 中 | `TaskEditDialog.xaml.cs:344,387,433-434,480-482` | `async void` 的 await 后访问已被 `OnClosing` 置 null 的 `_testCts` → NRE 被吞成"测试异常"，并继续写已关闭窗口控件。→ CTS 存局部变量 + `IsLoaded` 门闩 |
| C15 | 中 | `Services/PinService.cs:268-271,294-297` | PinGuard 状态文件损坏/不可写时静默"按无历史失败继续"，重启即清零失败计数，限流可绕；损坏时应**保守保留封锁**而非放行。另 `NamedPipeCommandServer.cs:58-71` DACL 构造失败仅 SYSTEM 兜底，IPC 静默不可用 |
| C16 | 中 | `ClipboardHistoryService.cs:23,47,67,90,107,138` | `_config` 跨线程读写无同步（唯 `_filter` 有 volatile），配置重载与记录并发时短窗口仍按旧配置自动记录。→ 改 volatile 引用 + 整体替换不可变快照 |
| C17 | 中 | `ClipboardHistoryConfig.cs:24,27`；`ClipboardHistoryService.cs:260-273`；`JsonClipboardHistoryStorage.cs:194,246-250` | 历史遗留 M8 仍在：无总字节预算（最坏 1000×1M 字符 ≈ 2GB），Flush 固定 5s 且 `Wait` 返回值忽略、写失败只 `Debug.WriteLine` → 重启历史全丢 |
| C18 | 中 | `NamedPipeCommandServer.cs:76-85,108-135` | `Start()` 的 `Task.Run(AcceptLoopAsync)` 无引用、`Dispose` 只 Cancel 不 Dispose CTS、Start 可重入产生两个 accept 循环争抢同名管道 |
| C19 | 中 | `Host/Ipc/PipeRpcClient.cs:37,43` | `--timeout` 只作用于 Connect，`ReadFrame` 同步无超时 → 宿主卡死时 CLI/MCP 永久挂起 |
| C20 | 低 | `TaskManagerWindow.xaml.cs:650-668` | `FormatDuration` 未钳制 `double.PositiveInfinity`/极大值（本机实测强转 `long` 为饱和值，输出荒谬时长但不崩溃）；`:117,174-177` 3s 内 PID 复用会读到他进程工作集 |
| C21 | 低 | `Common/ControlArgs.cs:60-105` | 不支持 `--key=value`，被解析成 `params["key=value"]=true`，服务端报 `unknown param` |

### 3.4 历史遗留项复核（2026-10-25 报告"未处理"清单）

| 项 | 现状 | 证据 / 说明 |
|---|---|---|
| M7 AppAutoMute 重载静音残留 | **仍存在** | `AppAutoMuteModule.cs:68-84` 的 `OnConfigReloaded` 按**新** `TargetApps` 解除，无旧清单兜底 |
| M8 剪贴板总量预算 | **仍存在** | 见 C17 |
| M28 测试质量 | **部分** | 恒真断言/名不副实已修；`UiRenderingTests.cs:111,121,143,189,277,296,333` 仍零 Assert 且用非贪婪正则解析嵌套 Grid；`ConcurrencyAndLifecycleTests.cs:72`、`TaskPersistentRunTests.cs:638`、`CommandHostTests.cs:372` 用 `Thread.Sleep` 等真实时序；`DevicePresenceAndAutoLockTests.cs:58-62` 依赖真实 ICMP |
| M25 配置编辑器脏检查 | **仍存在** | `ConfigEditorWindow.xaml.cs:234` `OnCloseClick → Close()`，全文件无 `OnClosing`/`IsDirty` |
| M27 安装脚本 i18n | **仍存在** | `CarroDesk.iss:10,76-77,85-92` 声明双语但 `Languages/` 仅 `ChineseSimplified.isl`，正文 `:267,297,301,529,632` 硬编码中文；`CarroDesk.nsi`(533 行) + `.isl` 为 release.py 不用的死配置 |
| M6 单实例互斥体跨宿主 | **仍存在**【待验证】 | `TaskSingleInstanceMutex.cs:30,86-92` 句柄由宿主持有；"宿主重启拉起第二实例"需按附录 B 复现 |
| M14/L13 Awake UI 线程枚举 | **仍存在** | `AwakeService.cs:257`（DispatcherTimer）、`:676` 每 5 tick → `:763` UI 线程 `Process.GetProcesses()`（已加快照+Dispose，仍在 UI 线程） |
| M16 白名单轮询周期 | **仍存在**【待验证】 | `AppAutoMuteModule.cs:150,163` 仅前台切换触发、`:208` timer 首行 `Stop()` |
| M19/M24/L22 多屏 DPI 与副屏定位 | **仍存在/部分** | `LockWindow.xaml.cs:76-79,119-123` 物理像素与 DIP 混用、`DisplayMonitorHelper.cs:69-77,92-101` 两套口径、`app.manifest:5` 仅系统级；`FloatingPanelWindow.xaml.cs:270,287-293,322-325` Tray/Center/TopRight 仍只用主屏 WorkArea（Custom 已做多屏映射） |
| L1 detach 失败不同步 supervision | **已修** | `TaskSchedulerService.cs:966-969` → `HandleDetachFailure` 写镜像 |
| L23 MemoryOptimizer CTS | **已修** | `MemoryOptimizer.cs:38-39,56` |
| L25 README 技术栈 | **已修（DESIGN 未同步）** | `README.md:40` 已为 net10.0-windows |
| L2/L4/L5/L6/L7/L8/L9/L14/L15/L16/L17/L19/L20/L21/L24 | **仍存在** | 逐条证据见 2026-10-25 报告 §6；本轮抽查 L2(`ProcessJob.cs:104-108`)、L5(`TaskConfigService.cs:301-308`)、L14(`AwakeSettingsWindow.xaml.cs:294`)、L19(`AppAutoMuteConfig.cs:12`)、L20(`CommandHost.cs:310-313`) 均确认 |
| L18 CTS Cancel+Dispose | **部分/原结论不成立** | `ProfileScheduleEngine.cs:103-107,191-222` 代码仍在；本机实测 .NET 10 下 `CTS.Token` 在 Dispose 后不抛 ODE，原"ODE 噪音"不成立，但 `Task.Run` 只 catch `OperationCanceledException`，`ApplyCurrentSettingSync` 的非取消异常成未观察异常 |

### 3.5 测试质量与覆盖盲区

- **优点**：411 项覆盖 IPC 管道、MCP、命令内核、PIN/凭据、配置持久化、任务状态机、Cron、剪贴板过滤，无跳过用例。
- **盲区**：`src` 有 18 个 Window/Dialog，tests 只实例化 9 个，其中 7 个是零断言渲染快照；`FloatingPanelWindow`、`FirstRunWindow`、`VerifyPinWindow`、4 个模块设置窗口从未被实例化。缺陷高发的 code-behind 恰是盲区。
- **缺口**：TaskScheduler 数据保护（损坏/只读/版本/外部修改）、MCP 宿主未启动→恢复、能力 PIN 元数据契约、XAML 硬编码 CJK 静态检查、安装器迁移逻辑。
- **建议**：无断言渲染用例改名 `*_Snapshot` 并标 `[TestCategory("Visual")]` 默认跳过；XAML 校验用 `XDocument` 替代正则；`Thread.Sleep` 改 `ManualResetEventSlim`/可控时钟；窗口逻辑抽 ViewModel/Mapper 后补纯逻辑断言。

---

## 4. 界面与人机交互

### 4.1 视觉一致性【实证】

| 编号 | 级别 | 问题 | 证据 |
|---|---|---|---|
| **V1** | 高 | `App.xaml:1-5` 为空壳，无 `Application.Resources`；至少 11 个 XAML 各写一份"现代极简窄滚动条"；按钮样式 5 套命名（`ModernBtn`/`PrimaryBtn`/`PrimaryButtonStyle`/`SecondaryButtonStyle`/`ModernButtonStyle`）；主蓝 3 个值 `#FF1A73E8`(`TrayContextMenu.xaml:43`)、`#FF1F6FEB`(`FirstRunWindow.xaml:42`)、`#FF2563EB`(`AboutWindow.xaml:90`)；`TrayContextMenu.xaml:33-63` 与 `FloatingPanelWindow.xaml:53-83` 菜单模板逐行重复 | 改主色需动十几个文件，新增窗口必然跑偏 |
| **V2** | 高 | 全仓 `DynamicResource` / `AppsUseLightTheme` **0 命中**；`TrayContextMenu.xaml:6,11`、`FloatingPanelWindow.xaml:143` 硬编码白底深字 | Windows 深色模式下托盘/浮窗仍白底，与其它应用割裂 |
| **V3** | 中 | 尺寸策略不一：`TaskManagerWindow.xaml:5` `Width=1150 MinWidth=960` 但 10 列合计约 1035 DIP；`FirstRunWindow.xaml:5`、`VerifyPinWindow.xaml:5` 无 Min 尺寸；`ClipboardHistorySettingsWindow.xaml:5` 固定 460×404 | 缩到 960 时右侧列被挤出；英文长文案溢出固定宽度窗口 |

**改进**：新建 `Assets/Styles/Theme.xaml`，把 `PrimaryButton/SecondaryButton/DangerButton/CardBorder/ThinScrollBar/MenuItemTemplate`、颜色（`Brush.Primary/Surface/Text/Border`）、字号、间距收敛为键；`App.xaml` 用 `MergedDictionaries` 引入；颜色改 `DynamicResource` 并加 Light/Dark 两套，按注册表 `AppsUseLightTheme` 切换；统一最小尺寸基线与列宽比例策略。

### 4.2 信息架构与交互反馈【实证】

- **V4 [高] 托盘与浮窗是两套菜单**：`TrayContextMenu.xaml:145-157` 有"关于"无"语言"；`FloatingPanelWindow.xaml.cs:569-602` 有语言子菜单无"关于"；`docs/USAGE.md:29` 却把"语言"写在托盘菜单树里。另 `TrayContextMenu.xaml:145` 硬编码 `InputGestureText="Win+Alt+C"`，用户改热键后仍显示旧组合。→ 抽 `BuildHostMenuItems()` 单一构建器供两处复用，热键文案从配置绑定。
- **V5 [高] 热键注册失败全程静默**：`App.xaml.cs:329-332` 与 `Core/ModuleBase.cs:262,265` 用 `out _` 丢弃 `HotkeyService.cs:101-112` 已生成的可读错误（语言包 `zh-CN.json:629-631` 已有 `HotkeyErr.ConflictInternal/OccupiedExternal`）；`docs/USAGE.md:208` 却称"任务日志记 hotkey register failed"。→ 接住 `out error`，失败时 `ShowBalloon(error)`；设置窗口行内红字；气泡按 Info/Warning/Error 分级并附"打开日志"。
- **V6 [高] 设置窗口普遍无脏检查**：仅 `TaskEditDialog.xaml.cs:476` 有放弃确认；`ConfigEditorWindow.xaml.cs:234`、`AppAutoMuteSettingsWindow.xaml:262`、`AudioSwitchSettingsWindow.xaml:293` 直接关窗。→ 抽 `DirtyCheckBehavior` 统一"保存/放弃/取消"三选。
- **V7 [中] 改 PIN 后提示与落盘不一致**：`ConfigEditorWindow.xaml.cs:228-230` 显示"已修改*"并弹"新 PIN 已生成"，实际只写 `_editing` 草稿，必须再点"保存并应用"；`:234` 关窗即丢。→ 改 PIN 成功后立即落盘并重载 PIN 服务，或关闭时强制脏检查。
- **V8 [中] 菜单/操作失败提示无原因**：`FloatingPanelWindow.xaml.cs:636-638` 只拼"执行失败"，不含异常信息。→ 附加 `ex.Message` 首行 + 日志路径。
- **V9 [中] 任务管理器无默认排序、无列头排序**：`TaskManagerWindow.xaml.cs:228-232` 按 `tasks.json` 物理顺序 `Add`，全文件无 `OrderBy`；`GridViewColumn` 无点击排序。10 列高密度反而更难扫读。→ 默认按"运行中 → 启用 → 名称"排序，列头加 `GridViewColumnHeader.Click` 与升降序指示。

### 4.3 国际化【实证】

- **V10 [高] 任务管理与编辑窗口整体绕过本地化**：`TaskManagerWindow.xaml` 与 `TaskEditDialog.xaml` 的 `loc:Loc` 绑定数均为 **0**（对比旧 `TaskEditorWindow.xaml` 为 97）。含中文的行数（含注释与默认文案，按脚本统计）分别为 58 / 103；典型：`TaskManagerWindow.xaml:5` `Title="自动化任务管理 - CarroDesk"`、`:162` `Content="＋ 新建任务"`、`:378` `$"确定要删除任务 \"{row.Name}\" 吗？"`、`:411` `"任务调度总开关已禁用，请先启用总开关！"`。→ 切 en-US 后整个任务管理链路仍是中文；把这批字符串搬入 `Tasks.*`（可复用已有 `Tasks.DeleteConfirm`），XAML 改 `{loc:Loc}`、C# 改 `Loc.T()`，并加一条 CI 静态检查拦截新增裸中文。
- **V11 [中] C# 一次性取值的文本不随语言切换刷新**：`LocExtension.cs:30-39` 用 `Binding` 索引器、`I18nService.cs:65-67` 触发 `PropertyChanged("Item[]")`，XAML 静态文本即时生效（**做得好**）；但 `LanguageChanged` 订阅者仅 3 处，`ConfigEditorWindow.xaml.cs:82-88` 的 `ModeText/PinStatusText` 与 `TaskManagerWindow.xaml.cs:257` 的 `"共 {0} 个任务"` 不刷新。→ 改绑定或订阅后 `RefreshDynamicTexts()`。
- **V12 [低] 零散硬编码与死键**：`AboutWindow.xaml:187-188,210`（"部署模式"/"共 8 个模块"，语言包已有 `About.ModePortable`/`About.ModulesCount`）、`AudioSwitchSettingsWindow.xaml:197,211`（"扬声器"/"耳机"，已有 `Audio.Speaker/Headset`）；`VerifyPin.PenaltyWait` 从未被引用。
- **优点**：`zh-CN` / `en-US` 各 **838 键**、双向差集为 0；XAML 普遍用 `{loc:Loc Key, Default='…'}` 双保险。

### 4.4 无障碍【实证】

全仓 `AutomationProperties` / `TabIndex` / `FocusVisualStyle` **0 命中**。`FloatingPanelWindow.xaml:26-32` 的 `ToolButtonStyle` 设 `Focusable="False"`，4 个工具按钮用 emoji 作图标且部分 `ToolTip` 只在 code-behind 设置；所有 `PasswordBox`（FirstRun/VerifyPin/Lock）无标签、无可访问名；`LockWindow.xaml:84-85` 逃生按钮 11px、灰色 `#8B949E`、透明底，位于卡片最底部。

**改进**：全量补 `AutomationProperties.Name/HelpText`（走语言包）；移除 `Focusable=False` 并显式 `TabIndex`；emoji 改矢量 `Path`；逃生按钮提到 13px、提高对比度、加键盘访问键；`app.manifest` 声明 `PerMonitorV2` 并逐窗口验证高 DPI。

### 4.5 做得好的交互设计【实证】

1. 语言包键集合完全对齐；`LocExtension` 用 `Binding` 索引器，切语言时 XAML 文本即时刷新。
2. 锁屏全链路容错：逃生通道 `LockWorkStation()`（`LockWindow.xaml.cs:357-367`）、CapsLock/NumLock 实时提示（`:253-272`）、惩罚等待倒计时（`:274-292`）、关闭拦截 + 500ms 兜底定时器防全屏残留（`:383-441`）、副屏独立遮罩。
3. 危险操作确认覆盖较全：删除任务、清空列表、恢复默认、改 PIN 先验原 PIN、退出验 PIN。
4. 失败回滚：`ConfigEditorWindow.xaml.cs:185` 保存异常 `previous.CopyTo(target)` 回滚；`FloatingPanelWindow.xaml.cs:37-42,54-59` 钉住/锁定保存失败复原视觉。
5. 空状态与状态双编码：`TaskManagerWindow.xaml:320-324` 空列表引导、状态圆点+文字、启用/触发徽标+ToolTip。
6. 自助排障：`AboutWindow.xaml:266` 复制诊断信息；`McpStdioServer.cs:111-123` 的 `instructions` 下发 PIN 策略/错误码/能力边界；`HostCommands.cs:57-71` 的 `host.guide` 返回内嵌手册。
7. 内存采样仅窗口可见时执行（`TaskManagerWindow.xaml.cs:53-90`）；`UnlockOnResume` 默认 false 且语言包有完整风险说明。

---

## 5. 易用性与文档一致性

### 5.1 首启与默认值

| 编号 | 级别 | 问题 | 证据 | 改进 |
|---|---|---|---|---|
| **E1** | 高 | 首启向导单步、取消即静默退出、规则不前置 | `App.xaml.cs:143-155` `ShowDialog() != true → Shutdown(0)`；`FirstRunWindow.xaml:90-94` 两个 PasswordBox 无用途/规则说明；纯数字、≥4 位只在出错后显示 | 加第 0 步模块概览 + "PIN 用于解锁/退出验证、忘记需删配置重置" + 规则前置；取消时弹确认 |
| **E2** | 高 | 默认开箱即静音常用应用 + 默认开启锁屏(5 分钟)与开机自启，无知情同意 | `AppAutoMuteConfig.cs:7,12`（`Enabled=true`，默认 `chrome.exe/QQMusic.exe/cloudmusic.exe`）；`ScreenLockConfig.cs` `Enabled/AutoLockEnabled=true, IdleMinutes=5`；`AppSettings.cs:7` `AutoStart=true` | `TargetApps` 默认空列表；向导加两个显式开关；首次实际静音时发一次解释性气泡 |
| **E3** | 中 | 缺"为什么没生效"自诊断入口 | 仅 `AboutWindow.xaml:266` 复制诊断信息、`docs/USAGE.md:206-213` FAQ；托盘/浮窗无入口，而热键失败、模块未运行、配置校验失败都只写日志 | 托盘加"诊断"项聚合热键冲突、模块状态、配置校验、日志目录、复制诊断信息 |
| **E4** | 中 | 配置编辑器暴露原始 `config.json` 字段（含 `Ipc`、`ModuleConfigs`），对普通用户不友好 | `ConfigEditorWindow.xaml` | 提供"基础/高级"分区或指向各模块设置窗口 |

### 5.2 CLI / MCP 人机可用性

| 编号 | 级别 | 问题 | 证据 | 改进 |
|---|---|---|---|---|
| E5 | 中 | `--json` 在错误路径失效：失败时向 stderr 写纯文本，stdout 为空，`args.Json` 不参与 | `ControlForwarder.cs:31-38` | 输出 `{"ok":false,"code":-32002,"message":"..."}` 并固定 schema 写入文档 |
| E6 | 中 | 无 `--help/-h/--version`，无参运行先报 `not a ctl invocation` 再打 usage；PIN 只能 `--pin 1234` 明文传参（进 shell 历史与进程命令行） | `Program.cs:30-59`、`ControlArgs.cs:49-53,89` | 识别 help/version，usage 带能力摘要与错误码表；省略 `--pin` 时从 TTY 无回显读取或读 `CARRODESK_PIN` |
| E7 | 中 | `--timeout` 语义与实现不符（只连不读，见 C19）；宿主未运行只返回 `-32020 pipe transport failed: <原始异常>` | `PipeRpcClient.cs:37-43` | 实现整体截止时间或改名 `--connect-timeout`；-32020 专门提示"请先启动托盘程序" |
| E8 | 低 | MCP `instructions` 声称覆盖锁屏/音频/任务，实际 IPC 只有唤醒/服务；`tools/call` 的 `pin` 不在 `inputSchema` 中 | `McpStdioServer.cs:212-214` | 文案改"以 tools/list 为准"；schema 声明可选 `pin` |
| E9 | 低 | `initialize` 原样回显客户端 `protocolVersion`，未做版本协商 | `McpStdioServer.cs:104` | 按支持列表协商 |

### 5.3 文档漂移（择要，完整表见同日报告 §5 的 D1–D19）

| 文档位置 | 文档说法 | 实际 | 处置 |
|---|---|---|---|
| `README.md:12` | 11 种触发器 | `TaskTriggerType` 共 10 种（`TaskDefinition.cs:11-20`） | 改 10 |
| `README.md:41,42` | Costura 单文件 / NSIS 安装包 | `PublishSingleFile` / Inno Setup 6（`CarroDesk.iss`） | 更正 |
| `README.md:55` | 近期变更日志指向 `CHANGES-20260926.md` | 最新为 `CHANGES-20261109.md` | 改指向最新 |
| `docs/DESIGN.md:5,43,175,176` | net48 / C# 7.3 / 291 通过 / 免安装运行时 | net10.0-windows / 411 通过 / 框架依赖需 .NET 10 Desktop Runtime | 重写 §2/§8 |
| `docs/DESIGN.md:60` | `ModuleBase` 提供 `GetCommands()` | 无此方法，能力靠 `ICommandProvider` | 更正 |
| `docs/DESIGN.md:81` | 防抖 250ms；托盘含"语言" | `DebounceMs=150`（`DynamicTrayController.cs:25`）；语言项已移除 | 更正 |
| `docs/USAGE.md:29,204,208,213` | 托盘含语言 / PIN 6 位 / 热键冲突写日志 / 客户端自动恢复 | 语言项已移除 / PIN 4–16 位 / 不写日志 / MCP 不恢复 | 逐条更正（配合 C9、V5） |
| `docs/AI-AGENT-MANUAL.md:13,58` | "问用户要 6 位口令"、"11 项能力" | 4–16 位；表格实列 15 项 | 统一位数；删静态数字改由 `registry.Count` 注入 |
| `docs/MODULE-DEVELOPMENT-GUIDE.md §7.6` | 在 `App.xaml.cs` 调 `Modules.RegisterModule` | 实际在 `Host/Modules/ModuleRegistry.cs` | 更正并补检查清单 |
| `scripts/release.py:120`、`CarroDesk.Cli.csproj:21-22` | docstring/注释写 Costura | 已改 PublishSingleFile | 更正 |
| `scripts/installer/CarroDesk.iss:262-267,297-301,527-535` | 声明中英双语 | `[Code]`/目录标签硬编码中文，`Languages/` 仅中文 isl | 抽 `{cm:...}` 或删 `.nsi/.isl` 死配置 |

**另**：`CarroDesk.iss:486-497` 的 `MigrateDataIfNeeded` 只复制 `config.json`/`tasks.json` 并**只创建**空 `scripts` 目录 → Roaming ↔ Portable 切换后 `tasks.json` 里引用的 `scripts/xxx.js` 全部失效。建议 `FileCopy` 整个目录后再写标记文件，失败则中止并提示。

---

## 6. 改进与优化路线图

> 原则：先止血（数据/安全/对外接口），再体验一致性，最后架构债务；每批独立提交、各自跑 `dotnet build` + `dotnet test`，每项配一条能捕获缺陷的回归用例（本轮发现的新缺陷几乎全部落在既有测试未覆盖路径）。

### 第一批：止血（改动小、收益大，建议同批或分 2–3 次提交）

1. **任务配置数据保护**（C7）：仅 `JsonException`/结构校验失败进入 `RecoverFromCorruptFile`；IO 异常保留文件只上报；`LoadResult.Errors/SchemaVersion/ReadOnly` 传到窗口，有错误/版本更高时显示横幅并禁用保存；`Save` 前校验 mtime 拒绝覆盖外部修改。
2. **让失败可见**（C12、V5、C15、C17）：锁屏失败（含未配 PIN）、热键注册失败、剪贴板落盘/Flush 失败 → 统一气泡 + Error 日志 + 菜单态；PinGuard 状态文件损坏时保守保留封锁；管道 DACL 构造失败 fail-fast 告警。
3. **对外接口契约**（C9、C10、E5、E6）：MCP 拉取失败不缓存空表（或带短 TTL 重试）；能力元数据加 `pinPolicy` 并同步 MCP `inputSchema`；`--json` 错误结构化；CLI 加 `--help/--version`。
4. **进程治理与默认安全**（C8、C11、E2）：去掉 `isDetach &&`，wait 模式按复选框持久化 `KillWithHost`；白名单为空直接 return；`TargetApps` 默认空列表。
5. **新编辑器与采样生命周期**（C1–C4、C13、C14）：采样加 CTS/代际标志，回投校验 `_closed`/可见性；搜索 200ms 防抖 + 就地更新；测试句柄与 CTS 正确释放；`async void` 改局部 CTS + `IsLoaded` 门闩。
6. **剪贴板过滤追溯**（C5、C6）：规则变更时重扫历史删除命中项；服务层裁剪规则数与关键字长度；匹配移出 UI 线程。
7. **PIN 修改即时生效**（V7/E1 相关）与**安装器 `scripts/` 迁移**（§5.3）。

### 第二批：体验与一致性

1. 建 `Theme.xaml` 全局资源字典，收敛 5 套按钮样式/窄滚动条/2 套菜单模板；颜色改 `DynamicResource` 并加 Light/Dark；统一窗口最小尺寸与 10 列宽度策略。
2. 任务管理与编辑窗口全面本地化（V10），加 XAML 裸中文静态检查；修 V11 动态文本刷新、V12 零散硬编码。
3. 托盘/浮窗菜单合并为单一 `BuildHostMenuItems()`；移除硬编码 `Win+Alt+C`；同步 `USAGE.md` 菜单树。
4. 抽 `DirtyCheckBehavior` + 统一 `Validate()`/行内错误 + `PromptDialog`；任务管理器默认排序与列头排序（V9）。
5. 首启向导加功能概览、PIN 用途/找回说明、自启与锁屏显式开关；托盘加"诊断"入口（E1–E4）。
6. 无障碍：全量 `AutomationProperties.Name`、显式 `TabIndex`、矢量图标、逃生按钮对比度；`PerMonitorV2` 实机验证（V1–V3、§4.4）。
7. 统一更正文档 D1–D19；`host.guide` 能力清单去静态数字。

### 第三批：架构与债务

1. 删除 `TaskEditorWindow`（1,407 行 + XAML）、4 个 Fody 残留文件、失效的 `CleanCliOutputJunk`、`.nsi/.isl` 死配置及 Q2–Q8 类死代码（G5、G6）。
2. 抽 `CarroDesk.Protocol` 无 WPF 类库，CLI 解除对宿主引用（G1）；`CommandErrorCodes` 等契约下沉 Core（G2）；Dispatcher 构造注入（G4）。
3. 命名空间批量重命名（G3）；配置序列化统一、删除 `ConfigReloaded` 死事件（G8）；`AppSettings.DisabledModules` 让 `Disabled` 状态真正可用。
4. 开 `Nullable` + analyzer（Core/Host/测试项目先行）、恢复测试项目默认 glob、加 build+test CI（G7）。
5. UI 线程减负：Awake 进程枚举、ScreenLock ARP/Ping 探测、Task 触发器重建移出 UI 线程；`TaskDefinitionMapper` + 字段往返测试（15 个超 500 行文件优先拆这两个编辑器与服务）。
6. 专项设计与实机验证：M6 单实例互斥体跨宿主、M19/M24 多屏 DPI、M16 白名单轮询、安装器 .NET 10 运行时前置检查。

### 建议补的回归用例（按优先级）

| 用例 | 断言 |
|---|---|
| TaskScheduler 数据保护 | 损坏 JSON / 只读文件 / IO 异常 / `SchemaVersion` 更高 / 保存前外部修改 → 不覆盖、不删除、错误可见 |
| 状态机不变量 | `ActiveInstances` 与真实句柄数一致；retry/restart/cancel/配置删除后回 Idle；`WaitingRetry` 可停止 |
| MCP 恢复 | 宿主未启动 → `tools/list` 空 → 启动宿主 → 再次 `tools/list` 必须出现工具 |
| 能力元数据契约 | `RequiresPin` / `RequiresPinFor` / `pinPolicy` / `inputSchema` 四者一致 |
| UI 冒烟 | 托盘与浮窗菜单项集合一致、脏检查、PIN 修改未保存、`TaskEditDialog` 字段往返 |
| 本地化静态检查 | XAML 中无 `loc:` 的 CJK 字面量（白名单化存量） |
| 采样生命周期 | 窗口关闭/最小化后不再有性能读取与回投；搜索不触发全表重建 |

---

## 7. 附录

### A. 量化数据一览

| 项目 | 数值 |
|---|---|
| 构建 / 测试 | 0 警告 0 错误 / 411 通过 0 失败 0 跳过（50s） |
| 源码规模 | src 180 文件 36,447 行；cli 1 文件 61 行；tests 47 文件 10,758 行 |
| 超 500 行文件 | 15 个 / 11,631 行（占 src 32%） |
| 空 catch | 233 处 / 62 文件 |
| 语言包 | zh-CN / en-US 各 838 键，差集 0 |
| 无障碍/主题关键字 | 5 类全部 0 命中 |
| 无本地化绑定的窗口 | `TaskManagerWindow.xaml`、`TaskEditDialog.xaml`（`loc:Loc` 各 0；对照旧编辑器 97） |
| 死代码 | `TaskEditorWindow.xaml.cs` 1,407 行 + `.xaml`；`src/FodyWeavers.xml`、`cli/.../FodyWeavers.xml` + 2 份 `.xsd`；`CarroDesk.nsi` 533 行 + `.isl` |
| CI | 无 `.github/workflows` |

### B. 本次交叉复核记录与【待验证】项的验证方法

**已回读源码确认**（同日报告提出、本次复核）：C7 异常即重写 `[]`（`TaskConfigService.cs:198-202,253-274`）、C8 `KillWithHost = isDetach && (...)`（`TaskEditDialog.xaml.cs:550`）、C9 MCP 空表缓存（`McpStdioServer.cs:141-150`）、C11 白名单空全静音（`AppAutoMuteModule.cs:167-171,214-231`）。

| 待验证项 | 验证方法 |
|---|---|
| wait 模式重试 `ActiveInstances` 漂移 | wait 任务脚本首次失败、重试成功；退出后查状态应为 Idle（当前可能"运行中"且无 PID） |
| `CatchUpMissed` 重复补跑 | 09:00 daily 任务正常跑过后，10:00 保存任意任务触发 Reload，观察是否再跑一次 |
| 重启 300ms / `OnStopCompleted` 槽位不同步 | 用顽固进程树（>300ms 才退出）点"重启"，观察是否无反应或 UI 显示空闲但进程在跑 |
| M6 单实例互斥体跨宿主 | `killWithHost=false` + `singleInstance=true` 启动 → 结束宿主 → 重启，统计子进程数 |
| M16 白名单漏静音 | 保持前台不变，后台启动播放器，观察音频会话是否被静音 |
| M19/M24 多屏 DPI | 150% 缩放 + 副屏负坐标，观察锁屏覆盖范围与浮窗 Tray 定位 |
| C20 PID 复用 | 3s 内让任务进程退出并构造 PID 复用，观察内存列数值 |

### C. 审查方法局限

- 未做实际运行时验证（多显示器非 100% 缩放、真实服务 DACL、受限账户睡眠、读屏软件、密码管理器排除格式），相关项标【待验证】。
- 未审计 `docs/old/`、`temp/`、`release/` 产物与 `.git` 目录。
- 同日 `docs/CODE-REVIEW-20261109.md` 中的 Q/P/U 编号条目未逐条复核（仅复核其 4 条高风险结论 + 文档漂移表抽样），两份报告若结论不一致，以本报告标注的实证行号为准再回读。
- 本报告只做审查，**未修改任何源码、配置或已有文档**；仅新增本报告文件。
