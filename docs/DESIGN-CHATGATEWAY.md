# 设计方案：ChatGateway 模块（企微聊天入口 → opencode 执行）

> 状态：设计稿 v1（2026-10-07）
> 目标：以 CarroDesk 常驻宿主为载体，新增一个企微（WeCom 智能机器人）出站 WebSocket 通道模块，
> 把聊天消息转发给 `opencode run` 执行并回传结果，替代 PicoClaw 的全部在线职责。

## 1. 背景与动机

- 现用 PicoClaw（Go，常驻 ~40MB）承担"手机企微发消息 → 本机执行"的入口，但其默认模型
  （OpenRouter 免费小模型）对中文口语指令理解差，已发生一次"关游戏被误解为休眠整机"的事故。
- opencode（v2.0.22，本机已装）CLI 可非交互执行且可用 OpenRouter 全系强模型，
  `opencode run` + `-s <session>` 支持多轮会话，适合作为"大脑"。
- CarroDesk 已是常驻托盘宿主，模块化、带安全模型（命令白名单/PIN/审计），
  由其承接传输层可减少一个常驻进程，且自带托盘/日志/配置热重载等基础设施。

## 2. 总体架构

```
手机企微 ──wss(出站)──> CarroDesk[ChatGateway模块] ──Process──> opencode run -m <模型> -s <会话>
                            │  allowlist 校验 / 会话映射 / 并发闸 / 超时管理
                            └──回复文本──wss──> 手机企微
```

职责切分：

| 层 | 职责 | 明确不做的事 |
|---|---|---|
| ChatGateway（CarroDesk 模块） | 企微协议、白名单、消息队列、会话映射、超时与重连 | 不理解消息内容，原样转发 |
| opencode | 理解自然语言、决策、执行命令 | — |
| opencode.json（配置） | 工具权限与命令黑名单 | — |

安全要点：PicoClaw 的 exec 黑名单**无法穿透**到 opencode 内部 shell，
因此危险命令拦截必须落在 opencode.json 的 permissions 配置里（见 §7）。

## 3. 模块结构（遵循 MODULE-DEVELOPMENT-GUIDE）

```
src\Modules\ChatGateway\
├── ChatGatewayModule.cs        # ModuleBase<ChatGatewayConfig>, ICommandProvider
├── Models\
│   └── ChatGatewayConfig.cs    # 配置模型（config.json 顶层 "ChatGateway" 节点）
└── Services\
    ├── IChatTransport.cs       # 传输抽象（收/发/断线事件），便于测试与未来接 Telegram 等
    ├── WeComBotClient.cs       # 企微智能机器人 WebSocket 客户端（IChatTransport 实现）
    ├── RequestDispatcher.cs    # 收到消息 → 校验 → 排队 → ack → 调 Runner → 回复
    ├── OpencodeRunner.cs       # 封装 opencode run 子进程（复用 TaskProcessHandle 范式）
    └── ChatSessionStore.cs     # chat_id → opencode session 映射（持久化）
```

注册：`src\Host\Modules\ModuleRegistry.cs` 的 `RegisterStandardModules()` 中 `RegisterModule<ChatGatewayModule>()`。

## 4. 配置设计（config.json 顶层节点）

```jsonc
"ChatGateway": {
  "Enabled": true,
  "BotId": "aibs...",                       // 企微智能机器人 ID
  "WebSocketUrl": "wss://openws.work.weixin.qq.com",
  "AllowedUserIds": ["ZhangXiaoKe"],        // 硬白名单，空=拒绝所有人（安全默认）
  "Model": "openrouter/anthropic/claude-haiku-4.5",
  "WorkspaceDir": "C:\\Home\\Temp\\agent-workspace",  // opencode 工作目录
  "TaskTimeoutMinutes": 10,                 // 单次 opencode 执行超时
  "MaxConcurrentTasks": 1,                  // 全局并发上限（1=串行，保护弱机器）
  "AckEnabled": true,                       // 收到任务先回一条确认
  "MaxReplyChars": 1800                     // 企微单条上限，超长分片
}
```

热重载：`OnConfigReloaded()` 推送到各服务；BotId/Url 变更触发重连，白名单立即生效
（参考 AwakeModule.OnConfigReloaded 的推送写法）。

## 5. 关键技术决策

### 5.1 WebSocket 客户端（首个网络长连先例，需自建模式）

- 用 BCL 自带 `System.Net.WebSockets.ClientWebSocket`（net48 原生），**不引入 NuGet 依赖**，
  避免 Costura.Fody 嵌入清单变动。
- 收发循环：`Task.Run` 驱动 + `CancellationTokenSource`，**禁止 DispatcherTimer 做网络 IO**
  （仓内已有 TaskSchedulerService 的教训）；IO 线程一律 `ConfigureAwait(false)`，
  UI 通知经 `Context.Dispatcher.BeginInvoke`。
- 重连：指数退避 1s→2s→4s→…→上限 5min，抖动 ±20%；连接状态用 lock 保护的状态机
  （Disconnected/Connecting/Connected/WaitingRetry，参考 IpPresenceDetector 的写法）。
- 发送并发：`SemaphoreSlim(1,1)` 串行化单连接写操作。
- 心跳：按企微协议要求响应 ping/keepalive 帧；`await ws.ReceiveAsync` 超时判定半开连接。

### 5.2 企微协议实现路径（2026-10-07 调研结论）

- 生态现状（调研核实）：
  - 官方仅提供 Node.js SDK（`WecomTeam/aibot-node-sdk`，TS，结构干净：
    ws.ts/client.ts/crypto.ts/api.ts，但仓库未声明 license，只作协议参考）。
  - NuGet 曾有 `WeCom.AiBot.Sdk`（net8.0 only，源码仓库已 404），仅可当 API 设计参考，
    且 TFM 不兼容 net48。
  - 飞书/钉钉的 C# 生态见 §12 对比；企微智能机器人无 net48 可直接使用的 SDK。
- **结论：自研协议层，以 Sipeed/PicoClaw（MIT，30k star，活跃维护）的
  `pkg/channels/wecom` 为主要移植参考**——MIT 许可允许代码借用；
  且这正是当前生产环境在跑的实现，行为可对照验证。
- 协议细节（auth 帧、AES 加解密、回执、流式回复）全部收在 `WeComBotClient` 内部，
  其余代码只面向 `ChatMessage`/`ChatReply` 抽象（IChatTransport），
  未来可加飞书/Telegram 等传输实现，返工面被隔离在单类内。
- 开发期打开原始帧日志（`--log-level debug` 记录全部收发帧），用真实流量校准移植代码。
- 预估协议层体量：600–800 行 C#（含加解密与单测），是本模块最大的单一工作量。

### 5.3 子进程：复用 TaskRunner/TaskProcessHandle 范式

- `OpencodeRunner` 用 `Process.Start` → `TaskProcessHandle`（含 kill-on-close 作业对象，
  `killWithHost` 语义：宿主退出不遗留 opencode/Node 进程树）。
- 命令形如：
  `opencode run "<消息>" -m <Config.Model> -s <会话id> --format json`
  （工作目录 = `WorkspaceDir`；会话 id 首次为空，由 opencode 返回后存入 `ChatSessionStore`）。
- `--format json` 输出解析失败时降级把 stdout 纯文本直接回传，不丢结果。
- 超时：`Task.WhenAny` 三路竞速（退出/超时/宿主停止），超时走 taskkill /T /F 降级链。
- 转发消息时对引号/换行做参数转义；长消息用 `-f` 临时文件传递而不是命令行参数。

### 5.4 会话与并发

- `ChatSessionStore`：`chat_id → opencode session id` 持久化（模块数据文件，原子写），
  宿主重启后会话不丢；提供"新会话"指令（如用户发送 `/new`）清映射。
- 每 chat 串行（同一人连发排队），全局并发 = `MaxConcurrentTasks`；排队时回 ack。
- 消息去重：企微可能在回执超时后重推，按 `message_id` 做 10 分钟 LRU 去重表。

### 5.5 对外能力（GetCommands，供 CLI/测试/未来 MCP 使用）

| 能力名 | 风险 | 说明 |
|---|---|---|
| `chatgateway.status` | ReadOnly | 连接状态、当前任务、会话数、队列深度 |
| `chatgateway.send --text xx` | Privileged | 主动推送到绑定 chat（测试/运维通知） |
| `chatgateway.reconnect` | Privileged | 强制重连 |
| `chatgateway.session.clear --chat xx` | Privileged | 清除某会话映射 |

## 6. 消息处理流水线（RequestDispatcher）

```
收到帧 → 解密解析 → sender 白名单? ──否──> 拒绝(可配静默) + LogWarning
              │是
        message_id 去重 → 入队(每chat串行) → 回 ack("收到，开始处理…")
              → OpencodeRunner.Run(msg, session) → 解析结果
              → 长度分片 → 回复企微；异常 → 回友好错误 + LogError
宿主 Stop 时：停止收新消息 → 取消在跑任务(作业对象兜底) → 优雅断开 ws
```

## 7. opencode 侧安全配置（必须与模块同步交付）

`WorkspaceDir\.opencode\opencode.json`（或全局配置）：

- 不使用 `--auto` 全自动批准；在 permissions 中显式 allow 文件读写与常规 bash，
  deny 高危命令（与 PicoClaw 黑名单同款正则）：
  `rundll32*`、`*SetSuspendState*`、`*SetSystemPowerState*`、`shutdown*`、
  `Stop-Computer*`、`Restart-Computer*`、`powercfg /h*`、`logoff*`。
- PicoClaw 退役前的双跑期，两份黑名单保持同步演进。

## 8. 测试与验收

- 单元测试（MSTest，记得在 CarroDesk.Tests.csproj 手工 `<Compile Include>`）：
  - 白名单/去重/会话映射/配置解析（纯逻辑，无网络）
  - opencode JSON 输出解析（构造样例输出：成功/失败/降级纯文本）
  - Mock IChatTransport 的 Dispatcher 全流程（含超时与队列）
- 手工验收清单：
  1. `ctl chatgateway.status` 各状态正确（未配置/未连接/已连接/重试中）
  2. 白名单外用户发消息被拒并有审计日志
  3. 正常问答、多轮会话（session 复用）、`/new` 重开会话
  4. 任务超时（>TaskTimeoutMinutes）自动终止并回执
  5. 断网/断流自动重连（拔网线实测）；宿主重启后会话保持
  6. 危险指令（"帮我关机"）被 opencode.json 拦截且回复可理解
- 发布：走 `scripts/release.py` 常规流程；灰度期 Config `Enabled=false` 随安装包下发，
  验证后再启用。

## 9. 实施阶段划分

| 阶段 | 内容 | 产出 |
|---|---|---|
| P1 骨架 | 模块骨架 + 配置模型 + status 能力 + 注册 + 单测 | 无网络行为，host.status 可见新模块 |
| P2 传输 | WeComBotClient（连接/心跳/重连/收发/帧日志）+ echo 自测 | 手机发消息收到原样回显 |
| P3 执行 | OpencodeRunner + SessionStore + Dispatcher（ack/超时/去重/分片） | 全功能可用 |
| P4 加固 | opencode.json 权限、错误分类文案、测试补齐、release | 可日常使用，退役 PicoClaw |

体量估计：含测试约 1500–2500 行 C#。

## 10. 风险与对策

| 风险 | 对策 |
|---|---|
| 企微协议无现成 C# 实现，细节踩坑 | 帧日志 + PicoClaw Go 源码对照；协议隔离在单类内，返工面小 |
| net48/C# 7.3 无 async streams | 传统 while 循环 + 缓冲区解析，语法用 ValueTask 之外的常规写法 |
| opencode 冷启动慢（Node） | ack 文案先行；后台服务 `opencode service` 预热可选，不做依赖 |
| 强模型费用失控 | 默认中档模型（haiku/glm-flash 级），对话内可指定升级；`stats` 观测用量 |
| 与 PicoClaw 双跑期 bot_id 冲突 | 同一 bot_id 只允许一个连接，切换时先停 PicoClaw 计划任务 |
| 宿主更新打断在跑任务 | 作业对象兜底杀树；会话映射持久化，重发消息可续会话 |

## 11. 与现有设施的协同（后续可选项）

- CarroDesk 已有 `--mcp` stdio 适配：把 CarroDesk 能力挂给 opencode 作为 MCP server 后，
  agent 可自主调用 `awake.on`（执行长任务时保持唤醒）、`services.*` 等——形成
  "聊天入口 + 强模型 + 本机运维能力"闭环，此为 ChatGateway 落地后的第二步。

## 12. 附：国内 IM 的 .NET SDK 生态对比（2026-10-07 NuGet/GitHub 核实）

| 平台 | 候选 | TFM | net48 可用 | 评估 |
|---|---|---|---|---|
| 企微智能机器人 | WeCom.AiBot.Sdk v1.0.6 | net8.0 | ✗ | 源码仓库已 404，仅 API 设计参考 |
| 企微智能机器人 | **PicoClaw pkg/channels/wecom（Go）** | Go | （移植） | MIT、活跃维护、生产验证，**首选移植源** |
| 企微智能机器人 | WecomTeam/aibot-node-sdk（TS） | TS | （参考） | 官方出品但无 license 声明，只作协议参考 |
| 飞书 | **Mud.Feishu.WebSocket v3.0.0** | netstandard2.0 | ✓ | 自带重连/心跳/事件分发；依赖 M.E.* 8.x + STJ + protobuf-net（纯 .NET，Costura 可嵌） |
| 飞书 | FeishuNetSdk v4.3.3（+WebSocket 扩展，5 万下载） | net8/9/10 | ✗ | 更主流但 TFM 不兼容 net48 |
| 钉钉 | Jusoft.DingtalkStream v0.1.8 | 社区 | （未核实） | 支持机器人消息回调，成熟度一般 |

结论：**留在企微**（bot 已就绪、手机端习惯不变、有 MIT 高质量参考实现）；
若不愿做协议移植，换飞书 + Mud.Feishu.WebSocket 是唯一"直接用库"的路线，
代价是注册飞书应用 + 手机端迁移。协议被 IChatTransport 隔离后，两条路线可共存演进。
