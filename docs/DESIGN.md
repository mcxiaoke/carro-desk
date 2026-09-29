# CarroDesk 架构设计

> 版本：v2.0（2026-09-29，随代码现状更新）
> 平台：Windows 10 / 11
> 技术栈：.NET Framework 4.8 + WPF（C# 7.3）

## 1. 项目定位

CarroDesk 是一个常驻系统托盘的**模块化工作机管家**：宿主进程承载一组功能模块（锁屏、计划任务、保持唤醒、服务控制、音频/显示器辅助功能等），并通过命名管道 IPC + 命令内核把精选能力安全地暴露给外部客户端（CLI 单文件 exe、MCP stdio 适配器），供人或 AI 助手远程驱动本机的低风险操作。

演进历史：项目起源于 ScreenLock（闲时伪锁屏工具），2026-09 完成模块化宿主改造（`MODULAR-HOST-DESIGN.md` v2.0）并更名 CarroDesk；锁屏功能保留为 ScreenLock 模块。

设计原则：

- **In-Process Modular Monolith**：单进程多模块，无插件动态加载，模块间只经宿主上下文通信
- **瘦上下文**：模块只能从 `IModuleContext` 拿服务（pull），禁止向容器注册服务
- **能力即白名单**：IPC 只暴露显式注册的能力，未注册的一切不可达
- **纵深防御**：能力白名单 → 模块状态校验 → 参数 schema → 口令 → 限流 → 审计，服务控制另有内核 DACL 兜底

## 2. 总体结构

```
CarroDesk.slnx
├── src/                      CarroDesk.exe（WPF 宿主）
│   ├── App.xaml(.cs)         入口：单实例互斥、全局异常、托盘、ctl 二实例转发
│   ├── Core/                 模块契约层
│   │   ├── IModule.cs / ModuleBase.cs        生命周期与状态机
│   │   ├── IModuleContext.cs                 只读服务上下文
│   │   └── Commands/ICommandProvider.cs      能力暴露接口（pull 模式）
│   ├── Host/                 宿主层
│   │   ├── Commands/         CommandHost 分发、CommandRegistry 白名单、
│   │   │                     审计、限流、错误码、host.* 内置能力
│   │   ├── Ipc/              NamedPipeCommandServer、FrameCodec、RpcProtocol、
│   │   │                     McpStdioServer（MCP stdio 适配器）
│   │   ├── Modules/          ModuleRegistry（标准模块装配）
│   │   └── Services/         ModuleManager、ServiceContainer(DI)、
│   │                         ConfigManager、DynamicTrayController、HotkeyService…
│   ├── Modules/              功能模块（见 §3）
│   ├── Services/             ConfigService、PinService(+PinGuard)、
│   │                         ProcessExclusionService、Tasks 运行时…
│   └── Views/                LockWindow、TaskEditorWindow、TrayContextMenu 等
├── cli/CarroDesk.Cli/        CarroDesk.Cli.exe（Costura 单文件；ctl / --mcp）
├── tests/CarroDesk.Tests/    单元测试（291 通过）
├── skills/carrodesk/         AI agent 技能（CLI 直控，MCP 无关）
└── docs/                     设计文档、使用说明、AI 手册、remote-admin 脚本
```

## 3. 模块化宿主

### 3.1 模块契约与生命周期

`IModule` / `ModuleBase<TConfig>`（`src/Core/`）定义统一生命周期，状态机：

```
Created → Initialized → Running → Stopped
                            └────→ Faulted（Start 异常时逆序清理后标记）
                                    Disabled（config.Enabled=false，跳过启动）
```

`ModuleBase` 提供的公共服务：配置节泛型加载、幂等 Start/Stop、托管热键注册（`RegisterManagedHotkey`，停止自动注销）、配置热重载回调 `OnConfigReloaded`、语言切换回调 `OnLanguageChanged`、托盘菜单项 `GetTrayMenuItems()`、能力暴露 `GetCommands()`（`ICommandProvider`，与托盘菜单同构的 pull 模式）。

模块清单（`src/Host/Modules/ModuleRegistry.cs` 一次注册）：

| 模块 | Order | 职责 | IPC 能力 |
|---|---|---|---|
| ScreenLock | — | 空闲/热键 PIN 锁屏 | — |
| TaskScheduler | — | 计划任务调度与执行 | —（B4 二期） |
| Awake | 15 | 保持唤醒 | awake.status / awake.on / awake.off |
| AudioSwitch | — | 热键切默认音频设备 | — |
| AppAutoMute | — | 目标进程自动静音 | — |
| MonitorProfile | 30 | DDC 显示器配置档定时切换 | — |
| ClipboardHistory | 35 | 剪贴板历史 | — |
| ServiceControl | 900 | Windows 服务启停（无托盘 UI） | services.status / services.start / services.stop |

宿主自身作为伪模块（`id: "host"`，`isHost: true`）提供 `host.hello / host.guide / host.status / host.modules.list / host.capabilities.list` 五个能力。`capabilityCount: 0` 表示该模块尚未开放能力（分批开放中），不是故障。

### 3.2 宿主服务

- **ModuleManager**：按 Order 装配/启停，`CollectCommands()` 汇总各模块能力（逐模块 try/catch 隔离故障），`GetModuleStatus()` 输出状态快照
- **ServiceContainer**：手写 DI；模块可 `GetService<T>()` 取共享服务（PinService、HotkeyService、日志、通知等）
- **DynamicTrayController + MenuProjectionEngine**：`TrayContextMenu.xaml` 预留 `PluginSlotAnchorTop/Bottom` 双锚点，各模块菜单项按 Order 注入（250ms 防抖）；顶层静态项：桌面控制面板、开机自启、配置编辑器、打开配置目录、重载配置、语言、退出（需验 PIN）
- **ConfigManager/ConfigService**：JSON 配置 + 热重载；数据目录优先级 `CARRODESK_DATA_DIR` > 便携模式（exe 旁 `portable.ini` → `app_data\`）> `%AppData%\CarroDesk\`

## 4. IPC 层（S1–S4 已实现，S5 预留）

```
外部客户端                     宿主
─────────────                 ─────────────────────────────────────
CarroDesk.Cli.exe ctl ──┐
CarroDesk.exe ctl ──────┤     NamedPipeCommandServer（每用户管道，ACL 限本用户+SYSTEM）
  （二实例转发）          ├──→    ↓ FrameCodec：[int32 LE 长度][UTF-8 JSON]，1MB 上限
CarroDesk.Cli --mcp ────┘       RpcProtocol：JSON-RPC 2.0 + pin/source 扩展字段
  （stdio ↔ 管道桥）               ↓
                               CommandHost（命令内核，固定校验序）
                                 ↓
                         CommandRegistry（能力白名单）→ 模块实现
```

### 4.1 传输

- 管道名 `\\.\pipe\CarroDesk.ctl.<用户SID>`（缺 SID 退化 `.default`）；`PipeSecurity` 仅当前用户 SID + SYSTEM
- 帧协议：4 字节小端长度 + UTF-8 JSON，单帧上限 1MB，超限断开；显式 64KB 缓冲区避免双向写死锁
- `CarroDesk.exe ctl ...` 作为二实例把命令转发给已运行的宿主后退出（与 CLI 共用 `ControlArgs` 解析）

### 4.2 命令内核（CommandHost）

校验顺序固定，任一步失败即短路返回：

| 步骤 | 校验 | 失败错误码 |
|---|---|---|
| 1 | 能力存在于注册表 | -32601 CapabilityNotFound |
| 2 | 所属模块 Running | -32001 ModuleUnavailable |
| 3 | 参数 schema（未知参数拒绝；string/int/bool 收敛；枚举精确匹配） | -32602 InvalidParams |
| 4 | 口令（`RequiresPinFor` 动态判定 + PinGuard 挑战） | -32002 PinRequired |
| 5 | 限流（默认每能力×来源 30 次/分钟） | -32003 RateLimited |
| 6 | 执行（默认超时 5000ms） | 超时 -32004 / 异常折叠 -32010 |

错误码全集：-32700 ParseError、-32601、-32602、-32001、-32002、-32003、-32004、-32010 Internal、-32020 TransportError（客户端连不上管道，通常宿主未运行）。

**审计**：每次调用写 `ipc-audit.log`（1MB 轮转），参数只记 SHA-256 摘要，`pin` 单独提取不进摘要与参数（只记 PinUsed 布尔）。

### 4.3 口令（PinService + PinGuard）

- PIN 哈希 PBKDF2-SHA256 100k 迭代 + 随机盐，兼容旧 SHA256 哈希透明升级；数据在 config.json `PinSalt/PinHash`
- PinGuard：连错 5 次进入封锁期，第 n 次超限封锁 `(n-4)*30s` 递增，封锁期内正确口令也拒绝（-32002 blocked）
- 宿主退出、锁屏解锁等敏感动作复用同一 PIN 校验

### 4.4 客户端

- **CLI**（`cli/CarroDesk.Cli`）：`ctl <capability> [--json] [--pin xx] [--pipe name] [--timeout ms] [--source s] [--<param> <value>]`；退出码 0 成功 / 1 用法错误 / 2 业务错误 / 3 连不上宿主
- **MCP stdio 适配器**（`McpStdioServer`）：`--mcp` 启动；`initialize` 回显协议版本 + 行为约定 instructions，`tools/list` 首次经 `host.capabilities.list` 拉取并缓存（自动随宿主升级），`tools/call` 按约定把 `arguments.pin` 提取为请求口令；stdout 只出协议 JSON，日志走 stderr
- **host.guide**：返回宿主内嵌的 `docs/AI-AGENT-MANUAL.md` 全文，AI 助手的深度用法参考，随宿主发布自动更新

### 4.5 安全边界（明确不做的）

- ❌ 任意 shell / PowerShell 执行 —— 系统性管理走 `docs/remote-admin/` 的 SSH/Agent 带外通道
- ❌ 剪贴板内容读取、任意文件读写、配置整体覆写
- ❌ `host.exit` 等远程退出宿主的能力永不开放
- ❌ 授权清单之外的服务操作 —— 配置只是参数枚举，真正的边界是服务对象 DACL（管理员一次性授予，见 `docs/remote-admin/grant-service-control.ps1`）

## 5. 任务系统（TaskScheduler）

### 5.1 调度

触发器 11 类：startup / interval / daily / cron（5 字段）/ sessionLock / sessionUnlock / idle / manual / hotkey / watch（FileSystemWatcher，500ms 防抖）。`tasks.json` 支持注释，托盘热重载；条件 `when`（onlyIdle/acPower/networkAvailable/fileExists/fileNotExists）不满足记 `skipped(condition)`。`file/args/workDir` 支持 `%VAR%` 与 `{{date}}` 等模板。

### 5.2 运行模式（B1：wait / detach）

`options.mode` 决定任务的运行形态：

- **`wait`（默认）**：宿主拉起子进程并等待退出，受 `timeoutSec`/`retry`/`allowConcurrent` 约束，输出进任务日志
- **`detach`**：启动后立即返回，任务作为常驻后台进程存在；`timeoutSec/retry/allowConcurrent` 不可用（`Validate()` 拒绝）
  - `killWithHost`（默认 true）：经 Job Object 挂 kill-on-close，宿主退出子进程随之结束；false 时脱离（Stop 走 `taskkill /T /F` 降级链）
  - `singleInstance`（B2）：命名互斥体 `Local\CarroDesk.Task.<name>` 防重复拉起；进程内登记表先行防重入，AbandonedMutex 视为前任死亡安全接管，失败 fail-open
  - `restart: "on-failure"`（B3）：守护自动重启，`restartDelaySec`（1-3600，默认 5）、`restartLimit`（1-100，默认 3）、`stableUptimeSec`（默认 60，稳定运行满即复位连续失败计数）；超限熔断标记 failed，保存任务/手动运行/宿主重启可复位

内部实现：运行注册表 `Dictionary<name, RunSlot>`（`TaskProcessHandle` 统一封装进程+Job Object+退出监视）；任务编辑器"测试运行"有 10s 兜底超时、独立停止按钮与防重入，detach 任务测试运行不等待。

## 6. 服务控制（ServiceControl）

双层安全：

1. **配置层**：config.json `Services.AllowedServices: [{name, desc, requiresPin}]`（兼容旧字符串写法）；`services.start/stop` 的 `name` 参数 AllowedValues 即清单，清单外一律 -32602
2. **内核层**：服务对象 SDDL DACL 由管理员一次性授予 ACE（`docs/remote-admin/grant-service-control.ps1`，支持批量与 `-DryRun` 预览，备份原 SDDL 可回滚）；未授予时服务启停返回拒绝并指引运行脚本

按服务口令策略：`requiresPin: false`（如无害高频的 GameViewerService）免 PIN；`requiresPin: true`（如停掉会断 RDP 的 TermService）必须带 PIN，缺/错均 -32002。Access denied（Win32 错 5）时返回授予脚本指引。

## 7. ScreenLock 模块（保留功能）

空闲（`GetLastInputInfo` 轮询）或热键触发全屏、置顶、覆盖全部显示器的伪锁屏，PIN 解锁（复用全局 PinService），低级键盘钩子拦截 Win/Alt+Tab/Alt+F4 等。暂停条件聚合：会话已锁 / 暂停计时 / 全屏忙碌 / **排除进程运行中**（`ExcludeProcesses`，`ProcessExclusionService` 2s 缓存，适合游戏挂机）。属"防误碰/提醒"性质，非安全软件（Ctrl+Alt+Del 不可拦截）。

## 8. 构建、测试与发布

- `CarroDesk.slnx`：`src`（主程序）、`cli/CarroDesk.Cli`（单文件 CLI）、`tests/CarroDesk.Tests`
- 全部 .NET Framework 4.8 / C# 7.3，Win10/11 免安装运行时；CLI 经 Costura 内嵌依赖输出单 exe
- 测试：`tests/CarroDesk.Tests`（291 通过，2026-09-29）
- 发布产物保持最小集：exe + 配置样例 + pdb；部署目标如 `C:\Home\Tools\CarroDesk\`（便携 + `app_data\`）

## 9. 相关设计文档

- `MODULAR-HOST-DESIGN.md` —— 模块化宿主总设计（v2.0，已兑现）
- `IPC-CAPABILITY-EXPOSURE-DESIGN-20260926.md` —— IPC/能力暴露设计（S1-S4 已实现，S5 远程网关预留）
- `SERVICE-CONTROL-PLAN-20260928.md` —— 服务控制方案（已实施）
- `TASKS-PERSISTENT-RUN-DESIGN-20260928.md` —— 常驻任务设计（B1/B2/B3 已实施，B4 tasks.* 能力二期）
- `MODULE-DEVELOPMENT-GUIDE.md` —— 模块开发指南
- `I18N-DESIGN.md`、`AUTORUN-DESIGN.md`、`HOTKEY-SYSTEM-REFACTOR-PROPOSAL-20260918.md` —— 专项设计
- `CHANGES-20260926.md` —— 变更日志
