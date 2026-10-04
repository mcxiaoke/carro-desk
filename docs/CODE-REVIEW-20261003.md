# CarroDesk 代码审查报告

> 审查日期：2026-10-03（GMT+8）；**2026-10-04 第二轮独立复核并修订**
> **修复状态：2026-10-04 第二轮修复已落地 12 批，见第十四节；测试基线 336 通过 / 0 失败 / 0 跳过**
> 审查范围：整个仓库（`src/` 149 个源文件，约 3.5 万行；`cli/`、`tests/`、`scripts/`、工程配置）
> 审查方式：第一轮按宿主核心 / IPC / 各功能模块 / UI / 基础设施 / 测试 分线逐行阅读；第二轮**独立重审全量源码后再与本报告交叉核对**，所有结论均对照实际代码
> 目标框架：.NET Framework 4.8 + WPF，C# 7.3
> 结论性质：**只报告在代码中确实存在的问题**，不做推测性夸大；对无法确认严重性的事项标注了"待确认"
> 验证状态：**已实际执行 `dotnet build`（0 警告 0 错误）与 `dotnet test`（336 通过 / 0 失败 / 0 跳过）**

---

## 一、总体评价

这是一个**架构意识明显高于一般个人工具项目**的代码库：模块化宿主、瘦上下文（`IModuleContext` 只读 pull）、能力白名单（`CommandRegistry`）、纵深防御（能力校验 → 模块状态 → 参数 schema → 口令 → 限流 → 审计）、Job Object 进程治理、DDC 防抖队列、原子文件写入、剪贴板异步合并落盘等，都属于有设计、有注释、有对应防御代码的实现。多数资源释放（COM RCW、DDC 句柄、`ServiceController`、`Mutex`）处理到位，i18n 键集合（772/772）完全对齐。

但本次审查也发现**若干真实且影响明确的缺陷**，其中三项可归类为"严重"：一个 C# 重载解析陷阱导致大量错误提示丢失、一个任务编辑器保存路径静默丢字段（数据丢失）、一个剪贴板模块无条件采集并明文持久化敏感内容。此外存在一处安全相关的并发缺陷（PIN 校验）和一处锁屏焦点/钩子耦合导致的安全隐患。

**一句话判断**：骨架和工程素养好，但"最后一公里"的健壮性与安全细节仍有真实的坑，尤其是编辑器数据往返、i18n 文案格式化、剪贴板隐私这三块，建议优先修。

**第二轮（2026-10-04）结论**：先独立重审全量源码、再与本报告交叉核对。新增 14 项（见第三节），其中 **3 项锁屏安全绕过（R1/R2/R3）应排在已修完的 S1/S2/S3 之前** —— 后者是数据与隐私问题，前者是可直接绕过认证的活路径。第一轮标注 ✅ 的 20 项修复经逐条 grep 复核**全部属实**；第一轮"未能编译/未跑测试"的局限已关闭（实际构建 0 警告 0 错误、315 项测试全通过）。

**第三轮（2026-10-04 下午）结论**：第二轮的 14 项新增已按"改动小、收益大、风险低"分 **12 批**落地（R1/R2/R3/R4/R5/R6/R7/R9/R10/R11/R12/R13/R14/R15/M15 已修，R8 部分修复），新增 21 个回归用例，其中 5 个经实证确认能捕获对应缺陷；测试基线推进到 **336 通过 / 0 失败**。修复过程中另发现并修掉两个真实缺陷（`AudioService` 在已关闭 Dispatcher 上 `Invoke` 永久阻塞、`ProcessJob` 缺终结器导致句柄真泄漏）。仍未处理的主要是需要真机/多屏环境验证的项（H1/M6/M14/M16/M19/M24）与需产品确认的行为变更（M7/M9），逐条见第十四节。

---

## 二、问题汇总

| 编号     | 严重度 | 一句话描述                                                                                             | 位置                                                                 |
| ------ | --- | ------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| **R1**   | **严重** | **锁屏期间托盘菜单未被禁用，可经托盘打开锁屏设置并关闭"启用"解除锁定，全程无需 PIN** ✅已修 | `ScreenLockModule.cs:546`                                  |
| **R2**   | **严重** | **`UnlockOnResume` 默认 `true`：任何导致 Windows 会话解锁的事件都会免 PIN 解除锁屏** ✅已修 | `ScreenLockConfig.cs:22`、`ScreenLockModule.cs:300`                |
| **R3**   | **严重** | **PIN 失败计数与封锁窗口仅存内存，重启宿主即清零，限流可被绕过** ✅已修 | `PinService.cs:106-131`                                          |
| S1     | 严重  | `Loc.T(key, string)` 命中错误重载，含占位符文案永久显示字面量 `{0}`，错误详情被静默丢弃（10+ 处） ✅已修 | `Services/Localization/Loc.cs:13` 及多处调用点                      |
| S2     | 严重  | 任务编辑器保存会静默清空 `singleInstance` / `stableUptimeSec`；"复制任务"还会丢失 detach/restart 全部配置 ✅已修 | `TaskEditorWindow.xaml.cs:546,403`                     |
| S3     | 严重  | 剪贴板历史不检查 Windows 排除格式，无差别采集含密码在内的内容并以明文 JSON 落盘 ✅已修（排除格式 + 长度上限；**存储仍为明文**） | `ClipboardHelper.cs:14-19`、`ClipboardHistoryService.cs:80` |
| **R4**   | 高   | **`IsTaskStillEnabled` 用引用相等判定任务存活，配置重载摧毁运行中常驻任务的守护/重试链** ✅已修 | `TaskSchedulerService.cs:872-878`                                   |
| **R5**   | 高   | **`GetRecent()` 返回内部列表活视图，并发写入致托盘 Tasks 菜单整块消失** ✅已修 | `TaskSchedulerService.cs:898-900`                                   |
| **R6**   | 高   | **`TryStart` 异常路径产生无人可停止的僵尸进程，并永久泄漏 Job 句柄** ✅已修 | `TaskRunner.cs:281-286`、`ProcessJob.cs:148`                        |
| **R7**   | 高   | **`KillTree` 无条件 `taskkill /PID`，pid 回收后可能误杀无关进程树** ✅已修 | `TaskProcessHandle.cs:229-238`                                     |
| **R8**   | 高   | **任务日志在全局锁内做同步文件 IO 且逐行写入，回调不及时会顶满子进程 stdout 管道** ◑部分修复 | `TaskLogger.cs:49-86`                                              |
| H1     | 高   | 锁屏键盘钩子无条件放行数字/Enter/Backspace/Shift，而自动锁屏难以稳定夺回前台 → PIN 可能经键盘泄漏到下层窗口 ⚠️部分修复 | `KeyboardBlocker.cs:112`                     |
| H2     | 高   | wait 模式"停止任务"被判为失败；`retry>0` 时"停止"会触发自动重启（停止形同虚设） ✅已修 | `TaskSchedulerService.cs:486,509`                        |
| H3     | 高   | 悬浮面板菜单区无滚动/限高，模块较多时窗口超出屏幕，底部菜单（含退出）不可点击 ✅已修 | `FloatingPanelWindow.xaml:6,186`                               |
| H4     | 高   | `PinGuard` 锁外并发调用共享的 `HostPinService.Verify`，其内部状态无同步 → 并发下可能误判 PIN ✅已修 | `HostPinService.cs:21,41`                     |
| **R9**   | 中   | **`AudioService.RaiseDevicesChanged` 全仓无调用点，设备变化后托盘永不刷新（死代码）** ✅已修 | `AudioService.cs:32-35`、`IAudioService.cs:11`                      |
| **R10**  | 中   | **宿主退出时 `killWithHost=false` 的常驻任务弹出虚假"失败"通知** ✅已修 | `TaskSchedulerService.cs:862-866`、`TaskProcessHandle.cs:153`      |
| **R11**  | 中   | **任务加载错误使编辑器永久无法保存，用户只能手改 JSON** ✅已修 | `TaskEditorWindow.xaml.cs:794-798`                                   |
| **R12**  | 中   | **`TaskConfigService.Save`/`Load` 无串行化，与后台重载并发时可互相覆盖** ✅已修 | `TaskConfigService.cs:250-277`                                      |
| **R13**  | 中   | **常驻任务各占一个线程池线程（约 10-20 个即饱和）** ✅已修 | `TaskProcessHandle.cs:114`                                          |
| **R14**  | 中   | **`WINDOWPOS.flags` 声明为 `uint`，清除的是 `SWP_FRAMECHANGED`(0x0020) 而注释写 `SWP_NOZORDER`(0x0004)** ✅已修 | `LockWindow.xaml.cs:139,157`                          |
| **R15**  | 中   | **剪贴板历史 ToolTip 绑定未截断全文，悬停即加载超大文本** ✅已修 | `ClipboardHistoryWindow.xaml:157`                                   |
| M1     | 中   | 配置 JSON 语法损坏时静默回退默认配置启动，不提示不记日志（可能导致 PIN"丢失"触发首启向导） ✅已修 | `ConfigService.cs:266-292`                      |
| M2     | 中   | `CommandHost` 超时后 handler 仍后台运行且不可取消；线程池被额外占用                                                      | `CommandHost.cs:119-129`                                           |
| M3     | 中   | `int` 类型参数收到 JSON 小数（如 `5.0`）时静默降级为字符串 ✅已修         | `CommandHost.cs:199-268`                                           |
| M4     | 中   | `CommandRegistry.Rebuild` 失败后残留半注册状态，日志却称"降级为空" ✅已修 | `CommandRegistry.cs:22-33`                      |
| M5     | 中   | 日志文件 `log.txt` 无轮转，长期运行无界增长（审计日志已有轮转，两者不一致） ✅已修    | `DefaultLoggerService.cs:38-51`                                    |
| M6     | 中   | `singleInstance` 互斥体由宿主持有，`killWithHost=false` 时无法阻止常驻子进程重复拉起                                     | `TaskSingleInstanceMutex.cs:44-84`                                 |
| M7     | 中   | `AllowConcurrent` 时运行槽被覆盖，前一个实例句柄丢失，无法查询/停止                                                       | `TaskSchedulerService.cs:400-408`                                  |
| M8     | 中   | Cron 正常到点也被标记 `cron-catchup`（`missed` 恒真），复检分支不可达 ✅已修 | `CronTrigger.cs:76-93`                                             |
| M9     | 中   | Cron `5/2` 语义与标准不一致（应表示 5,7,9…，实际只匹配 5）；永不可达表达式静默失效无任何日志                                          | `CronHelper.cs:211-226`、`CronTrigger.cs:52-57`                     |
| M10    | 中   | `SetGlobalEnabled` 持久化失败回滚路径可递归直至栈溢出 ✅已修     | `TaskSchedulerService.cs:110-132`                                  |
| M11    | 中   | Job Object 挂接失败时 `killWithHost=true` 静默降级为"孤儿进程"，日志还错报为 false ✅已修 | `TaskRunner.cs:262-275`                    |
| M12    | 中   | 任务/剪贴板/显示器设置窗口不校验热键（与 AudioSwitch/AppAutoMute 不一致），非法热键静默失效 ✅已修 | `ClipboardHistorySettingsWindow.xaml.cs:44-71` 等                   |
| M13    | 中   | Awake 用户手动"关闭"会被任意一次配置重载撤销，表现为"关了又开" ✅已修     | `AwakeService.cs:200-204`                                          |
| M14    | 中   | Awake 每 5 秒在 UI 线程 `Process.GetProcesses()` 全量枚举，周期性卡顿 ⏸待真机验证 | `AwakeService.cs:519-521`                                          |
| M15    | 中   | Awake `SetSuspendState` 未启用 SE_SHUTDOWN_NAME 权限，受限账户静默失败 ✅已修 | `AwakeService.cs:392`                                              |
| M16    | 中   | AppAutoMute 白名单模式只在"前台切换"时扫描一次，期间新起的发声进程漏静音                                                       | `AppAutoMuteModule.cs:163-247`                                     |
| M17    | 中   | 显示器设置表单格可直接编辑，绕过 0–100 与时间格式校验；时间非法静默归零 ✅已修 | `MonitorTimeSetting.cs:30` |
| M18    | 中   | DDC 一次 `ManagementException` 即永久禁用 WMI 回退（瞬时故障被当成"系统不支持"） ✅已修        | `MonitorDdcService.cs:679,714`                                     |
| M19    | 中   | 多显示器全屏：物理像素与 DIP 混用，清单仅系统级 DPI 感知 → 非 100% 缩放下锁屏窗口错位                                              | `LockWindow.xaml.cs:72,117`、`app.manifest:5`                       |
| M20    | 中   | `LockSafe()` 在非 UI 线程提前返回 true（锁定失败被吞） ✅已修   | `LockController.cs:189`                                            |
| M21    | 中   | `SystemIdleService.UserActiveDetected` 为电平触发，用户活跃时每秒触发一次 ✅已修 | `SystemIdleService.cs:126`                      |
| M22    | 中   | `services.status` 未逐服务隔离，单个服务访问被拒会中断整个列表 ✅已修 | `WindowsServiceControlAdapter.cs:14`        |
| M23    | 中   | 服务处于过渡态（StartPending/StopPending）被误判为"服务不可用" ✅已修 | `WindowsServiceControlAdapter.cs:30,45`          |
| M24    | 中   | 悬浮面板位置预设固定用主屏工作区，副屏无法定位                                                                           | `FloatingPanelWindow.xaml.cs:270`                                  |
| M25    | 中   | `ConfigEditorWindow` 关闭不提示未保存，语言/自启/PIN 草稿被静默丢弃                                                   | `ConfigEditorWindow.xaml.cs:234`                                   |
| M26    | 中   | CLI `ctl` 未固定 stdout 编码，重定向时中文/JSON 乱码（`--mcp` 已固定）✅已修        | `cli/Program.cs:37`、`ParentConsole.cs:24`                          |
| M27    | 中   | 安装脚本 i18n 不完整：声明中英双语但大量文案硬编码中文                                                                    | `scripts/installer/CarroDesk.iss`、`.nsi`                           |
| M28    | 中   | 测试"名不副实/恒真断言"：`UiRenderingTests` 声称校验重叠却无断言、正则解析嵌套 Grid 不可靠、`DevicePresenceAndAutoLockTests` 恒真断言 ◑部分修复 | `tests/*`     |
| L1–L20 | 低   | 详见第四节（死代码、释放不完整、i18n 漏网、可访问性、测试隔离等）                                                               | —                                                                  |

> 累计：严重 6 项 / 高 8 项 / 中 35 项 / 低 20 项，共 69 项（第一轮 55 项 + 第二轮新增 14 项）。
> 状态标记：✅已修 = 已在代码中修复并经实证复核；⚠️部分 = 仅修 related 路径，根因未除；◑部分 = 部分子项已修；⏸ = 需真机/多屏环境验证后再改。
> **第二轮已逐项实证复核**：S1/S2/S3、H2/H4、M3/M4/M5/M8/M10/M11/M12/M13/M17/M18/M20/M21/M22/M23/M26 的修复均属实（修复时间 2026-10-03 22:53 之后）。
> **第三轮修复（2026-10-04 下午，12 批）**：R1/R2/R3/R4/R5/R6/R7/R9/R10/R11/R12/R13/R14/R15/M15 已修；R8 部分修复（见第十四节）。仍未处理：H1（根因）、M2/M6/M7/M9/M14/M16/M19/M24/M25/M27/M28 及低危清单。详见第十四节与 `docs/CHANGES-20261004.md`。

---

## 三、第二轮独立复核（2026-10-04）新增发现

> 本轮**先独立重审全量源码、后与第一轮报告交叉核对**。以下 14 项为第一轮未覆盖的问题，均已逐条对照源码核实。
> 交叉核对结论：第一轮标注 ✅ 的修复**全部属实**（已逐项 grep 复核）；标注 ⚠️ 的 H1 确认根因未除；第一轮第十一节"未能编译/未跑测试"的局限**已在本轮关闭**（实际 `dotnet build` 0 警告 0 错误，`dotnet test` 315 通过 / 0 失败）。
> 同时**驳回两条过度论断**（详见第四节）。

### 锁屏安全边界（最优先，3 项严重）

#### [严重] R1. 锁屏期间托盘菜单未被禁用 —— 可绕过锁屏且无需 PIN

**位置**：`src/Modules/ScreenLock/ScreenLockModule.cs:546`（`screenlock_settings` 菜单项）、`:553`（打开设置窗口）

**问题**：`GetTrayMenuItems()` 在锁屏状态下**照常返回全部菜单项**，未依据 `Controller.IsLocked` 做任何裁剪（已核实全模块 `IsLocked` 仅在 `:300` 一处被读取，且是解锁判断，不参与菜单构建）。因此锁屏期间：

1. 托盘图标仍可右键点击（锁屏窗口 `WindowStyle=None` + `Topmost=True`，但任务栏 `Shell_TrayWnd` 是独立顶层窗口，`LockWindow` 并不独占；Z 序由 `OnKeepAliveTick` 每 2.5s 才校正一次）；
2. 点击 `screenlock_settings` 打开 `ScreenLockSettingsWindow`；
3. 该窗口保存后回调 `OnConfigReloaded()`（`:107`），其中执行 `if (Config != null && !Config.Enabled) Controller?.Unlock();`（`:108`）；
4. **把"启用"关掉并保存，锁屏立即解除，全程不需要 PIN。**

**影响**：这是设计上的自洽性问题 —— 锁屏把"忘记 PIN"的风险转移给用户，却留下了一条完全不需要凭据的解除路径。

**建议**：`LockController` 暴露只读 `IsLocked`；`ScreenLockModule.GetTrayMenuItems()` 在锁定时返回空集合（或仅保留"解除锁定"入口）；`ScreenLockSettingsWindow` 打开时若 `IsLocked` 则拒绝显示。彻底方案是 `LockCore` 成功时隐藏托盘图标、`UnlockCore` 恢复。

#### [严重] R2. `UnlockOnResume` 默认为 `true` —— 会话解锁即免 PIN 解除锁屏

**位置**：`src/Modules/ScreenLock/Models/ScreenLockConfig.cs:22`、`ScreenLockModule.cs:300`

```csharp
public bool UnlockOnResume { get; set; } = true;
...
if (Config != null && Config.UnlockOnResume && Controller.IsLocked) Controller.Unlock();
```

**问题**：默认值使"任何导致 Windows 会话解锁的事件"都无条件解除 CarroDesk 锁屏，**不校验 PIN**。叠加 R1，锁屏的实际保护强度远低于用户预期。

**影响**：默认值让"忘记 PIN"与"绕过关���"在行为上不可区分；用户以为设了 PIN 就安全，实际只要能让 Windows 会话发生一次解锁即可。

**建议**：默认值改为 `false`，并在设置界面明确标注该项语义（"会话解锁后自动解除 CarroDesk 锁屏（跳过 PIN）"）；`Unlock()` 路径区分来源（PIN 校验 / 生命周期 / 会话解锁）并记审计日志。

#### [严重] R3. PIN 失败计数与封锁窗口仅存内存，重启宿主即清零

**位置**：`src/Services/PinService.cs:106-131`（`PinGuard._fails` / `_blockedUntil`）

**问题**：`_fails` 与 `_blockedUntil` 是纯内存字段，**无任何持久化**；`PinGuard.Reload()` 会清零且全仓无调用点（已核实）。攻击者输错 4 次 → 重启 CarroDesk → 计数归零 → 可无限次试探。H4 修复保证了并发下的正确性，但未解决"跨进程重启"的绕过面。

**建议**：把 `_fails`/`_blockedUntil` 写入 `%AppData%\CarroDesk\pin-guard.json`，进程启动读回，衰减窗口 24h。

### 任务调度（5 项）

#### [高] R4. `IsTaskStillEnabled` 用引用相等判定 —— 配置重载摧毁常驻任务守护链

**位置**：`src/Modules/TaskScheduler/Services/TaskSchedulerService.cs:872-878`

```csharp
private bool IsTaskStillEnabled(TaskDefinition task)
{
    if (task == null) return false;
    lock (_lock)
    {
        return _started && _globalEnabled && _tasks.Any(t =>
            ReferenceEquals(t, task) && t.Enabled);
    }
}
```

**问题**：每次 `ApplyCoreLocked` 都整体替换任务列表，而 `result.Tasks` 来自 `ParseTaskNode` —— **每次解析都 `new TaskDefinition()`**。因此**任何一次配置重载**（托盘"重载任务"、编辑器"保存并重载"、`ReloadConfig()` → `ReloadAll()`）都会让所有存量 `TaskDefinition` 引用失效。

而 `AttachDetached`（`:553-560`）与 `ExecuteAsync` 的重试循环都把**旧实例**闭包捕获进长生命周期流程。detach 任务在配置重载后崩溃时：`RestartContinue` → `!IsTaskStillEnabled(旧实例)` 为 true → 走"restart cancelled"分支 → **常驻任务永久失去守护**，且日志原因具有误导性（用户会以为是自己关掉了开关）。

**建议**：按任务名判定而非引用：

```csharp
// 按名字判定：配置重载会重建 TaskDefinition 实例（ParseTaskNode 每次 new），
// 用 ReferenceEquals 会让"重载"被误判成"任务被删除"，从而摧毁常驻任务的守护链。
return current != null && current.Enabled;   // current 由 Name 匹配（OrdinalIgnoreCase）得到
```

#### [高] R5. `GetRecent()` 返回内部列表的活视图 —— 并发写入致托盘菜单消失

**位置**：`TaskSchedulerService.cs:898-900`，消费点 `TaskSchedulerModule.cs:227`

```csharp
lock (_lock) return _recent.AsReadOnly();
```

**问题**：`AsReadOnly()` 返回的是**包装原 list 的活视图**，锁在返回瞬间即释放；而 `_recent` 会被 watcher 线程持续 `Insert(0, entry)`。消费方 `foreach` 遍历期间若有任务完成，将抛 `InvalidOperationException: 集合已修改`。虽被 `ModuleManager.GetItemsGuarded` catch，但后果是**整个托盘 Tasks 菜单消失**，且仅在任务频繁完成时偶发，极难复现。

**建议**：`return _recent.ToArray();`（快照）。`Tasks` 属性（`:35`）同理建议改快照消除隐患。

#### [高] R6. `TryStart` 异常路径产生僵尸进程 + 永久泄漏 Job 句柄

**位置**：`src/Modules/TaskScheduler/Services/TaskRunner.cs:281-286`、`src/Common/ProcessJob.cs:148`

```csharp
handle = new TaskProcessHandle(task, proc, job);
TaskLogger.Info(...);
proc.BeginOutputReadLine();
proc.BeginErrorReadLine();
return true;
}
catch (Exception ex)
{
    TaskLogger.Error(task.Name, "exception: " + ex);
    handle = null;     // ← proc 已启动！job 是局部变量，无人持有
    return false;
}
```

**问题**：进程在 `proc.Start()`（`:254`）已成功启动。此后任何异常（`BeginOutputReadLine` 在进程瞬退、流已关闭时抛 `InvalidOperationException` 是已知场景）都会进入 catch，而 catch 只做 `handle = null`：

1. **进程成僵尸**：`TaskProcessHandle` 未创建，调用方（`StartDetached` 返回 -1）无人持有引用，无人可 Stop；
2. **Job 句柄永久泄漏**：`job` 是局部变量，`ProcessJob` **无 finalizer**（已核实仅有 `:148` 的 `Dispose`），`job = null` 只是丢引用，SafeJobHandle 不会被 GC 终结 —— 句柄真泄漏；
3. **kill-on-close 兜底恰好失效**：作业对象只在句柄关闭时才杀树，而句柄永远不会关闭。

**建议**：把 `proc`/`job` 声明移出 try，catch 中分情况回收（句柄已接管 → `handle.Dispose()`；未接管 → `job.Dispose()` + `proc.Kill()`）。

#### [高] R7. `KillTree` 无条件 `taskkill /PID` —— 可能误杀无关进程树

**位置**：`src/Modules/TaskScheduler/Services/TaskProcessHandle.cs:229-238`

```csharp
var searcherCmd = "taskkill /PID " + pid + " /T /F";
```

**问题**：`taskkill` **无条件先执行**，`proc.HasExited` 检查在其**之后**。`Stop()` 虽幂等（`Interlocked.Exchange`），但幂等不保证 pid 仍有效：detach 实例可能已自然退出而槽位未清理，pid 在 Windows 上很快被回收分配给新进程 —— 此时 `taskkill /PID <旧pid> /T /F` 会杀掉一个完全无关的进程树（`/T` 还连带其所有子进程）。这是本模块唯一能杀"非本模块启动的进程"的路径。

**建议**：taskkill 前做 pid 归属校验（比对 `Process.StartTime` 与句柄的 `StartedAt`，容差 2 秒），不匹配则跳过。

#### [高] R8. `TaskLogger` 全局锁内同步文件 IO + 逐行写入 —— 顶满 stdout 管道

**位置**：`src/Modules/TaskScheduler/Services/TaskLogger.cs:49-86`

**问题**：单次 `Write` = 1 次 `Directory.Exists` + 2 轮(Exists + FileInfo) + 2 次文件开关，**全部在单个静态锁内**。而 `WriteOutput` 是 `proc.OutputDataReceived` 的直接回调（`TaskRunner.cs:243-247`），运行在线程池线程。

风险链：多任务并发输出 → 全局锁串行化 → 回调处理速度跟不上子进程写入 → `BeginOutputReadLine` 内部缓冲填满 → **子进程 stdout 管道写满后阻塞**（Windows 匿名管道默认 4KB）→ 任务进程挂起，现象是"脚本莫名卡住"，与调度器毫无关联，极难定位。

**建议**：改为内存队列 + 单写线程落盘；`WriteOutput` 多行用 `string.Join` 合并为一次写入，把每行 N 次 IO 降为每批 1 次。

### 其他（7 项）

| 编号 | 严重度 | 问题 | 位置 |
| --- | --- | --- | --- |
| **R9** | 中 | `RaiseDevicesChanged()` 全仓**仅有一处定义、零调用点**（已 grep 确认），`AudioSwitchModule` 的订阅/退订全是死代码 → 插拔耳机后托盘仍显示旧设备名，必须重启进程。接口注释承诺的 WM_DEVICECHANGE 桥接未实现 | `AudioService.cs:32-35`、`IAudioService.cs:11` |
| **R10** | 中 | 宿主退出时 `killWithHost=false` 的实例走 `h.Dispose()`（`:866`），而 exit watcher 仍在 `_proc.WaitForExit()` → `ObjectDisposedException` 被吞 → 返回 -1 → `OnDetachedExited` 判定为**意外退出** → **每个 detach 常驻任务弹一次虚假"失败"通知** | `TaskSchedulerService.cs:862-866`、`TaskProcessHandle.cs:153` |
| **R11** | 中 | `_loadErrors` 填充后永不清空，且每个保存入口都拦截 → 用户手写错一个 cron 表达式后，**打开编辑器点任何保存都被拦下**，唯一出路是手改 `tasks.json` | `TaskEditorWindow.xaml.cs:794-798` |
| **R12** | 中 | `Save`（全量覆盖）与后台 `Reload`→`Load()` 无跨线程串行化，存在"保存成功但调度器仍用旧配置"的交错窗口。`TaskConfigService` 是纯静态类无锁 | `TaskConfigService.cs:250-277` |
| **R13** | 中 | detach 模式强制 `timeoutSec=0` 且传入 `CancellationToken.None`，`WaitAsync` 必走 `Task.Run(() => _proc.WaitForExit())` → **每个常驻任务独占一个线程池线程**直到自然退出；10-20 个即饱和，拖慢所有依赖 `Task.Run` 的路径 | `TaskProcessHandle.cs:114` |
| **R14** | 中 | `WINDOWPOS.flags` 声明为 `uint`，而原生是 `WORD`；代码 `pos.flags &= ~((uint)0x0020)` 清除的是 `SWP_FRAMECHANGED`(0x0020)，**注释却写 `SWP_NOZORDER`(0x0004)**。即注释与实现不符，实际未在清除 NOZORDER | `LockWindow.xaml.cs:139,157` |
| **R15** | 中 | 列表项 `ToolTip="{Binding FullText}"` 绑定**未截断的完整原文**（`:157`）；叠加 S3 的 `MaxTextLength` 默认 100 万字符，悬停任一行即让 WPF 加载完整原文 | `ClipboardHistoryWindow.xaml:157` |

**补充（性能，非缺陷但影响体验）**：`AppAutoMuteModule.cs:214-225` 白名单静音对每个活跃进程单独调 `SetProcessMute`，而该方法内部每次都重新枚举全部音频会话（`AudioService.cs:197-272`）→ O(N×M) 次 COM 跨进程调用，且运行在 UI 线程（`OnMuteTimerTick` 是 `DispatcherTimer` 回调）。30 个会话 + 10 个进程时单次 tick 约 300 次枚举，UI 冻结 100-500ms；`UnmuteAllTargets` 被 5 处调用（含**程序退出流程**）。建议在 `IAudioService` 增加批量接口，建索引后一次枚举。

---

## 四、驳回的两条过度论断

第二轮复核中，以下两条曾被提出但经实证核对**不成立**，记录在此以免后续误改：

1. **`IpPresenceDetector.ProbeArp` 的 `SendARP` 字节序"错误"** —— 现有 `BitConverter.ToUInt32(ipBytes, 0)` 在小端机上产生的内存字节恰为 `C0 A8 01 05`（即网络字节序），**代码正确**。若按"应为大端移位"的建议改成 `(b[0]<<24)|...`，反而会把内存字节变成 `05 01 A8 C0` 而**引入真实缺陷**。（已用 Python 实测算术验证）
2. **`LockSafe()` 非 UI 线程误报成功（第一轮 M20）** —— 该问题**已于 2026-10-03 22:53 修复**（同步封送），对当前代码已过期，不应再作为待办项。

---

## 五、严重问题（详述）

### [严重] S1. `Loc.T(key, string)` 重载陷阱 —— 占位符不替换、错误详情丢失

**位置**：`src/Services/Localization/Loc.cs:13`（重载定义）；受影响调用点：`src/Views/ConfigEditorWindow.xaml.cs:56,186`、`src/Modules/TaskScheduler/Views/TaskEditorWindow.xaml.cs:422,891`、`src/Modules/ScreenLock/Views/LockWindow.xaml.cs:358`、`src/Modules/ScreenLock/Views/ScreenLockSettingsWindow.xaml.cs:366` 等 10 余处。

**问题**：`Loc` 同时定义了

```csharp
public static string T(string key, string defaultValue)                     // (A)
public static string T(string key, string defaultValue, params object[] args) // (B)
public static string T(string key, params object[] args)                      // (C)
```

当第二个实参是 **`string`**（如 `Loc.T("Tasks.SaveFailed", ex.Message)`）时，C# 重载解析优先选择"普通形参"的 (A)，而非 `params` 扩展形式的 (C)。于是走 `I18nService.Get(key, defaultValue)`：key 存在时返回语言包原文，**`{0}` 不替换，实参被丢弃**。

**证据**：

```csharp
// TaskEditorWindow.xaml.cs:891  语言包 Tasks.SaveFailed = "保存失败: {0}"
MessageBox.Show(Loc.T("Tasks.SaveFailed", ex.Message), ...);   // 实际显示：保存失败: {0}

// TaskEditorWindow.xaml.cs:422  Tasks.DeleteConfirm = "删除任务 \"{0}\" ？"
MessageBox.Show(Loc.T("Tasks.DeleteConfirm", cur.Name), ...);  // 实际显示：删除任务 "{0}" ？
```

同一 key 在 `App.xaml.cs:90` 用了三参写法 `Loc.T(key, "读取配置失败: {0}", rawLoadError)`（正确），说明这是**系统性误用而非个例**；两种写法并存恰恰暴露了该重载集易被误用。

**影响**：所有"带字符串参数的错误/确认提示"都只显示字面量 `{0}`，真实原因（异常消息、任务名）全部丢失 —— 直接损害可排障性，用户看到的提示等于无效。

**建议**：字符串实参统一改用 `Loc.Format(key, arg)` 或三参 `Loc.T(key, default, arg)`；并**删除 (A) 这个易误用的重载**（其能力可由 (B) 覆盖），或将其改名 `Get`。

---

### [严重] S2. 任务编辑器保存静默丢失字段（数据丢失）

**位置**：`src/Modules/TaskScheduler/Views/TaskEditorWindow.xaml.cs:859`（保存）、`:393-401`（复制）、`Models/TaskDefinition.cs:82,102`

**问题**：`BuildCurrent()` 从表单重建一个全新的 `TaskDefinition`（`t.Options` 为 `new TaskOptions()`）。`TaskOptions.SingleInstance`（默认 false）与 `StableUptimeSec`（默认 60）在界面上**没有任何控件**，`BuildCurrent` 也从未赋值（见 `:515-528` 全部赋值语句）。保存时执行 `targetTask.Options = built.Options`（整体替换），于是：

- 打开一个已配置 `singleInstance=true` 或 `stableUptimeSec≠60` 的常驻任务，仅点"保存"，磁盘上的 `tasks.json` 就把这些字段**重置为默认值**，无任何提示；
- `singleInstance` 被抹掉会直接导致下次启动重复拉起常驻实例。

同时，`OnCopyClick` 构造副本时（`:393-401`）只复制了 `Hidden/TimeoutSec/AllowConcurrent/Retry/NotifyOnFailure/WorkDir`，**丢失 `Mode / KillWithHost / SingleInstance / Restart / RestartDelaySec / RestartLimit / StableUptimeSec`** —— 复制一个 detach 常驻任务会得到一个 wait 任务。

**证据**：

```csharp
// BuildCurrent() 对 t.Options 的赋值到此为止，无 SingleInstance / StableUptimeSec
if (int.TryParse(RetryBox.Text.Trim(), out rt)) t.Options.Retry = rt;   // :528
...
targetTask.Options = built.Options;   // :859  整体替换 → 未覆盖字段归默认
```

```csharp
Options = new TaskOptions   // :393 复制任务：仅 6 个字段
{
    Hidden = cur.Options.Hidden, TimeoutSec = cur.Options.TimeoutSec,
    AllowConcurrent = cur.Options.AllowConcurrent, Retry = cur.Options.Retry,
    NotifyOnFailure = cur.Options.NotifyOnFailure, WorkDir = cur.Options.WorkDir
},
```

（`:888` 的异常回滚保存了 `previousOptions`，说明作者已意识到"整体替换"的风险，但正常路径仍整体替换。）

**建议**：`BuildCurrent` 以 `cur?.Options` 为基底、只覆盖表单字段；或在保存时做字段级合并而非整体替换。补一个"BuildCurrent 字段完备性"的单元测试防回归。

---

### [严重] S3. 剪贴板历史无差别采集敏感内容并明文落盘

**位置**：`src/Modules/ClipboardHistory/Services/ClipboardHelper.cs:24-27`、`Services/ClipboardHistoryService.cs:106-118`、`Models/ClipboardHistoryConfig.cs`

**问题**：监听 `WM_CLIPBOARDUPDATE` 后直接 `Clipboard.GetText()` 取文本，**完全未检查 Windows 剪贴板排除格式**（`CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` / `ExcludeClipboardContentFromMonitorProcessing`）—— 而这些正是 KeePass、1Password 等密码管理器用来声明"不要把我纳入剪贴板历史"的格式（系统自带 Win+V 会遵守）。本模块不遵守，因此"复制密码"会把**明文密码**写入 `history.json`，默认保留 1000 条 / 90 天，且为**明文 JSON**。

**证据**：

```csharp
// ClipboardHelper.cs:24
if (Clipboard.ContainsText())
{
    text = Clipboard.GetText();     // 未取 DataObject、未检查排除格式
    return true;
}
```

```csharp
// ClipboardHistoryService.cs:106-115  全文原样保存，无长度上限
var item = new ClipboardItem { FullText = rawText, PreviewText = preview,
                               TextLength = rawText.Length, ... };
```

**影响**：隐私/安全。属于"默认不安全"的设计缺陷，且用户难以察觉。

**建议**：改用 `Clipboard.GetDataObject()` 遍历格式，命中排除格式即跳过；提供"疑似敏感内容不记录"开关；存储层至少加密或收紧目录 ACL；并加 `MaxTextLength`（如 1 MB）防止超大文本驻留。

---

## 六、高危问题（详述）

### [高] H1. 锁屏键盘钩子与焦点耦合 —— PIN 可能泄漏到下层窗口

**位置**：`src/Modules/ScreenLock/Services/KeyboardBlocker.cs:112`、`src/Modules/ScreenLock/Views/LockWindow.xaml.cs:287-313`

**问题**：`IsAllowedKey` 对数字、NumPad、`Backspace`、`Enter`、`Shift` **无条件**放行（`CallNextHookEx` 到系统），放行与否**与锁屏窗口是否为前台无关**：

```csharp
if (IsAllowedKey(vk) && !winDown && !altDown)
    return CallNextHookEx(_hook, nCode, wParam, lParam);   // 数字键交给系统 → 投递给当前前台窗口
```

而自动锁屏由后台进程触发，`ActivateIfNeeded()`（`:287-289`）与失焦重试（`:304-313`）都只调用 `Activate()`，未使用 `SetForegroundWindow`/`AllowSetForegroundWindow`/`AttachThreadInput` 等补救手段。Windows 前台锁定规则会拒绝后台进程抢占焦点。

**影响**：自动锁屏后若焦点未被夺回，用户输入的 PIN 数字会被投递到仍持有焦点的下层应用（且被锁屏层遮住看不见），同时锁屏窗口收不到按键 —— 构成 **PIN 泄漏** + "看似卡死"两种后果。

**建议**：置顶/激活后再校验 `GetForegroundWindow()==hwnd`；未取得前台时，钩子回调直接吞掉数字等"允许键"（即仅当锁屏窗口为前台才放行）；配合 `AllowSetForegroundWindow`/`AttachThreadInput` 方案。

---

### [高] H2. wait 模式"停止任务"被判为失败，`retry>0` 时会自动重启

**位置**：`src/Modules/TaskScheduler/Services/TaskSchedulerService.cs:463-517`、`:760-792`、`Services/TaskProcessHandle.cs:112-115`

**问题**：wait 分支调用 `TaskRunner.RunAsync(..., CancellationToken.None, null, ...)`，既无可取消令牌也无 outcome；`ExecuteAsync` 的重试循环只检查 `_globalEnabled`/`IsTaskStillEnabled`，**从不读取 `slot.StopRequested`**。用户点"停止任务"→ `TryStop` 置 `slot.StopRequested=true` 并 `handle.Stop()` 杀进程树 → `WaitAsync` 走无限等待分支，进程被杀后返回非 0 退出码：

```csharp
code = await TaskRunner.RunAsync(task, reason + ..., CancellationToken.None, null, h => slot.Handle = h);
...
if (code == 0 || attempt > retry) break;    // 被停止(非0) → 继续重试
await Task.Delay(1000);
```

```csharp
// TryStop:774 注释称"取消挂起中的自动重启(systemctl stop 语义)"，但 ExecuteAsync 从不读它
if (s != null) s.StopRequested = true;
```


**影响**：`retry=0` 时"停止"被记为任务失败并弹失败通知；`retry>0` 时"停止"会重新拉起进程 —— **停止功能失效**。

**建议**：wait 分支传入随实例可取消的令牌（slot 持有 CTS），或在 `RunAsync` 后检查 `slot.StopRequested`，若是主动停止则跳过失败通知与重试。

---

### [高] H3. 悬浮面板菜单区无滚动/限高

**位置**：`src/Views/FloatingPanelWindow.xaml:6,148-193`

**问题**：`Window Width="230" SizeToContent="Height"`，内容区为 `Grid` 的 `RowDefinition Height="*"`，其中直接放 `Menu`，**既无 `MaxHeight` 也无 `ScrollViewer`**。菜单项 = 全部模块根项 + 宿主项，随模块数量增长；高度超过工作区后窗口顶到屏幕外，底部菜单（含"退出"）不可点击。

**证据**：

```xml
<Window Width="230" SizeToContent="Height" ...>
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto"/><RowDefinition Height="*"/>
  </Grid.RowDefinitions>
  <Border Grid.Row="1" Padding="4">
    <Menu x:Name="ItemsHostMenu" .../>   
```

**建议**：内容区改用 `ScrollViewer`，或给 `Menu` 设 `MaxHeight` 绑定 `SystemParameters.WorkArea.Height`。

---

### [高] H4. `PinGuard` 并发调用共享的 `HostPinService.Verify`，状态无同步

**位置**：`src/Services/PinService.cs:165-171`（锁外验证）、`src/Services/HostPinService.cs:31-65`

**问题**：`PinGuard.Try` 有意"锁内预留名额 → 锁外 PBKDF2 → 锁内结算"以支持并发：

```csharp
var pinService = _pinProvider?.Invoke();          // 锁外
var verified = pinService != null && pinService.Verify(pin);
```

但 `_pinProvider` 返回的是**同一个** `HostPinService` 实例，其 `Verify` 会写共享可变字段：

```csharp
_inner.SetFromConfig(_pendingSalt, _pendingHash);   // 或 BuildFromCurrent() 写 _inner
bool ok = _inner.Verify(pin);                        // 读 _inner 的 Salt/Hash
```

`_inner`（`PinService`）的 `Salt/Hash/JustUpgraded` 及 `HostPinService._pendingSalt/_pendingHash` 均**无同步**。命名管道每连接独立 Task（并发上限 `MaxAllowedServerInstances`），两个并发请求会同时改写/读取同一 `_inner` 的 salt/hash，存在读写撕裂窗口 —— 可能"正确 PIN 被拒"或"错误 PIN 通过"，且 `JustUpgraded` 升级落盘可能重复。

**建议**：为 `HostPinService.Verify` 增加独立 `_verifyLock`（PBKDF2 约 60ms 级，串行化可接受），或每次验证使用**局部** `PinService` 实例。

---

## 七、中危问题（要点）

> 每条已给位置，此处列关键证据与影响，完整描述见各文件。

**IPC / 命令内核**

- M2 `CommandHost.cs:119-129`：`Task.Run(handler)` 后 `task.Wait(timeoutMs)`，超时返回但 handler 无法取消且继续跑（副作用残留），同时占用线程池。建议 `await Task.WhenAny` + 传 `CancellationToken`。
- M3 `CommandHost.cs:199-268`：`type=="int"` 时 JSON 小数（`double`）落到末尾兜底分支被 `Convert.ToString` 变成字符串 —— 声明 int 拿到 string，类型契约被破坏。证据：int 分支只覆盖整数类型与 string，未覆盖 `double/decimal`。
- M4 `CommandRegistry.cs:22-33`：`Clear()` 后逐条 `RegisterCore`，中途抛异常即停在半注册态；`App.xaml.cs:192` 却记日志"能力通道降级为空"，与事实不符，误导排障。

**配置 / 日志**

- M1 `ConfigService.cs:266-292`：`JsonException` 等解析失败只备份 `.corrupt`、不置 `LastLoadIoFailure`、无日志，App 静默以默认配置启动（PIN 丢失→触发首启向导）。建议解析失败也告警。
- M5 `DefaultLoggerService.cs:38-51`：`log.txt` 每次追加，无轮转；同项目 `FileCommandAuditSink` 已有 1MB 轮转，属实现不一致。

**TaskScheduler**

- M6 `TaskSingleInstanceMutex.cs:44-84` + `TaskSchedulerService.cs:826-831`：互斥体由**宿主**持有，`killWithHost=false` 时宿主退出即销毁互斥体，但常驻子进程仍活着 → 新宿主认为"未运行"再拉一个，恰是该选项要防的场景失效。
- M7 `TaskSchedulerService.cs:400-408`：`AllowConcurrent=true` 时 `_running[task.Name] = slot` 覆盖旧槽，第一个实例句柄丢失，`GetRunning/TryStop` 只能看到最后一个。
- M8 `CronTrigger.cs:76-93`：`missed = scheduled < now`，而定时器已等到 `scheduled`，故 `scheduled < now` 恒真 → 每次正常触发都标 `cron-catchup:`，`IsMatch` 复检分支（`:93`）不可达。
- M9 `CronHelper.cs:211-226`：无区间时 `start=end=值`，`5/2` 只匹配 5（标准应为 5,7,9…）；`CronTrigger.cs:52-57` 对"合法但永不可达"（如 2 月 30 日）直接 `return`，无日志无提示，触发器静默失效。
- M10 `TaskSchedulerService.cs:110-132`：`SaveModuleConfig` 失败 → `SetGlobalEnabled(!enabled)` 回滚 → 回滚里再失败 → 再回滚，递归直至栈溢出（仅持续失败时触发）。
- M11 `TaskRunner.cs:262-275` + `TaskProcessHandle.cs:50`：`ProcessJob.TryAssign` 失败即 `job=null`，`KillWithHost` 直接以 `_job != null` 判定为 false —— 用户显式 `killWithHost=true` 被静默当 false，宿主退出留下孤儿进程，日志还错报 `killWithHost=false`。
- M12 热键不校验：`ClipboardHistorySettingsWindow.xaml.cs:44-71`、`MonitorProfileSettingsWindow.xaml.cs:533-538` 直接 `Text.Trim()` 写配置（对比 `AudioSwitchSettingsWindow.xaml.cs:420-427` 有 `HotkeyHelper.Validate`）；非法热键注册静默失败无提示。

**Awake / AppAutoMute**

- M13 `AwakeService.cs:200-204`：`_userSuppressedProcessLink` 在任何 `UpdateConfig` 且联动开启时被清零，而 `ReloadAll()` 会触发 `OnConfigReloaded → UpdateConfig` —— 用户点"关闭"后一次配置重载即"关了又开"。
- M14 `AwakeService.cs:519-521,608-632`：1 秒 `DispatcherTimer` 在 UI 线程，每 5 秒 `Process.GetProcesses()` 全量枚举，数百进程时周期性卡 UI。建议改后台快照或 `GetProcessesByName`。
- M15 `AwakeService.cs:392`：`SetSuspendState(false,false,false)` 直接调用，未用 `AdjustTokenPrivileges` 启用 `SE_SHUTDOWN_NAME`，受限账户返回 FALSE(1314)，而 IPC 早已回"已调度"。（shutdown 路径借用了系统 `shutdown` 工具，权限由工具处理，故仅 sleep/hibernate 受影响。）
- M16 `AppAutoMuteModule.cs:163-247`：白名单分支每次前台变化才启动 `_muteTimer`，而 `OnMuteTimerTick` 首行即 `Stop()`，两次前台切换之间新起发声的后台进程漏静音。

**MonitorProfile**

- M17 `MonitorProfileSettingsWindow.xaml:203` + `MonitorTimeSetting.cs:30`：`DataGridTextColumn` 可编辑且双向绑定，只有下方"添加/更新"输入框做校验；单元格输入 `999/-5/abc` 会被保存，时间非法时 `ToTimeSpan()` 静默归零（≥00:00 可能永不命中）。建议列设只读或在 `CellEditEnding`/setter 校验。
- M18 `MonitorDdcService.cs:679,714-719`：首次 `ManagementException`（WMI 忙等瞬时故障也会抛）即置 `_wmiSupported=false` 永久禁用回退，笔记本内置屏亮度可能失效到重启。建议带冷却重试。
- 另：`MonitorProfileSettingsWindow.xaml.cs:495-505` "立即应用测试"实际调用 `SaveAndApplyConfig`（写盘+重载），名为测试实为持久化。

**ScreenLock / 服务控制**

- M19 `LockWindow.xaml.cs:72,117` + `app.manifest:5`：`EnumDisplayMonitors` 返回**物理像素**却赋给 WPF 的 DIP 属性 `Left/Top/Width/Height`，`OnSourceInitialized` 又用物理值走 `SetWindowPos`；清单仅 `dpiAware=true`（系统级）。非 100% 缩放或非 0 原点多屏下窗口错位。建议统一换算或声明 `PerMonitorV2`。
- M20 `LockController.cs:189-196`：`Lock()` 经 `RunOnUiThread` 在非 UI 线程是 `BeginInvoke`（异步），`LockSafe` 紧接着读 `_locked`（尚未置位）→ 直接返回 true；真实失败只写日志。
- M21 `SystemIdleService.cs:126`：`wasActive` 判定的是"上一拍 `_rawIdleTicks <= 0`"（基本只在首拍成立），非"上一拍空闲 > 阈值"，导致 `UserActiveDetected` 在用户活跃时每秒触发，订阅方 `ScreenLockModule.ResetIdleMachine()` 每秒执行。
- M22 `WindowsServiceControlAdapter.cs:14`：`GetStatus` 只捕 `InvalidOperationException`，SCM 访问被拒抛 `Win32Exception(5)` 直接逃逸；`ServiceControlModule.cs:204` 枚举所有服务无逐项 try/catch，一个服务出错即整个 `services.status` 失败。
- M23 `WindowsServiceControlAdapter.cs:30,45`：只排除 `Running/StartPending`（Start）与 `Stopped/StopPending`（Stop），对 `StopPending` 调 `Start()`/对 `StartPending` 调 `Stop()` 会抛异常并被统一下判为 `InvalidParams`("service unavailable")，信息误导。
- 另 `ScreenLockModule.cs:216`：`OnIdleThresholdReached` 在守卫命中时 `return` 但未复位 `_idleFired`，而 `OnIdleTick` 首行 `if (_idleFired) return;`，可致自动锁屏卡死直到下次用户活动。

**UI / 基础设施 / 工程**

- M24 `FloatingPanelWindow.xaml.cs:270`：`SystemParameters.WorkArea` 只反映主屏，`Tray/Center/TopRight` 全部基于它计算，副屏无法定位。
- M25 `ConfigEditorWindow.xaml.cs:234`：语言/自启/PIN 都改在 `_editing` 草稿，只有"保存并应用"写回，而"关闭/取消"直接 `Close()` 无脏检查，改完 PIN 点关闭会误以为已保存。
- M26 `cli/Program.cs:37`：`--mcp` 设了 `Console.OutputEncoding=UTF8`，`ctl` 分支未设，重定向时含中文的 JSON 按系统代码页输出 → AI/jq 侧乱码；`ParentConsole.cs:24` 同样未固定。
- M27 安装脚本：`scripts/installer/CarroDesk.iss`、`CarroDesk.nsi` 声明中英双语，但进度提示、卸载文本、README 全文硬编码中文，英文用户仍见中文。
- M28 测试质量：
  - `UiRenderingTests.cs:84,94,116` 方法名声称"校验重叠"，实际只 `Show()` + 截图、**无任何 Assert**，且 `:189` 用非贪婪正则 `@"<Grid\b.*?</Grid>"` 解析嵌套 Grid 会在第一个内层 `</Grid>` 截断，结果不可靠；
  - `DevicePresenceAndAutoLockTests.cs:49` `Assert.IsTrue(x || !x)` 恒真断言，且依赖真实 ICMP；
  - `JsonMigrationTests.cs:180` 名为 `SaveAndReload_RoundTrip` 却从未调用 `Save`；
  - `CarroDeskContractTests.cs:42` 用例名与断言相反（"slow→false" 实际断言快任务 `IsTrue`）；
  - 多个用例直接改全局 `I18nService.Instance` 语言但无 `try/finally`，断言失败会污染后续用例。

---

## 八、低危问题（清单）

| 位置                                                                      | 问题                                                                                                                                |
| ----------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `HostCommands.cs:83`                                                    | `host.status` 每次调 `Process.GetCurrentProcess()` 未 `using`。**注**：当前进程句柄为伪句柄，不构成真实句柄泄漏，属释放不规范，影响小（原判"高"被高估，此处更正为低）                  |
| `NamedPipeCommandServer.cs:80-85`                                       | `Dispose` 只 `Cancel()`，未 `_cts.Dispose()`、未关闭在途监听流                                                                                |
| `NamedPipeCommandServer.cs:46,60`                                       | `WindowsIdentity.GetCurrent()` 实现 IDisposable，构造时调用两次且未释放                                                                         |
| `HostMenuActions.cs:73`                                                 | `Process.Start(path)` 返回值未 Dispose                                                                                                |
| `DynamicTrayController.cs:224,99-108`                                   | `OnToggleClick` 只退订未订阅（死代码）；`_debounceTimer` 从未 Stop/清理                                                                           |
| `SafeInvoker.cs:34-51`                                                  | 创建超时 CTS 并把 token 传给不观察 token 的 action，超时无法取消也无从观测，属误导性实现                                                                         |
| `PipeRpcClient.cs:37-43`                                                | `Connect` 有超时，随后 `ReadFrame` 同步无超时，对端不回包会永久阻塞                                                                                     |
| `ServiceContainer.cs:134-154`                                           | 释放顺序依赖 `Dictionary` 枚举顺序（.NET 不承诺），与"按注册逆序释放"意图不符                                                                                 |
| `App.xaml.cs:277-283`                                                   | 浮动面板热键注册失败被静默（`out _` 丢弃错误），用户无感知                                                                                                 |
| `App.xaml.cs:309-345`                                                   | 热重载重建能力表，但 `Ipc.Enabled/PipeName` 变更不重启管道服务，热重载不一致                                                                                |
| `MenuProjectionEngine.cs:91-97`                                         | `onPropertyChanged` 直接 `dispatcher.CheckAccess()`，dispatcher 为 null 时 NRE（同函数 `onChildrenChanged` 有保护）                            |
| `ForegroundTracker.cs:124-135`                                          | `Dispose` 未清空 `ForegroundChanged` 订阅                                                                                              |
| `ConfigEditorWindow.xaml.cs:167-181`                                    | `_configManager==null` 时跳过保存但仍提示"成功"（`return false` 在 if 内不可达）                                                                    |
| `KeyboardBlocker.cs:85`                                                 | `VK_SHIFT(0x10)` 分支在低级钩子下基本死代码（实际是 `VK_LSHIFT/RSHIFT`）                                                                            |
| `KeyboardBlocker.cs:78,99`                                              | `Remove()` 忽略 `UnhookWindowsHookEx` 返回值；`HookProc` 无 try/catch（托管异常穿越 P/Invoke 回调可致进程终止），且无"钩子被系统移除"看门狗                           |
| `LockWindow.xaml.cs:129,398`                                            | `WndProc` 无条件强制 TOPMOST（与注释不符）；`CloseSafe()` 为死代码                                                                                 |
| `LockController.cs:97`、`PinService.cs:124`                              | `ApplyPinFromConfig()` 空实现仍被调用；`PinGuard.Reload(pin)` 入参未使用                                                                       |
| `ComInterfaces.cs:323-352`                                              | 备用 PolicyConfig CLSID 与注释不符（Vista 实为 `294935CE-...` 且实现 `IPolicyConfigVista`）；`is IPolicyConfig` 失败时已创建的 RCW 未 `ReleaseComObject` |
| `AudioService.cs:111-169`                                               | `[HandleProcessCorruptedStateExceptions]` 捕获 CSE 后仍继续运行（权衡取舍）                                                                     |
| i18n 漏网                                                                 | `ClipboardHistoryWindow.xaml:176` 硬编码"{0} 字符"；`AudioSwitchSettingsWindow.xaml:197,211` 硬编码"扬声器/耳机"                                |
| `ConfigEditorWindow.xaml.cs:186`、`ScreenLockSettingsWindow.xaml.cs:366` | `Config.SaveFailed="保存配置失败"`（无占位符）却用 `Loc.T(key, ex.Message)`，命中 (A) 重载后 `ex.Message` 被丢弃，用户看不到失败原因（S1 的另一表现）                     |
| `FloatingPanelWindow.xaml:32,169-180`                                   | 工具按钮 `Focusable=False` 且无 `AutomationProperties.Name`，键盘/读屏不可达                                                                    |
| `Directory.Build.props:59,79`                                           | `DaysSinceEpoch` 对系统时间早于基准日无防护，会产生负数版本号                                                                                           |
| `tests/MemoryOptimizerTests.cs:43`、`ComInteropTests.cs:44`              | 用例无有效断言（无法证明行为）                                                                                                                   |
| `scripts/installer/CarroDesk.nsi:476-481`、`TrayContextMenu.xaml.cs:57`  | 重复调用 / 无引用成员等维护噪音                                                                                                                 |

---

## 九、架构与工程观察

1. **分层清晰但存在"看得见的捷径残留"**：`App.xaml.cs:35-38` 的注释明确记录了曾删除 `App.Config`/`App.Services` 静态门面以维护"宿主零感知"铁律，方向正确。当前仍有个别 `App.XXX` 静态回调（`ShowBalloonPublic` 等），但已受控。
2. **能力暴露采用 pull 模式**（`ICommandProvider`），与托盘菜单同构，设计自洽；`Rebuild` 的半注册态是其主要健壮性短板（M4）。
3. **测试规模可观（20+ 测试文件）且多为真实端到端验证**（进程生命周期、命名管道、命令内核），未发现 `[Ignore]`/跳过；但存在"名不副实/恒真断言/依赖真实时序"的用例（M28），且仓库内遗留两个约 200 MB 的 `testhost` hangdump（`tests/CarroDesk.Tests/TestResults/*.dmp`，共 386 MB，已被 `.gitignore` 忽略未入库），提示历史上曾出现测试挂起，建议清理并排查其成因。
4. **目录与命名一致性小瑕疵**：`src/Modules/TaskScheduler/Services/IdleDetector.cs` 却声明 `namespace CarroDesk.Services`（其余为 `CarroDesk.Services.Tasks`），易混淆。
5. **文件体积偏大**：`TaskEditorWindow.xaml.cs` 1339 行、`TaskSchedulerService.cs` 915 行、`AwakeModule.cs` 794 行、`MonitorDdcService.cs` 766 行、`AwakeService.cs` 713 行 —— UI 与业务逻辑混杂，是 S2/M12/M17 这类"字段遗漏"缺陷的温床。建议编辑器引入 ViewModel + 显式字段映射，并为 `TaskOptions` 往返加测试。
6. **安全边界的设计优于实现**：文档（`DESIGN.md §4.5`）明确列出了不做的事，PIN 用 PBKDF2-100k + 恒时比较，审计只记参数摘要、口令单独提取 —— 这些都很好；但 H4（并发验证无同步）和 H1（钩子与焦点耦合）说明"安全敏感路径"仍需专项加固与测试。

---

## 十、建议的修复优先级

> **2026-10-04 修订**：依据第二轮复核结果重排。R1/R2/R3 是锁屏安全边界，**优先于已修完的 S1/S2/S3** —— 后者是数据与隐私问题，前者是可直接绕过认证的活路径。

**P0（本迭代必修，涉及安全绕过 / 数据丢失 / 隐私）**

1. ~~**R1 锁屏托盘绕过**~~ ✅已修 —— 锁定期间不输出任何托盘菜单项；设置窗口经注入的锁定态守卫拒绝保存。
2. ~~**R2 `UnlockOnResume` 默认值**~~ ✅已修 —— 默认 `false`（含历史迁移兜底与示例配置），设置界面标注"不校验 PIN"，自动解除写审计日志。
3. ~~**R3 PIN 限流持久化**~~ ✅已修 —— 失败计数/封锁窗口落盘 `pin-guard.json`，24h 衰减，重启不清零。
4. ~~S2 任务编辑器字段丢失~~ ✅已修（建议补"字段往返"回归测试防复发）
5. ~~S3 剪贴板隐私~~ ✅已修排除格式与长度上限（**存储仍明文**，见第十节）
6. ~~S1 `Loc.T` 重载~~ ✅已修
7. **H1 锁屏钩子与焦点解耦** —— 钩子仅在锁屏窗口确为前台时放行数字键（⚠️ 第一轮标注"部分修复"，根因未除；需多屏/前台切换实测）

**P1（资源安全与稳定性，第二轮新增）**

8. ~~**R6 僵尸进程 + Job 句柄泄漏**~~、~~**R7 `KillTree` 误杀风险**~~ ✅已修 —— 二者已同批修复。
9. ~~**R4 常驻任务守护链**~~、~~**R5 托盘菜单消失**~~ ✅已修。
10. **R8 任务日志管道阻塞** —— ◑部分修复：已去掉全局锁并把单条代价从约 8 次文件系统操作降到 1 次开+写+关；根治（输出回调永不阻塞 + flush 屏障）需改调用链，见第十四节。
11. ~~H2 wait 模式停止语义~~ ✅已修；~~H3 悬浮面板滚动~~ ✅已修；~~H4 PIN 并发~~ ✅已修（但见 R3）。
12. **M2 命令内核超时不可取消**（需接口变更）；~~M3~~ ✅已修。

**P2（质量与体验）**

13. ~~**R9 音频设备刷新死代码**~~、~~**R10 虚假失败通知**~~、~~**R11 编辑器保存死锁**~~、~~**R12 配置并发覆盖**~~、~~**R13 线程池占用**~~ ✅已修。
14. ~~R14/R15~~ ✅已修；M8/M9 Cron 语义；M26 CLI 编码 ✅已修；M27 安装脚本 i18n；M28 测试断言与隔离；M5 日志轮转 ✅已修；~~M15~~ ✅已修。
15. 待实机验证项：M19/M24（多屏 DPI）、M16（白名单轮询）、M14（后台进程快照）、M6/M7（常驻任务治理）、M25（编辑器脏检查）。

---

## 十一、附：审查方法与局限

- **方法**：分 5 条线对全仓 149 个 `src` 源文件、`cli/`、20+ 测试文件、工程与安装脚本逐文件阅读；每条结论定位到 `文件:行号`；对 S1/S2/S3/H1/H2/H4 及 M3/M8 等关键项做了**二次人工复核**（直接阅读源码确认），并据此**下调了 1 项被高估的结论**（`host.status` 的 Process 释放，由"高"更正为"低"）。
- **第二轮（2026-10-04）**：先独立重审全量源码，再与本报告交叉核对。新增 14 项（R1-R15，详见第三节），均已对照源码核实；已逐条 grep 复核第一轮全部 ✅ 修复项，确认属实。
- **局限**：
  - ~~未能实际编译、未运行测试~~ **已于第二轮关闭**：实际执行 `dotnet build CarroDesk.slnx`（**0 警告 0 错误**）与 `dotnet test`（**315 通过 / 0 失败 / 0 跳过**），故本报告不含编译警告与静态分析器（Roslyn CA 规则）输出；如需可再补一次 `TreatWarningsAsErrors` 构建。
  - 未做实际运行时验证（如真实多显示器 DPI、DDC 设备、密码管理器场景），M19/M17/M18 等属"代码级推断"，已在描述中标注。**R1/R2/R3 同属需实机验证的安全边界结论**，建议修复前先在真机复现确认。
  - `docs/old/` 历史文档、`temp/`、`release/` 未纳入代码审查范围。

---

## 十二、修复进展（2026-10-03 第一批）

按"改动小、收益大、风险低"排序分模块实施；每批均 `dotnet build`（0 警告 0 错误）并跑全量单测（**315 通过 / 0 失败 / 0 跳过**，基线 312 + 新增 3 个回归用例）。

### 已修复

| 编号 | 状态 | 说明 |
|---|---|---|
| S1 | ✅ 已修 | 修正 30+ 处 `Loc.T(key, stringArg)` 调用点为三参写法；保留 2 参重载（有回归测试依赖其兜底语义） |
| S2 | ✅ 已修 | `BuildCurrent(cur)` 保留 `singleInstance`/`stableUptimeSec`；复制任务补齐全部选项字段 |
| S3 | ✅ 已修 | 剪贴板读取遵循三个排除格式；新增 `MaxTextLength`（默认 100 万）截断 |
| H1 | ⚠️ 部分 | 焦点耦合未改（需多屏实机验证）；本轮先修了与之相关的 `LockSafe` 误报成功 |
| H2 | ✅ 已修 | wait 模式读取 `StopRequested`，停止不再判失败/不再重启 |
| H3 | ✅ 已修 | 菜单区 `ScrollViewer` + 工作区高度限高 |
| H4 | ✅ 已修 | `HostPinService.Verify` 加锁串行化 |
| M1 | ✅ 已修 | 新增 `LastLoadContentCorrupted` + 启动告警（新增 zh/en 文案） |
| M3 | ✅ 已修 | `int` 参数按整数语义收敛，非整数明确报错 |
| M4 | ✅ 已修 | `CommandRegistry.Rebuild` 原子替换 |
| M5 | ✅ 已修 | `log.txt` 1MB 轮转 |
| M8 | ✅ 已修 | `missed` 判据改为越过原定分钟 |
| M10 | ✅ 已修 | `SetGlobalEnabled` 回滚不再递归持久化 |
| M11 | ✅ 已修 | `KillWithHostRequested` + 挂接失败告警与兜底 |
| M12 | ✅ 已修 | 剪贴板/显示器设置窗口校验热键 |
| M13 | ✅ 已修 | 仅联动"关→开"时清除用户抑制 |
| M17 | ✅ 已修 | 显示器时间表 DataGrid 改只读 |
| M18 | ✅ 已修 | DDC WMI 回退改 30 秒冷却 |
| M20 | ✅ 已修 | `LockSafe` 同步封送 |
| M21 | ✅ 已修 | 用户活跃改边沿触发 |
| M22 | ✅ 已修 | `GetStatus` 兼容 Win32Exception；`services.status` 逐服务隔离 |
| M23 | ✅ 已修 | 服务过渡态给出明确错误 |
| M26 | ✅ 已修 | CLI/父控制台统一 UTF-8 |
| M28 | ◑ 部分 | 修正恒真断言、名不副实用例名、TestCleanup 语言兜底；新增 3 个回归用例。布局自检正则、时序脆弱用例未改 |
| — | ✅ 附加 | `src/Host/Ipc/RpcProtocol.cs` 由 GBK 转 UTF-8 |

### 待办（需真机/多屏/权限环境验证后再改，避免引入新回归）

- **H1** 锁屏焦点/钩子解耦——⚠️ 第一轮仅修 related 的 `LockSafe`（M20），**钩子与焦点耦合的根因未除**，是 P0。
- **M19 / M24** 多显示器物理像素与 DIP 混用（锁屏窗口错位）、悬浮面板位置预设只用主屏工作区——同源，需一并处理 DPI 换算，需非 100% 缩放 + 多屏实测。
- **M15** Awake `SetSuspendState` 未启用 SE_SHUTDOWN_NAME——需在受限账户实测睡眠。
- **M16** AppAutoMute 白名单模式轮询周期——涉及静音状态机，需实测避免抖动。
- **M6 / M7** 单实例互斥体跨宿主语义、并发实例槽位覆盖——涉及常驻任务治理，需专项设计与测试。
- **M2** `CommandHost` 超时 handler 不可取消——需为能力 handler 引入 `CancellationToken`，属接口变更。
- **M9** Cron `5/2` 语义与标准不一致——**属行为变更**（会改变既有用户的触发次数），需产品确认后再改。
- **M14** Awake UI 线程全量枚举进程——需评估后台快照的线程安全边界。
- **M25** 配置编辑器关闭脏检查、**M27** 安装脚本 i18n。

---

## 十三、第二轮修复进展（2026-10-04）

本轮为**独立审查 + 交叉核对**，**未修改任何源码**；产出为本报告第三节的 14 项新发现与两处修正，以及第十二节修复项的实证复核。

### 已实证复核为真（第一轮 ✅ 项）

逐条 grep 对照源码，第一轮标注 ✅ 的 20 项修复全部属实：`Loc.T` 调用点、任务编辑器字段保留（`TaskEditorWindow.xaml.cs:546-547`）与复制补齐（`:403-407`）、剪贴板排除格式（`ClipboardHelper.cs:14-19`）与 `MaxTextLength`（`ClipboardHistoryService.cs:80-82`）、`HostPinService._verifyLock`、`CommandRegistry.Rebuild` 暂存字典原子替换、wait 模式 `StopRequested`（`:486,509`）、`LockSafe` 同步封送等。

### 仍需处理（第二轮新增 14 项）

按优先级：**R1/R2/R3**（锁屏绕过，会话解锁免 PIN，PIN 限流可绕）→ **R6/R7**（僵尸进程与误杀风险）→ **R4/R5/R8**（守护链、托盘消失、管道阻塞）→ **R9-R15**（音频刷新死代码、虚假通知、编辑器保存死锁、配置并发、线程池、DDC 注释不符、ToolTip 全文）。

> **2026-10-04 下午更新**：上述 14 项中 R1/R2/R3/R4/R5/R6/R7/R9/R10/R11/R12/R13/R14/R15 已修，R8 部分修复。逐批明细见**第十四节**。

### 澄清与更正

- **第一轮 M20 已修复**，相关"非 UI 线程误报成功"结论对当前代码已过期。
- **`SendARP` 字节序经算术验证为正确**，切勿按"应改大端"的建议修改（详见第四节）。
- **S3 的"明文落盘"部分未解决**：已加排除格式与长度上限，但 `history.json` 仍为明文 JSON（全仓 `Protect|Encrypt|Aes|CryptProtect` 零命中），敏感内容仍可被同用户进程直接读取。建议后续用 DPAPI 加密存储层。
- **测试基线**：315 通过 / 0 失败 / 0 跳过（较第一轮记录的 312 增加 3 个回归用例）。

---

## 十四、第三轮修复进展（2026-10-04 下午）

按"改动小、收益大、风险低"排序，**分 12 批独立提交**，每批只动互不重叠的文件集（批次之间刻意不复用同一文件），每批均编译（0 警告 0 错误）并跑全量单测。

### 已修复

| 批次 | 编号 | 主题 | 涉及文件 |
|---|---|---|---|
| 1 | R1 / R2 | 锁屏安全边界：托盘菜单在锁定态禁用、设置窗口锁定守卫、`UnlockOnResume` 默认改 `false`、自动解除写审计 | `ScreenLockModule.cs`、`ScreenLockSettingsWindow.*`、`ScreenLockConfig.cs`、`ConfigService.cs`、语言包 |
| 2 | R3 | PIN 失败计数/封锁窗口落盘 `pin-guard.json`，24h 衰减 | `PinService.cs`、`App.xaml.cs` |
| 3 | R4 / R5 / R10 | 任务存活改按名字匹配；`GetRecent()`/`Tasks` 返回快照；宿主退出时置位 `StopRequested` | `TaskSchedulerService.cs` |
| 4 | R6 | 启动失败按"句柄是否已接管"回收进程与作业对象；`ProcessJob` 补终结器 | `TaskRunner.cs`、`ProcessJob.cs` |
| 5 | R7 / R13 | `taskkill` 前校验 pid 归属；等待退出改用 `Process.Exited`，不再占用线程池线程 | `TaskProcessHandle.cs` |
| 6 | R8 | 任务日志锁粒度降到每文件一把、目录检查去重、轮转检查限频 | `TaskLogger.cs` |
| 7 | R11 | 加载错误改为"告知代价 + 用户选择"，保存成功后清空 | `TaskEditorWindow.xaml.cs`、语言包 |
| 8 | R12 | `tasks.json` 的 Save/Load/LoadOrCreate 共用一把锁 | `TaskConfigService.cs` |
| 9 | R14 | `WINDOWPOS.flags` 改为 `ushort`；清除 `SWP_NOZORDER` 而非 `SWP_FRAMECHANGED` | `LockWindow.xaml.cs` |
| 10 | R15 | 剪贴板 ToolTip 改绑 `ToolTipText`（上限 1000 字符） | `ClipboardItem.cs`、`ClipboardHistoryWindow.xaml` |
| 11 | M15 | 睡眠前启用 `SE_SHUTDOWN_NAME`，并纠正"`true` + 非零 lastError"的误判 | `AwakeService.cs` |
| 12 | R9 | 消息专用窗口 + `RegisterDeviceNotification` 设备变化桥接（400ms 去抖 + 按默认端点 Id 过滤） | `AudioService.cs`、`App.xaml.cs`、语言包 |

### 部分修复

- **R8（任务日志管道阻塞）**：审查建议的"内存队列 + 单写线程"经实测**不可直接采用** ——
  - 异步落盘会破坏读后写一致性（任务日志是用户可见的排障入口，编辑器"查看日志"与单测都在写入后立刻读文件）；
  - 常开追加流会与 `File.ReadAllText`（`FileShare.Read`）双向共享冲突，实测 9 个既有用例直接失败（"文件正被另一进程使用"）。
  - 已落地的替代方案：全局锁改为每文件一把锁、单条代价从约 8 次文件系统操作降到 1 次开+写+关、目录检查去重、轮转检查按路径限频。真正的根治（输出回调永不阻塞 + 显式 flush 屏障）需要改调用链，留待专项评估。

### 修复过程中新发现并已修掉的问题

- `AudioService` 的 `StartDeviceNotifications` 若依赖 `Application.Current.Dispatcher`，在 Dispatcher 已关闭时 `Invoke` 会**永久阻塞**（全量测试直接挂起 15 分钟）。已加关闭态短路，并让调用方显式传入 Dispatcher。
- `ProcessJob` 缺少终结器意味着"异常路径漏调 `Dispose`"= 句柄真泄漏（`SafeJobHandle` 只在拥有者被 GC 时才终结）。

### 回归测试

新增 **21 个**用例，其中 5 个经"回退修复 → 确认失败 → 恢复修复 → 确认通过"实证确实能捕获对应缺陷：

- `ScreenLockSecurityTests`（4）：`UnlockOnResume` 默认值与 Clone、设置窗口锁定守卫（含探测抛异常兜底）、未锁定时托盘菜单不被误伤。
- `PinServiceTests`（3）：失败计数跨实例（等价重启）存活、复位后状态文件清除、未注入路径时不落盘、状态文件损坏时优雅降级。
- `TaskPersistentRunTests`（3）：配置重载后 detach 守护链继续重启、`GetRecent` 返回快照、进程已退出后停止必须跳过 taskkill。
- `TaskRunnerTests`（1）：日志写后立即可读 + 8 线程 × 40 行并发不丢行。
- `ConfigPersistenceTests`（1）：并发 Save/Load 下每次读取都完整无解析错误。
- `ScreenLockWindowTests`（1）：`WINDOWPOS` 封送布局。
- `ClipboardHistoryModuleTests`（1）：ToolTip 文本截断。
- `ComInteropTests`（2）：`ProcessJob` 必须声明终结器、创建/重复释放幂等。
- `AwakeCapabilityTests`（1）：特权启用入口签名 + 调用顺序（不真正睡眠）。
- `AudioDeviceChangeTests`（4）：设备变化刷新链路、`RaiseDevicesChanged` 入口、宿主启动确有调用点、服务 Start/Stop/Dispose 幂等。

### 仍未处理

| 编号 | 原因 |
|---|---|
| **H1** 锁屏钩子与焦点解耦 | 需非 100% 缩放 + 多显示器 + 前台切换实测；改动涉及键盘钩子与窗口激活，风险高 |
| **M2** `CommandHost` 超时后 handler 不可取消 | 需为能力 handler 引入 `CancellationToken`，属接口变更 |
| **M6** 单实例互斥体跨宿主语义 | 互斥体由宿主持有，`killWithHost=false` 时宿主退出即销毁；需专项设计与测试 |
| **M7** `AllowConcurrent` 时运行槽被覆盖 | 需重构运行注册表为"每任务多实例"模型，改动面较大 |
| **M9** Cron `5/2` 语义 | 属行为变更（会改变既有用户的触发次数），需产品确认 |
| **M14** Awake UI 线程全量枚举进程 | 需评估后台快照的线程安全边界，且需实测避免抖动 |
| **M16** AppAutoMute 白名单轮询周期 | 涉及静音状态机，需实测 |
| **M19 / M24** 多显示器 DPI 与副屏定位 | 需非 100% 缩放 + 多屏实测 |
| **M25 / M27** 编辑器脏检查、安装脚本 i18n | 改动面与收益比偏低，留待后续 |
| **M28** 测试质量 | 布局自检正则、时序脆弱用例未改（详见第二节说明） |
| **低危 L1–L20** | 多数为释放不规范与维护噪音，逐条修改的收益/风险比不划算 |
| **S3 明文存储** | 建议后续用 DPAPI 加密 `history.json` 存储层 |

### 测试基线

**336 通过 / 0 失败 / 0 跳过**（第一轮 312 → 第二轮 315 → 第三轮 336），每批均实际执行 `dotnet build`（0 警告 0 错误）与 `dotnet test`。
