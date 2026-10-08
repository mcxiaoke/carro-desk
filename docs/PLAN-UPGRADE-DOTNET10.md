# 升级方案：CarroDesk net48 → .NET 10

> 状态：方案稿 v1（2026-10-07）
> 背景：为 ChatGateway 模块（企微/飞书 SDK 均要求 net8+）铺路，将宿主从 .NET Framework 4.8 升级到现代 .NET。
> 关联文档：[DESIGN-CHATGATEWAY.md](DESIGN-CHATGATEWAY.md)

## 1. 目标框架选择

| 候选 | 支持状态 | 结论 |
|---|---|---|
| .NET 8 | STS，**2026-11 到期（下个月）** | ✗ 排除 |
| .NET 9 | STS，2026-05 已到期 | ✗ 排除 |
| **.NET 10** | LTS，支持至 2028-11 | ✓ **目标框架**，`net10.0-windows` |

## 2. 工具选型与自动化程度（回答"能不能自动升级"）

| 工具 | 状态 | 自动化程度 |
|---|---|---|
| .NET Upgrade Assistant（VS 扩展 / `upgrade-assistant` CLI） | **官方已废弃**（2026-03 起文档标注 deprecated） | 不建议再采用 |
| **GitHub Copilot upgrade 代理（`@Modernize`）** | 现行推荐，**VS 2026 / VS 2022 17.14.16+ 内置** | **大部分自动化**，见下 |
| 手工升级 | — | 本项目改动面小，完全可行 |

### 2.1 Copilot upgrade 代理的能力与边界

**自动完成**：
- 评估（assessment）：扫描项目结构、依赖、代码模式，产出 `assessment.md`
  （破坏性变更、API 兼容问题、废弃模式清单）
- 规划（plan）：按确认的选项生成 `plan.md` / `tasks.md` 分步任务
- 执行：改 csproj（SDK 风格 / TFM / NuGet）、替换废弃 API、修构建错误，
  **每个任务一个 git commit**，可逐步回滚
- 流程模式：`automatic`（一路跑完只在真阻塞时停）与 `guided`（每阶段停下来等人审）
- 全部中间状态存在 `.github/upgrades/{scenarioId}/`（Markdown），可跨会话续跑、可提交留档

**仍需人工**：
- 关键决策确认（升级策略、依赖去留、单文件发布方式等）
- **回归验证**：8 个模块的手工功能验证、安装包实测——工具只保证"构建过、测试过"，
  不保证托盘常驻应用的行为完全无回归
- 发布流水线适配（release.py / Inno Setup 属于仓库脚本，工具不会改）

**前提条件**：VS 2026 + GitHub Copilot 订阅（登录 GitHub 账号）。
若无 Copilot 订阅，按 §4 手工步骤执行（本项目手工成本本来就低）。

### 2.2 本项目适配性

Copilot upgrade 支持的升级路径覆盖 "net Framework（任意版本）→ .NET 8+"，
支持项目类型含 WPF、Console、MSTest 测试工程——与 CarroDesk 三个项目完全匹配。

## 3. 升级改动清单（基于 2026-10-07 upgrade-assistant 分析报告核实）

> 报告：`temp\upgradereport\`（NewReport1.csv/json，6835 条）。
> 报告性质是**静态二进制/源码兼容性分析**，不执行构建，结论需人工甄别：

| 分类 | 数量 | 判定 |
|---|---|---|
| Mandatory "二进制不兼容" | 6633 | **全部为噪音**：全是 WPF 类型（TextBox/DispatcherTimer/MessageBox 等）被按 net48 程序集身份比对报错。net10 上 WPF 由运行时提供，TFM 改为 `net10.0-windows` 后整批自动消失，无需改代码 |
| Mandatory "需更改目标框架" | 3 | 真实工作：三个项目改 TFM |
| Mandatory "NuGet 包不兼容" | 1 | Costura.Fody（net48 only），本来就要删除 |
| Potential "源代码不兼容" | 197 | **真实工作量所在**，见下表 |
| Optional | 1 | MSTest 3.4.3 已弃用，顺手升 MSTest 4 |

### 3.1 Potential 197 条 → 具体动作（全部是"补 NuGet 包"级别）

| 缺失 API（报告聚类） | 集中文件 | 动作 |
|---|---|---|
| `System.ServiceProcess.ServiceController` 系列 (39) | ServiceControl 模块 | 装 `System.ServiceProcess.ServiceController`，API 平移 |
| `Microsoft.Win32.SystemEvents`（SessionSwitch/PowerModeChanged, 51） | ScreenLock、MonitorProfile、SessionEventTrigger | 装 `Microsoft.Win32.SystemEvents` |
| `System.Management`（WMI 查询, 9） | MonitorDdcService、TaskSchedulerService | 装 `System.Management` |
| `ProtectedData` / `DataProtectionScope` (6) | ClipboardStorageCrypto | 装 `System.Security.Cryptography.ProtectedData` |
| `System.Drawing.Icon` / `SystemIcons` (6) | 托盘图标加载 | 装 `System.Drawing.Common` |
| `System.Media.SystemSounds` (3) | 提示音 | 装 `System.Windows.Extensions` |
| `RNGCryptoServiceProvider` (1) | 已废弃类型 | 改用 `RandomNumberGenerator`（一行） |
| `HandleProcessCorruptedStateExceptionsAttribute` (1) | .NET Core 上已无效果 | 直接删除该特性 |

**代码级改动预计 < 10 行，其余全部是 csproj 加包引用。**

### 3.2 csproj / 脚本清单

| # | 项 | 现状 | 动作 |
|---|---|---|---|
| 1 | TargetFramework | `net48` × 3 项目 | → `net10.0-windows`（GUI）/ `net10.0`（CLI、Tests） || 2 | LangVersion | 显式 `7.3` | 删除，用默认（C# 13）；`Nullable` 暂保持 disable |
| 3 | Costura.Fody + Fody | 单文件嵌入 | **删除**，改 `PublishSingleFile`（Release 发布时生效；Debug 输出变多 DLL，属预期） |
| 4 | Framework references | `System.Management` / `System.Security` / `System.ServiceProcess` | → NuGet：`System.Management`、`System.ServiceProcess.ServiceController`（`System.Security` 大部分并入 BCL，按编译报错逐个补包） |
| 5 | Hardcodet.NotifyIcon.Wpf 2.0.1 / Newtonsoft.Json 13 | — | 保留，均支持现代 .NET（Newtonsoft → System.Text.Json 的迁移是可选的独立任务，不在本次范围） |
| 6 | CLI 单 exe | Costura 嵌入 CarroDesk.exe | 改为 publish 单文件；`CleanCliOutputJunk` Target 相应调整 |
| 7 | release.py | `dotnet build` | → `dotnet publish -r win-x64`（自包含 ~100MB+ 或框架依赖+运行时安装器，需决策） |
| 8 | Inno Setup | 引用 build 输出 | 引用 publish 输出，路径与体积上限调整 |
| 9 | 版本号 | Directory.Build.props 自定义 Target | 无需改动（与框架无关） |
| 10 | 数据目录 | `%AppData%\CarroDesk` / 便携 `app_data\` | 不受影响（config schema 无变化） |

预期代码级修改：以 csproj 和脚本为主，C# 源码预计只有零星 API 编译错误
（P/Invoke、命名管道、WPF 线程模型、自制 ServiceContainer 均平移兼容）。

## 4. 实施步骤

### 阶段 0：准备（人工，10 分钟）
1. 确认工作树干净，建分支 `feat/net10-upgrade`
2. 确认 .NET 10 SDK 已安装（`dotnet --list-sdks`）

### 阶段 1：自动升级（Copilot 代理或手工）
**路线 A（有 Copilot 订阅，推荐）**：VS 2026 打开解决方案 → 解决方案右键
**Modernize**（或 Copilot Chat 输入 `@Modernize`）→ 选 ".NET version upgrade" 场景 →
目标 .NET 10 → `guided` 模式逐阶段审阅 `assessment.md` → `plan.md` → 执行。
每个任务自动 commit，出问题随时回滚。

**路线 B（无订阅，手工等价流程）**：
1. 三项目改 TFM / 删 Fody / 换 NuGet 包（§3 清单 1–6）
2. `dotnet build` 逐个消除编译错误
3. `dotnet test` 全量测试修复

### 阶段 2：验证（人工，半天–1 天）
1. `dotnet test` 全绿（40 个测试文件）
2. 手工回归清单（常驻应用重点项）：
   - 托盘图标、菜单、多语言切换
   - ScreenLock 空闲锁屏 + PIN 解锁（P/Invoke 与钩子）
   - Awake 四种模式（SetThreadExecutionState）
   - TaskScheduler 计划任务 wait/detach 模式、进程树清理
   - ServiceControl 启停服务（SCM + DACL）
   - CLI 全部能力（`ctl host.status` / `awake.on` / `--mcp` 模式）
   - 便携模式数据目录
3. 常驻挂机 24–48 小时观察内存/句柄稳定性

### 阶段 3：发布链路（人工，半天）
1. release.py 改 publish 逻辑；决策安装包形态（推荐自包含单文件，免运行时依赖）
2. Inno Setup 实测全新安装 + 升级安装（per-user 免 UAC 流程不变）
3. 产出 zip/setup/SHA256SUMS，走一次完整发布演练

### 回滚方案
全程在 `feat/net10-upgrade` 分支，main 不动；Copilot 代理每任务一 commit 可精确回退；
最坏情况整分支废弃即可，现网 net48 版本不受影响。

## 5. 风险表

| 风险 | 概率 | 对策 |
|---|---|---|
| Costura → PublishSingleFile 的输出结构变化影响 Inno 打包 | 高（必然发生） | 阶段 3 专门处理；发布脚本与 iss 同步改 |
| 自包含包体积膨胀（单 exe → 100MB+） | 必然 | 可接受（本机工具）；或框架依赖+运行时安装器 |
| WPF 行为差异（NotifyIcon、Dispatcher、热键） | 低 | 回归清单覆盖；Hardcodet 2.0.1 官方支持新框架 |
| MSTest 3.4.3 在 net10 的行为差异 | 低 | 测试全量跑，逐个修 |
| Newtonsoft.Json 与新框架序列化行为差异 | 极低 | 本次不迁移，行为不变 |
| 升级期间 PicoClaw/日常使用中断 | — | 全程分支开发，不动现网安装版本 |

## 6. 完成后解锁（为什么值得做）

- ChatGateway 可直接引用 `WeCom.AiBot.Sdk`（企微智能机器人 C# SDK，net8.0），
  省去 600–800 行协议移植
- 飞书 `FeishuNetSdk` / `Mud.Feishu`、async streams、现代 DI 等生态全部解锁
- .NET 10 支持期到 2028-11，不再背 .NET Framework 的维护包袱
