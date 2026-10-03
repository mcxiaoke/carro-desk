# CarroDesk

Windows 常驻托盘工作机管家：模块化的桌面自动化宿主，集空闲锁屏、计划任务、保持唤醒、Windows 服务控制于一体，并通过命名管道 IPC 对外暴露 **MCP / CLI** 接口，可被 AI 助手（MCP 客户端）或人肉命令行直接驱动。

> 前身是 ScreenLock（闲时伪锁屏工具），现已重构为模块化宿主 CarroDesk；锁屏保留为其中一个模块。

## 功能总览

| 模块 | 功能 |
|---|---|
| **ScreenLock** | 空闲/热键触发的全屏 PIN 锁屏（多显示器、防暴力破解、排除进程挂机） |
| **TaskScheduler** | 计划任务：11 种触发器（startup/interval/daily/cron/hotkey/watch/idle/…），支持 `wait`（等待退出）与 `detach`（常驻后台）两种运行模式，detach 任务支持单实例互斥与失败自动守护重启 |
| **Awake** | 保持唤醒：无限期/定时/到点/进程联动四种模式，防止机器睡眠导致远程失联 |
| **ServiceControl** | 经授权清单启停 Windows 服务（如 UUYC 远控、RDP），按服务配置口令策略，内核级服务 DACL 兜底 |
| **AudioSwitch** | 热键一键切换默认音频设备（扬声器/耳机） |
| **AppAutoMute** | 目标进程运行时自动静音、退出恢复 |
| **MonitorProfile** | DDC/CI 按时间表切换显示器亮度/对比度配置档 |
| **ClipboardHistory** | 剪贴板历史记录与快速检索窗口 |

宿主能力：模块化生命周期管理（`ModuleBase` 统一启停/热键/配置热重载）、动态托盘菜单、多语言（zh-CN / en-US）、便携模式、开机自启。

## 对外接口（IPC）

宿主运行后开放命名管道 `\\.\pipe\CarroDesk.ctl.<SID>`，由独立单文件客户端 `CarroDesk.Cli.exe` 接入：

- **CLI**：`CarroDesk.Cli ctl <capability> [--pin xx] [--key value] [--json]`，脚本/人肉可用
- **MCP**：`CarroDesk.Cli --mcp` 以 stdio 适配器形态接入任意 MCP 客户端（OpenClaw / AstrBot / opencode 等），工具清单自动下发
- **能力清单**（随宿主升级自动增长）：`host.*`（状态/模块/能力表/内嵌手册）、`awake.*`（状态/开/关）、`services.*`（状态/启动/停止）；其余模块能力分批开放中

安全模型：命令白名单 + 模块运行校验 + 参数 schema 校验 + 口令（PinGuard 连错 5 次封锁）+ 限流 + 审计日志（参数仅记摘要）。

```cmd
CarroDesk.Cli ctl host.status
CarroDesk.Cli ctl awake.on --minutes 120
CarroDesk.Cli ctl services.start --name GameViewerService
```

## 构建与部署

- Visual Studio 打开 `CarroDesk.slnx`，目标框架 .NET Framework 4.8（Win10/11 自带运行时），C# 7.3
- 产物：`src` → `CarroDesk.exe`（WPF 宿主），`cli` → `CarroDesk.Cli.exe`（Costura 单文件），`tests` → 单元测试
- 打包发布：`python scripts/release.py`，产出便携 zip 与 NSIS 安装包（需先 `scoop install nsis`）
- 安装包：per-user 安装到 `%LOCALAPPDATA%\Programs\CarroDesk`，免管理员权限；安装/升级/卸载会自动结束主进程与 CLI 进程，升级无需手动杀进程
- 便携部署：exe + `portable.ini` 放任意目录即为便携模式（数据在同级 `app_data\`），否则使用 `%AppData%\CarroDesk\`

## 文档

| 文档 | 内容 |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | 架构设计：模块化宿主、IPC 命令内核、安全模型 |
| [docs/USAGE.md](docs/USAGE.md) | 使用说明：部署、各模块配置、任务系统、服务控制 |
| [docs/AI-AGENT-MANUAL.md](docs/AI-AGENT-MANUAL.md) | AI 助手接入手册（MCP/CLI，宿主内嵌，`host.guide` 可读） |
| [docs/MODULE-DEVELOPMENT-GUIDE.md](docs/MODULE-DEVELOPMENT-GUIDE.md) | 模块开发指南 |
| [docs/remote-admin/](docs/remote-admin/README.md) | 远程管理通道（服务 DACL 授予、RDP/SSH/Agent 初始化脚本） |
| [docs/CHANGES-20260926.md](docs/CHANGES-20260926.md) | 近期变更日志 |
