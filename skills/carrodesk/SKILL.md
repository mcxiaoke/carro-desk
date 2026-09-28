---
name: carrodesk

description: 通过 CarroDesk.Cli.exe 控制 Windows 本机：查询宿主/模块状态、启动和停止已授权的 Windows 服务（如 GameViewerService/UUYC、TermService/RDP，按服务的口令策略）。只要用户提到 CarroDesk、UUYC、GameViewer、远程桌面服务、启动/停止/重启某个服务、查这台电脑的运行状态或模块状态，就使用本技能——即使用户没说"CarroDesk"三个字。

---

# CarroDesk 本机控制

CarroDesk 是托盘常驻的本机控制服务。你通过它的命令行客户端（单文件 exe）发指令：
客户端把命令转发给正在运行的宿主进程，返回 JSON 结果。

- 客户端路径：`C:\Home\Tools\CarroDesk\CarroDesk.Cli.exe`（下文简写为 `CarroDesk.Cli`）
- **前提**：宿主 CarroDesk.exe 必须在运行。连不上时报 `error -32020: pipe transport failed`——此时提示用户启动 CarroDesk 托盘程序，不要反复重试
- 每条命令都是独立短进程，可以放心并发调用

## 用法模板

```text
CarroDesk.Cli ctl <能力名> [--json] [--pin <口令>] [--<参数名> <值>]
```

`--json` 输出缩进 JSON；退出码：0 成功 / 1 用法错误 / 2 业务错误 / 3 连不上宿主。

## 服务控制（最常用）

先查授权清单——返回每个服务的 `name`、`desc`（人类描述）、`requiresPin`（口令策略）、实时 `status`：

```text
CarroDesk.Cli ctl services.status
```

启动/停止（**是否需要口令由该服务的 requiresPin 决定，不是所有服务都要**）：

```text
:: GameViewerService（UUYC 远程）配置为免口令——直接调用
CarroDesk.Cli ctl services.start --name GameViewerService

:: TermService（RDP）配置为需口令——先向用户要 6 位口令再调用
CarroDesk.Cli ctl services.stop --name TermService --pin <用户提供的口令>
```

口令规则（违反会触发封锁）：
- `requiresPin: true` 的服务：**先问用户要口令**，放进 `--pin`；绝不猜测
- 口令错误最多重试一次；连错 5 次触发封锁期（`-32002 blocked`），期间口令正确也会被拒
- 用户没提口令就直接调用了需口令的服务 → 拿到 `-32002` 后**回去问用户**，不是重试

## 状态探查

```text
CarroDesk.Cli ctl host.status                :: 版本、运行时长、各模块状态
CarroDesk.Cli ctl host.status --json         :: 缩进 JSON，便于阅读
CarroDesk.Cli ctl host.capabilities.list     :: 全部能力的参数 schema（权威清单）
```

`host.status` 模块清单首项 `id:"host"` 是宿主伪模块（正常现象）；真实模块 `capabilityCount: 0` 表示该模块暂未开放能力（分批开放中），不是故障。

## 错误码速查

| 码 | 含义 | 你该做的 |
|---|---|---|
| -32601 | 无此能力 | 用 capabilities.list 核对名称 |
| -32602 | 参数非法（含服务不在授权清单） | 修正参数；服务被拒时告知用户需先配置授权 |
| -32002 | 口令缺失/错误/封锁 | 问用户要口令；blocked 时告知稍后再试 |
| -32003 | 限流 | 稍等再试 |
| -32004 | 执行超时 | 稍后用 services.status 查实际状态 |
| -32020 | 宿主未运行 | 提示用户启动 CarroDesk |

## 边界（没有的能力，不要尝试）

- 没有任意 shell / PowerShell 执行；系统级管理需求让用户走 SSH/远控
- 只能操作授权清单内的服务；清单外的服务被内核 DACL 拦截，配置也无法绕过
- 想加服务或改口令策略：让用户编辑 `C:\Home\Tools\CarroDesk\app_data\config.json` 的 `Services.AllowedServices`（`{name, desc, requiresPin}`），并以管理员运行 `docs/remote-admin/grant-service-control.ps1` 授予服务权限

## 深入资料

拿不准用法、想看完整约定（口令策略细节、能力边界、更多示例）时：

```text
CarroDesk.Cli ctl host.guide
```

返回 Markdown 手册全文（随宿主升级自动更新）。
