# CarroDesk 单实例互斥与顶层入口重构设计与实现

**文档版本**：v1.0  
**日期**：2026-10-08  
**状态**：已实现并通过全量验证  

---

## 1. 背景与问题定位

在旧版本实现中，CarroDesk 的单实例互斥机制位于 `App.xaml.cs` 的 `OnStartup` 阶段，存在以下几个架构与用户体验缺陷：

1. **互斥体名称全局写死**：
   - 使用硬编码常量 `Global\CarroDesk_SingleInstance_2C7A4F10`；
   - **便携绿化场景受限**：若用户在多个不同目录下放置便携版（例如工作环境与个人环境、或者测试版与日常版），由于互斥体完全相同，无法同时运行多个独立实例；
   - **测试与开发冲突**：在开发机或 CI 环境中运行单元测试时，测试进程容易与开发者当前正在运行的 CarroDesk 冲突；
   - **全局作用域权限隐患**：`Global\` 命名空间在某些非管理员受限环境或 Windows 组策略受限会话中容易出现 `AccessDenied`。
2. **拦截时机偏晚，冷退出开销大**：
   - 依赖 WPF 自动生成的入口，第二实例在到达 `App.OnStartup` 之前，.NET WPF 运行时、PresentationFramework、Dispatcher 消息上下文及 XAML 解析管线已经全部初始化完毕，第二实例退出需要数百毫秒，空耗 CPU 与内存。
3. **用户体验与语义缺失**：
   - 第二实例若未携带命令行控制参数，直接调用 `Shutdown(0)` 静默退出；
   - 当 CarroDesk 已最小化在托盘中时，用户双击桌面图标没有任何界面反馈，容易让用户产生“没点上”或“软件卡死/闪退”的错觉；
   - 退出码返回 0，外部调用方（如自动化脚本或父进程）无法区分“已有实例运行跳过”与“正常退出”。

---

## 2. 总体架构与设计方案

本次改造参考了现代轻量桌面软件的最佳实践，结合 CarroDesk 自身的架构特性（便携模式、命令行管道 RPC 与多层异常防御网），将入口与单实例互斥全面解耦重构。

```
                    [ 进程启动 CarroDesk.exe ]
                                │
                                ▼
                       Program.Main(args)
                                │
          ┌─────────────────────┴─────────────────────┐
          ▼                                           ▼
   [ 是否 CLI/MCP 参数? ]                       [ 普通 GUI 启动 ]
          │                                           │
    ┌─────┴─────┐                                     ▼
   是           否                      计算数据目录 SHA256 确定性哈希
    │           │                       Mutex: Local\CarroDesk_{DataDirHash}
    │           │                                     │
    │           └───────────────────────┐             │
    ▼                                   │             ▼
[ctl 管道转发 / --mcp]                  │      [ 创建 Mutex ]
连接管道 RPC 回显并退出                 │             │
(耗时数毫秒，不拉起 WPF)                │       ┌─────┴─────┐
                                        ▼      !isNew      isNew
                                   (继续向下)    │           │
                                                │           ▼
                                                │    [ 初始化 WPF 框架 ]
                                                │    new App().Run()
                                                │    注册 HwndSource 唤醒监听
                                                │           │
                                                ▼           ▼
                                    Win32 定向唤醒既有实例  [finally]
                                    设置 ExitCode = 2 退出 释放与清理 Mutex
                                    (耗时约 70ms，不拉起 WPF)
```

---

## 3. 核心设计与技术实现

### 3.1 确定性数据目录 SHA256 哈希命名（`ConfigService.cs`）

单实例互斥的核心目的是**保护底层数据存储（配置 JSON、日志等）免受并发读写损坏**。因此互斥体命名应与实际生效的数据目录严格绑定：

1. **哈希算法**：
   - 获取当前生效的数据根目录 `ConfigService.DirPath`；
   - 调用 `Path.GetFullPath(dir).Trim().ToLowerInvariant()` 进行绝对路径小写归一化（Windows 文件路径大小写不敏感）；
   - 使用 `SHA256.HashData` 取前 4 字节转为 8 位大写十六进制字符串（避开 .NET `string.GetHashCode()` 跨进程随机加盐机制）；
2. **多模式兼容性**：
   - **普通漫游模式**：`DirPath` 恒为 `%AppData%\CarroDesk`，全机同一用户哈希全局唯一，保证无论从何处启动都严格单实例互斥并支持相互唤醒；
   - **便携绿化模式**：`DirPath` 为 `<exe所在目录>\app_data`，不同目录的便携实例天然派生出不同的哈希值，彼此互不干扰、支持并行运行；同一目录下的便携版重复运行则精确互斥；
   - **单元测试数据隔离**：测试环境（`TestEnvironment`）通过 `ConfigService.DataDirOverride = TempRoot` 重定向到独立临时目录，哈希完全隔离，测试运行期间绝对不会抢占或碰撞用户日常运行的 CarroDesk；
3. **命名空间收敛**：
   - 互斥体名称：`Local\CarroDesk_{DataDirHash}`
   - 激活消息名称：`CarroDesk_Activate_{DataDirHash}`
   - 前缀由 `Global\` 改为 `Local\`（Windows 登录会话内隔离），符合桌面应用程序规范，彻底消除低权限环境下的 `AccessDenied` 隐患。

### 3.2 独立顶层入口毫秒级冷拦截（`Program.cs`）

将 `src/CarroDesk.csproj` 配置 `<StartupObject>CarroDesk.Program</StartupObject>`，把控制权收归 `Program.cs`：

1. **CLI 命令与 `--mcp` 极速直达**：
   - 第一时间检测 `ControlArgs.IsControlInvocation(args)` 或 `args[0] == "--mcp"`；
   - 挂接控制台并直接通过 `PipeRpcClient` 走命名管道 RPC 转发并输出结果后退出；
   - 彻底避免 CLI 调用时加载庞大的 WPF 图形管线，命令行响应提升至毫秒级。
2. **第二实例毫秒级退出**：
   - 在进入任何 WPF 代码之前检测单实例 Mutex；
   - 若 `!isNew`，定向唤醒主实例并设置 `Environment.ExitCode = 2` 后立即 return 退出（实测冷退出耗时约 70ms）。
3. **首实例生命周期与 Mutex 释放**：
   - 仅新实例执行 `var app = new App(); app.InitializeComponent(); app.Run();`；
   - 在 `finally` 块中通过 `try { _instanceMutex?.ReleaseMutex(); }` 和 `_instanceMutex?.Dispose()` 安全释放互斥体句柄。

### 3.3 Win32 精准定向唤醒与 Helper 监听窗口（`NativeMethods.cs` + `App.xaml.cs`）

1. **投递端精准过滤（`NativeMethods.NotifyExistingInstance`）**：
   - 使用 `RegisterWindowMessage(activateMessageName)` 注册特定数据目录绑定的专属消息 ID；
   - 调用 `EnumWindows` 枚举顶层窗口，通过 `GetWindowThreadProcessId` 获取进程信息；
   - **双重过滤**：必须匹配同应用进程名（如 `CarroDesk`），且**严格排除第二实例自身的 PID**；
   - 使用 `PostMessage` 异步投递，避开 `HWND_BROADCAST` 广播风暴和 UIPI 权限隔离屏障；
   - 由于消息名称内嵌了 `DataDirHash`，多个不同目录的便携实例同时运行时，双击只会唤醒目标目录的实例，绝不发生交叉误唤醒。
2. **接收端常驻监听（`App.xaml.cs`）**：
   - 在 `App.OnStartup` 阶段创建无界面、宽高为 0 的轻量级 `HwndSource`（Helper Window），并挂接 `WndProc` hook；
   - 即使主界面处于隐藏/最小化到托盘状态，该 Helper Window 始终存活并接收消息；
   - 收到消息时切回 Dispatcher 线程调用 `ToggleFloatingPanel()`，主动唤出并激活悬浮面板；
   - 在 `OnAppExit` 时自动安全注销该 `HwndSource`。

---

## 4. 改动文件清单

| 文件路径 | 改动类型 | 核心职责 |
| :--- | :--- | :--- |
| `src/Services/ConfigService.cs` | 修改 | 新增 `GetDataDirectoryHash()`、`InstanceMutexName` 与 `ActivateMessageName` 属性 |
| `src/Common/NativeMethods.cs` | 新增 | Win32 原生 API 互操作封装（`RegisterWindowMessage`、`PostMessage`、`NotifyExistingInstance` 与进程过滤） |
| `src/Program.cs` | 新增 | 应用程序独立入口点，实现 CLI/MCP 优先拦截、单实例冷拦截与首实例 WPF 托管 |
| `src/CarroDesk.csproj` | 修改 | 添加 `<StartupObject>CarroDesk.Program</StartupObject>` 与 `InternalsVisibleTo` 属性 |
| `src/App.xaml.cs` | 修改 | 移除写死的 `Mutex` 声明与重复拦截逻辑，新增 `HwndSource` 专属唤醒消息监听与悬浮面板拉起 |
| `tests/CarroDesk.Tests/SingleInstanceTests.cs` | 新增 | 单实例哈希确定性、大小写不敏感、多目录隔离、跨线程 Mutex 排他性与窗口过滤测试用例 |
| `tests/CarroDesk.Tests/CarroDesk.Tests.csproj` | 修改 | 引入 `SingleInstanceTests.cs` 编译项 |
| `docs/CHANGES-20261008.md` | 修改 | 顶部追加重点变更摘要与时间戳 |

---

## 5. 验证与实测结果

### 5.1 自动化单元测试
在全量测试套件中包含本次新增的 5 项单实例互斥与数据隔离专项测试：
- `GetDataDirectoryHash_IsDeterministic_AndCaseInsensitive`（通过）
- `DifferentDataDirectories_ProduceDifferentHashesAndMutexNames`（通过）
- `SingleInstanceMutex_AcquiresAndBlocksSecondInstance`（通过）
- `IsSameApplicationWindow_ExcludesCurrentProcessAndZeroHwnd`（通过）
- `AbandonedMutex_CanBeSafelyAcquiredAndReleased`（通过，验证前持有者崩溃时两阶段构造安全接手与正常释放）

**全量测试套件执行结果**：
```
dotnet test tests/CarroDesk.Tests/CarroDesk.Tests.csproj
已通过! - 失败: 0，通过: 360，已跳过: 0，总计: 360 - CarroDesk.Tests.dll (net10.0)
```
**360 项测试全部通过，0 警告，0 错误**。

### 5.2 命令行与冷退出实测
1. **CLI 语法校验与回显**：
   - 运行 `CarroDesk.exe ctl`，控制台毫秒级输出 usage 并退出，退出码为 1，完全不拉起 WPF 界面。
2. **第二实例启动拦截与退出码**：
   - 模拟首实例持有 Mutex 运行，启动第二实例，实测仅耗时 **72.54 毫秒** 完成检测、投递唤醒并退出，退出码准确返回 **2**。

---

## 6. 边界条件、架构权衡与已知限制 (Review 补充)

### 6.1 `Local\` 会话命名空间的语义边界
- `Local\` 命名空间在 Windows 体系下是以**当前登录会话 (Logon Session)** 为边界的。
- 相同会话内（日常单机多开、便携多开、自启动、快捷方式双击）严格互斥生效；
- 若同一物理机的不同 Windows 用户会话（例如多用户远程桌面并发）访问同一个共享磁盘上的便携目录，两者的 `Local\` 互斥体彼此独立，不会跨用户互斥。桌面工具以当前登录会话为主，此为符合 Windows 桌面规范的明确设计取舍。

### 6.2 环境变量 `CARRODESK_DATA_DIR` 的一致性要求
- 单实例互斥与定向唤醒依赖主客实例派生出完全相同的 `DataDirHash`；
- 当使用环境变量 `CARRODESK_DATA_DIR` 覆盖数据目录时，主实例与第二实例必须处于能继承相同环境变量的上下文中。若在不同终端设置了不同环境变量值，系统将视其为不同数据目录分别运行。

### 6.3 32 位（8 位 Hex）哈希截断与碰撞概率
- 基于 SHA256 截取前 4 字节（32 位），理论碰撞概率为 \(1 / 2^{32} \approx 1 / 4.29 \times 10^9\)；
- 在单机有限的工作目录集合下，不同路径碰撞概率几乎为零，兼顾了互斥体名称简短与唯一性；
- 若 `Path.GetFullPath` 解析极端异常，退化逻辑基于程序基准目录 `BaseDirectory` 动态派生哈希，彻底杜绝静态固定串导致的跨目录误判。

### 6.4 唤醒链路的异步 Best-Effort 模型与 Hang 状态权衡
- 第二实例向主实例投递激活消息采用异步 `PostMessage`，属于 **非阻塞的 Best-effort 交付**；
- **为何不采用同步 `SendMessageTimeout`**：若主实例 UI 线程暂时繁忙或发生假死（Hang），同步阻塞会导致第二实例同样卡顿 3~5 秒，严重损害桌面启动体验。因此选择让第二实例毫秒级冷退出；
- **Helper 窗口常驻**：主实例通过 `HwndSource` 构建的 Helper Window 具有独立的 Win32 句柄，且不依赖主 UI 窗口的可见状态。

### 6.5 Fail-Open 策略的考量
- 在构造 `Mutex` 发生无法预期的系统底层异常（如内核句柄耗尽、组策略极端安全限制）时，代码选择 `fail-open` 允许启动；
- 这一取舍的核心在于**可用性优先**：避免因极端系统内核对象环境受限导致用户完全无法打开软件，同时在调试通道输出告警留痕。

### 6.6 两阶段 Mutex 获取机制（防 `AbandonedMutexException` 句柄丢失）
- 若直接使用 `new Mutex(true, name, out isNew)`，当互斥体处于 Abandoned 状态时构造函数抛出异常，会导致未赋值对象引用，`finally` 块中的 `ReleaseMutex()` 无法执行；
- 本方案采用**两阶段构造**：先使用 `new Mutex(false, name)` 确保引用可靠赋值给 `_instanceMutex`，再调用 `WaitOne(0)` 并在 `catch (AbandonedMutexException)` 中捕获接手锁，确保生命周期内对象引用不丢失且 `finally` 释放绝对覆盖。
