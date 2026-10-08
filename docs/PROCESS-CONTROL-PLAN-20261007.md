# CarroDesk 进程控制能力方案（远程结束进程）

> 版本：v1.3（2026-10-07，据用户反馈修正 WMI 路径结论：本机火绒拦截 WMI 终止进程，实现优先级改为原始 Win32 API 为主）
> 版本：v1.2（2026-10-07，按位定位失败根因：`taskkill` 缺 `PROCESS_QUERY_INFORMATION`，并逐位证实靶子与真实提权目标等价）  
> 版本：v1.1（2026-10-07，补 §2.4「各种结束方式可用性实测」，修正摘要第 3 条）  
> 状态：**待评审（暂缓）** —— 2026-10-07 决定：先不为它新增 CarroDesk 能力，直接由远程 agent 结束进程（可用写法见 §2.4）；本文档转为备选方案，当需要"白名单 + 口令 + 审计"约束时再启用。  
> 目标场景：人在外，通过 AI 助手（MCP）/ CLI 让家里机器结束某个游戏进程  
> 平台约束：.NET Framework 4.8 + C# 7.3（不可用 record / switch 表达式 / 可空引用）  
> 关联文档：[IPC-CAPABILITY-EXPOSURE-DESIGN-20260926.md](IPC-CAPABILITY-EXPOSURE-DESIGN-20260926.md) §4/§6/§9.5/§11、[SERVICE-CONTROL-PLAN-20260928.md](SERVICE-CONTROL-PLAN-20260928.md)、[DESIGN.md](DESIGN.md)、[AI-AGENT-MANUAL.md](AI-AGENT-MANUAL.md)

---

## 0. 结论摘要

1. 新增模块 **Processes**（`ModuleId = "Processes"`），暴露两个能力：`processes.list`（只读）、`processes.kill`（Privileged）。
2. **不需要提权**。实测证明：同一用户账户下，普通权限（Medium 完整性）进程即可结束管理员权限启动的进程（§2）。因此 `app.manifest` 保持 `asInvoker` 不变，不引入 UAC、不引入 `CarroDesk.Agent` 特权代理服务。
3. **必须绕开 `.NET Process.Kill()` / PowerShell `Stop-Process` / `taskkill`**：本机对同一个 PID 的对照实测中，这三条路都报「拒绝访问」，而 `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess` 成功（§2.3）。可用实现路径有两条——Win32 P/Invoke（§6.1）或 WMI/CIM（§2.4，实测通过，且 `System.Management` 已是本工程既有引用）。这是本方案可行性的关键，不是风格偏好。
4. 安全边界三层：**配置白名单**（参数枚举，内核校验）→ **口令**（PinGuard，fail-safe 默认需要）→ **审计 / 限流**（复用既有内核）。真正的内核级兜底是"**只能动当前用户账户的进程**"——跨账户 / SYSTEM / PPL 一律拿不到句柄。
5. 破坏性动作，**默认关闭**：白名单为空时一律拒绝；不提供任意进程名、不提供任意 pid 绕过。

---

## 1. 需求与使用形态

| 步骤 | 能力               | 说明                                     |
| -- | ---------------- | -------------------------------------- |
| 1  | `processes.list` | 看白名单里的进程现在跑没跑、几个实例、是否提权（远程"还在挂机吗"是刚需）  |
| 2  | `processes.kill` | 结束该进程名的实例；带口令（远程内联）                    |
| 3  | （复查）             | 再次 `processes.list`，确认是否被杀掉 / 是否被看门狗拉起 |

CLI 形态（沿用既有通用通道，无需新增 CLI 代码）：

```cmd
CarroDesk.Cli ctl processes.list --json
CarroDesk.Cli ctl processes.kill --name eldenring.exe --pin 1234
```

MCP 形态：能力表自动下发，无需改动 MCP 适配器。

---

## 2. 技术前提（本机实测，2026-10-07）

### 2.1 权限矩阵（调用方 = 普通权限 Medium 完整性；道具：自写 ctypes 探针 + `OpenProcess` 逐权限位探测）

| 目标进程                               | PROCESS_TERMINATE | SYNCHRONIZE | QUERY_LIMITED | VM_READ / VM_WRITE / CREATE_THREAD / QUERY_INFORMATION / SET_INFORMATION / READ_CONTROL / WRITE_DAC / ALL_ACCESS |
| ---------------------------------- | ----------------- | ----------- | ------------- | ---------------------------------------------------------------------------------------------------------------- |
| 同账户 Medium（普通程序）                   | 允许                | 允许          | 允许            | 允许                                                                                                               |
| **同账户 High（管理员启动的程序、UIAccess 组件）** | **允许**            | **允许**      | **允许**        | **全部 ERROR_ACCESS_DENIED(5)**                                                                                    |
| 其它账户 / SYSTEM（实测 147 个进程）          | 拒绝(5)             | 拒绝          | 拒绝            | 拒绝                                                                                                               |

官方依据：MS Learn《How the Integrity Mechanism Is Implemented in Windows Vista》——低完整性进程对高完整性进程仍保留 **generic execute**，其中包含 `SYNCHRONIZE`、`PROCESS_QUERY_LIMITED_INFORMATION`、`PROCESS_TERMINATE`；被 `NO_WRITE_UP` 拦下的是 `PROCESS_CREATE_THREAD`、`PROCESS_VM_OPERATION`、`PROCESS_VM_WRITE`、`PROCESS_SET_*` 等写权限，读权限也被拦。实测结果与该文档逐项吻合。

### 2.2 结论

- 结束**同一账户**下的提权游戏进程：**普通权限即可**，零提权、零 UAC、零服务代理。
- 结束**其它账户 / SYSTEM / 服务**进程：单靠本方案做不到，需要走 `docs/remote-admin/` 的 SSH/WinRM 通道（人打字执行）。本方案明确不覆盖。

### 2.3 同一个 PID 的对照实测（决定实现方式）

对 `C:\Home\Tools\ClickClient\ClickClient.exe`（PID 29308，High 完整性，同账户）：

| 调用方式                                                  | 结果                                                           |
| ----------------------------------------------------- | ------------------------------------------------------------ |
| PowerShell `Stop-Process -Name clickclient`           | **失败**：`Cannot stop process "ClickClient (29308)" ... 拒绝访问。` |
| `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess` | **成功**，`TerminateProcess` 返回 1，进程随即从列表消失                     |

`TerminateProcess` 的权限前提只有句柄上的 `PROCESS_TERMINATE`，内核只做一次引用校验、不再查 ACL/完整性，所以"拿到这个句柄 = 一定能结束"。

**根因已按位定位（2026-10-07 补测，`temp/verify_kill_bits.py`）**：用自建靶子逐级补权限位做阶梯实验——

- `taskkill /F` 在 `{TERMINATE, QUERY_LIMITED, SYNCHRONIZE}` 下失败；**补上 `PROCESS_QUERY_INFORMATION` 后立即成功** → 它缺的正是被 MIC 拒绝的那一位；
- PowerShell `Stop-Process` 补上 `PROCESS_QUERY_INFORMATION` 后**仍然失败** → 它请求的集合更大（还缺至少一位）；
- `OpenProcess(PROCESS_TERMINATE)` 只请求这一位，所以成功。

**靶子等价性已逐位证实**（`temp/probe_bits.py`）：真实 High 完整性进程对普通权限调用方开放的**恰好**是 `{TERMINATE, QUERY_LIMITED, SYNCHRONIZE}` 三项，其余 18 项（`DELETE` / `READ_CONTROL` / `WRITE_DAC` / `SUSPEND_RESUME` / `VM_READ` / `GENERIC_*` / `ACCESS_SYSTEM_SECURITY` 等）全部拒绝。因此 §2.4 的靶子结论可 1:1 迁移到真实提权目标。

### 2.4 各种结束方式可用性实测（2026-10-07）

方法：自建靶子进程（`ping.exe` 副本），用**精确 DACL** 复现"提权目标对普通权限调用方开放的权限集合"= `{PROCESS_TERMINATE, SYNCHRONIZE, PROCESS_QUERY_LIMITED_INFORMATION}`，其余一律不给。

**等价性论证**：该画像与真实提权目标对普通权限调用方开放的集合**完全相同**（§2.1 实测），因此「方法 X 在靶子上成功 ⇔ X 在真实提权目标上成功」。模型可信度由对照项验证：`Stop-Process` 在靶子上复现了与真实 ClickClient 完全一致的失败。

| 结束方式                                                                                                | 结果                    | 说明                                                |
| --------------------------------------------------------------------------------------------------- | --------------------- | ------------------------------------------------- |
| PS 5.1 `Stop-Process -Name`                                                                         | ❌ 拒绝访问                | 对照项，复现线上现象                                        |
| PS 7 `Stop-Process -Name`                                                                           | ❌ 拒绝访问                | 对照项，复现线上现象                                        |
| `taskkill /F /PID`                                                                                  | ❌ 拒绝访问                | **常被误当作可行方案**；缺 `PROCESS_QUERY_INFORMATION`（§2.3） |
| `taskkill /F /IM`                                                                                   | ❌ 拒绝访问                | 同上，同一根因                                           |
| `wmic process where processid=<PID> delete`                                                         | ✅ 成功                  | 老命令，Win11 已弃用/移除                                  |
| `Get-CimInstance Win32_Process -Filter "ProcessId=<PID>" \| Invoke-CimMethod -MethodName Terminate` | ✅ 成功（`ReturnValue=0`） | **推荐写法**，PS 5.1/7 通用                              |
| 同上，`-Filter "Name='xxx.exe'"`                                                                       | ✅ 成功（`ReturnValue=0`） | 按名字结束全部实例                                         |
| PS 5.1 `(Get-WmiObject Win32_Process ...).Terminate()`                                              | ✅ 成功（`ReturnValue=0`） | pwsh 7 已移除 `Get-WmiObject`                        |
| `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess`                                               | ✅ 成功                  | 自带 helper 时的基线方案                                  |

**机制说明**：WMI/CIM 路径的终止动作由 **WMI 提供程序（服务身份）**&#x4EE3;为执行，因此不受调用方令牌完整性级别的限制。  
⚠️ 由此推论（**未实测**）：这条路径可能能结束一部分"用户自身权限杀不掉"的进程，规划时不要把"只能动同账户进程"当作它的边界。

**⚠️ 环境实测警示（2026-10-07 16:04，用户反馈）**：上表中 **WMI/CIM 那条路的"成功"是人工放行后的结果**——火绒（Huorong）HIPS 拦截了 WMI 终止进程的行为，用户手动放行后才返回 `ReturnValue=0`。因此：

- WMI/CIM 路径在**无人值守的远程场景下不可靠**：没人点"放行"，调用会被拦截；
- **反向证据**：本方案所有走**原始 Win32 API**（`OpenProcess` + `TerminateProcess`）的实测，**均未触发任何杀软拦截**；
- 结论：实现优先级调整为 **① 原始 Win32 API（P/Invoke 或独立小 exe）为主，② WMI/CIM 仅作备选**（启用前须确认杀软放行）。
- 附带影响：WMI 终止进程是恶意软件的常用手法，杀软默认就拦；这类"借系统服务身份执行"的捷径在安全软件面前天然脆弱。

**对本方案的影响**：适配器首选 P/Invoke（§6.1 ①）；`System.Management` 方案（`src/CarroDesk.csproj` 已引用）作为备选保留，且需先验证杀软放行。

---

## 3. 能力设计

### 3.1 清单

| 能力名              | 风险级        | 口令                                       | 超时      | 说明                                   |
| ---------------- | ---------- | ---------------------------------------- | ------- | ------------------------------------ |
| `processes.list` | ReadOnly   | 否                                        | 默认 5s   | 白名单内进程名的运行实例：pid、镜像路径、完整性级别、会话、是否可结束 |
| `processes.kill` | Privileged | **按条目 `requiresPin`（缺省 true，fail-safe）** | 15000ms | 结束白名单内指定进程名的实例（默认全部实例）               |

命名与既有惯例一致（`services.*` / `tasks.*` / `awake.*`）。风险级取 `Privileged` 的理由：它不需要管理员权限，但**副作用重**（会丢未保存数据），按 IPC §11.6 应落到"需口令 + 全量审计"的一档，与 `services.*` 同档。

### 3.2 参数

```
processes.list
  name      string   可选   AllowedValues = 白名单（不填 = 列出白名单全部项）

processes.kill
  name      string   必填   AllowedValues = 白名单
  pid       int      可选   仅结束指定实例（见 §8 待确认项 2；实现时必须校验该 pid 的进程名在白名单内）
```

`name` 的 `Description` 动态拼接 `进程名=描述` 映射表（照 `services.*` 的做法），供 AI 把自然语言（"那个挂机的游戏"）映射到 exe；`AllowedValues` 用了**紧凑数组**参数，注意现有序列化路径对短数组的处理（实现时按 `services.*` 现状核对）。

### 3.3 返回值

```jsonc
// processes.list
{ "allowlist": ["eldenring.exe"],
  "processes": [ { "name": "eldenring.exe", "desc": "艾尔登法环（挂机那个）", "requiresPin": true,
                   "running": true,
                   "instances": [ { "pid": 1234, "path": "D:\\Games\\...\\eldenring.exe",
                                    "integrity": "High", "session": 1, "killable": true } ] } ] }

// processes.kill
{ "name": "eldenring.exe", "killed": [1234], "failed": [], "remaining": 0,
  "note": null }   // 若被看门狗拉起：remaining > 0 且 note 给出提示
```

`kill` 的语义（可靠性的核心）：

- **结束后必须复查**（再枚举同名进程）；有剩余则进 `failed` 或体现在 `remaining`，并给出"可能被看门狗重新拉起 / 受保护"的提示；
- **不自动重试**：与看门狗互殴只会陷入循环，明确把事实回报给调用方；
- **幂等**：目标本来就没在跑 → 成功返回，`running=false` 语义（`killed` 为空），不算错误；
- 进程在结束过程中自然退出 → 视为成功。

### 3.4 配置（`config.json`，节名 = 模块 Id `Processes`）

```jsonc
"Processes": {
  "AllowedProcesses": [
    { "name": "eldenring.exe", "desc": "艾尔登法环（挂机那个）", "requiresPin": true },
    "ClickClient",                                  // 旧字符串写法：desc 空、requiresPin=true
    { "name": "potplayer64", "desc": "PotPlayer", "requiresPin": false }
  ]
}
```

- 条目 schema 与 `Services.AllowedServices` 完全同构：字符串或对象两种写法都收，`pin` 为 `requiresPin` 的别名，缺省 true。
- 进程名比较**忽略大小写**、**可带可省略 `.exe`**（与 `ScreenLock.ExcludeProcesses`、`Awake.AutoAwakeProcesses` 的既有约定一致）。
- 白名单为空 = 能力对所有名字一律拒绝（fail-safe），与 `services.*` 行为一致。

---

## 4. 安全模型

### 4.1 三层闸门（全部复用既有机制，不新造）

| 层       | 位置                                                        | 作用                                        |
| ------- | --------------------------------------------------------- | ----------------------------------------- |
| 白名单     | `CommandHost` 参数校验（`AllowedValues` + 处理函数内二次 `FindEntry`） | 非白名单名字一律 `-32602`                         |
| 口令      | `PinGuard` + `RequiresPinFor` 动态判定                        | 缺/错口令 `-32002`，含失败限流（连错 5 次封锁）            |
| 审计 / 限流 | `ICommandAuditSink`（参数只记摘要）+ 内核限流                         | 成功与失败都落盘；模块另经 `LogInfo` 记录"已结束 xxx (pid)" |

### 4.2 内核级兜底（诚实说明与 §11.2 的差异）

`services.*` 的内核兜底是"**服务对象 DACL**"——可以一次性授权到具体对象上。**进程不行**：进程对象的安全描述符由创建者的令牌默认 DACL 决定，每个实例各不相同，没有"预授权"的位置。所以进程控制的真实边界是：

> **内核天然只允许动「当前登录用户账户」的进程。** 跨账户 / SYSTEM 服务 / PPL 保护进程，连 `PROCESS_TERMINATE` 句柄都拿不到，与白名单怎么写无关。

这既是限制也是好处：即使远程通道被攻破、白名单被写进 `svchost.exe`，攻击面仍止步于"该用户本来就能做的破坏"——**本方案不放大本机已登录用户的权限**（他能杀的游戏，等于本机任何人都能杀）。真正的风险因此集中在**远程通道 + 口令强度**，与 IPC §11.6 的结论一致。

### 4.3 破坏性与防呆

- 进程控制是**破坏性**动作（可能丢未保存数据）→ 风险级 `Privileged`、默认要求口令、默认白名单为空。
- 硬性防呆（**不是配置项，写死在代码里**）：
  1. **拒绝结束 CarroDesk 自身**（当前进程、宿主 `CarroDesk.exe`、CLI 进程）——避免远程把通道自己掐掉导致失联；
  2. **内置一组危险同名进程黑名单**（`system` 类同名进程），命中即拒绝并说明原因，防止把 per-user 的 `svchost.exe`、`explorer.exe` 之类配进白名单后误杀。
- 不做优雅关闭（`WM_CLOSE`）：跨完整性级别的窗口消息受 UIPI 限制（实测 `WM_NULL` 这类被放行的例外能过，其余默认不放行），对提权目标不可靠。第一阶段**只做强制结束**。

### 4.4 明确不做

`shell.exec` 式任意命令、任意进程名、任意 pid 绕过白名单、结束跨账户进程、`host.exit`。

---

## 5. 文件清单

### 新增（6 个文件）

| 路径                                                                    | 内容                                                                                            |
| --------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| `src/Modules/ProcessControl/ProcessControlModule.cs`                  | 模块本体：`ModuleBase<ProcessControlConfig>, ICommandProvider`，两个能力描述符、口令策略、错误翻译                   |
| `src/Modules/ProcessControl/Models/ProcessControlConfig.cs`           | `ProcessAllowlistEntry` + `ProcessAllowlistEntryConverter`（字符串/对象双写法）+ `ProcessControlConfig` |
| `src/Modules/ProcessControl/Services/IProcessControlAdapter.cs`       | 抽象：枚举实例 / 结束实例（生产走 P/Invoke，单测注入假实现）                                                          |
| `src/Modules/ProcessControl/Services/WindowsProcessControlAdapter.cs` | 生产实现：`Process.GetProcessesByName` 枚举 + `OpenProcess`/`TerminateProcess` 结束 + 完整性级别读取          |
| `tests/CarroDesk.Tests/ProcessControlModuleTests.cs`                  | 单测（见 §7）                                                                                      |
| `docs/PROCESS-CONTROL-PLAN-20261007.md`                               | 本文档                                                                                           |

### 修改（7 处）

| 路径                                             | 改动                                                                       | 风险 |
| ---------------------------------------------- | ------------------------------------------------------------------------ | -- |
| `src/Host/Modules/ModuleRegistry.cs`           | `+ modules.RegisterModule(new ProcessControlModule());`（1 行）             | 低  |
| `src/Assets/Locales/zh-CN.json`                | `Tray` 节新增 `ProcessesTitle` / `ProcessesDesc`                            | 低  |
| `src/Assets/Locales/en-US.json`                | 同上（英文）                                                                   | 低  |
| `src/Samples/config.sample.json`               | 新增 `Processes` 示例节                                                       | 低  |
| `docs/DESIGN.md`                               | 模块表 +1 行；如有能力清单表同步                                                       | 低  |
| `docs/USAGE.md`                                | 新增 `Processes` 配置与用法（对齐 `Services` 写法）                                   | 低  |
| `docs/AI-AGENT-MANUAL.md`                      | 能力表 +2 行、示例 1 条（该文件构建时嵌入主程序，经 `host.guide` 下发）                           | 低  |
| `tests/CarroDesk.Tests/CarroDesk.Tests.csproj` | `<Compile Include="ProcessControlModuleTests.cs" />`（**逐文件登记，漏了会静默不编译**） | 低  |

### 明确不改

- `src/app.manifest`：**不加** `requireAdministrator`（IPC §11.1 的红线）；
- `src/Models/AppSettings.cs`：模块配置走 `IConfigManager.GetModuleConfig<T>(Id)`，无需宿主级新字段；
- CLI / MCP 适配器、`CommandHost`、`CommandRegistry`：能力经注册表自动暴露，零改动。

---

## 6. 实现要点（可靠性关键）


1. **结束进程二选一，都绕开 `Process.Kill()` / `Stop-Process` / `taskkill`**（理由见 §2.3、§2.4）：  
   ① **原始 Win32 API** `OpenProcess(PROCESS_TERMINATE)` → `TerminateProcess` → `CloseHandle`（`try/finally` 保证句柄释放）——**首选**：不依赖 WMI 服务，实测不触发杀软 HIPS；  
   ② **WMI/CIM**（`Get-CimInstance ... Invoke-CimMethod Terminate`）——代码最短，但**本机被火绒拦截、需人工放行**，只作备选（见 §2.4 警示）。  
   ①② 的先后即优先级：在装有 HIPS 杀软（如火绒）的机器上，① 是唯一无人值守可用的路径。
2. **枚举用托管 API**：`Process.GetProcessesByName(name)`（名字不含 `.exe`），逐实例读取 pid / 镜像路径 / 会话，异常逐项隔离（照 `ServiceControl` 的 `SafeDescribeStatus` 风格）。
3. **完整性级别读取**：`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `OpenProcessToken(TOKEN_QUERY)` + `GetTokenInformation(TokenIntegrityLevel)`，映射 Low/Medium/High/System；取不到记 `unknown`（受保护进程取不到，属正常），**只用于展示，不参与判定**。
4. **错误映射**（照 `services.*` 的粒度）：
   - `ERROR_ACCESS_DENIED(5)` → `Internal`，文案指明"目标可能属于其它账户 / 受保护进程 / 被反作弊驱动拦截"；
   - 进程不存在 → `InvalidParams`；
   - 超时 → `Timeout`（`-32004`）。
5. **超时与取消**：`CancellableHandler` + 200ms 短轮询复查退出（照 `WindowsServiceControlAdapter.WaitForStatus` 的写法），让内核超时与调用方取消都能立刻生效。
6. **无新依赖**：P/Invoke 手写；`System.Diagnostics.Process` 在 net48 开箱可用。
7. **C# 7.3 / net48** 语法约束：不用 record、switch 表达式、可空引用类型。
8. **模块无托盘 UI**：照 `ServiceControlModule`（`OnStart`/`OnStop` 空实现，`Order` 取 910，紧随 services）。

---

## 7. 实施步骤与验收

| 步  | 内容                                                              | 验收                                          |
| -- | --------------------------------------------------------------- | ------------------------------------------- |
| S1 | 配置模型 + 适配器接口/实现                                                 | 单测：字符串/对象双写法解析、往返序列化、去重                     |
| S2 | 模块与两个能力（口令策略、错误翻译、防呆黑名单）                                        | 单测：白名单外拒绝、缺口令 `-32002`、访问被拒映射、幂等、复查逻辑       |
| S3 | 接线（注册、i18n、样例配置）                                                | 构建通过；宿主启动后 `host.capabilities.list` 出现两个新能力 |
| S4 | 单测 + 登记 csproj，跑全量回归                                            | 全量测试全绿；构建 0 错误 0 警告                         |
| S5 | 文档（DESIGN / USAGE / AI-AGENT-MANUAL / CHANGES-20261007）         | 审查可读性                                       |
| S6 | **实机验收**（本机靶子：`C:\Home\Tools\ClickClient`，非提权启动 → 再以管理员启动，两种都测） | 见下                                          |

S6 实机验收命令与预期：

```cmd
:: 1) 只读：应能看到实例、完整性级别
CarroDesk.Cli ctl processes.list
:: 2) 结束：应成功（无论目标是否以管理员启动）
CarroDesk.Cli ctl processes.kill --name clickclient --pin <PIN>
:: 3) 负例
CarroDesk.Cli ctl processes.kill --name notepad            :: -32602 不在白名单
CarroDesk.Cli ctl processes.kill --name clickclient        :: -32002 缺口令
:: 4) 边界：白名单里放一个 SYSTEM 进程名 → 明确报"拒绝访问"，不是静默失败
```

---

## 8. 待确认项（评审时定）

| # | 问题                     | 我的建议                                                                                                 |
| - | ---------------------- | ---------------------------------------------------------------------------------------------------- |
| 1 | 是否要做 `processes.list`？ | **要**。远程"游戏还在不在跑"是刚需，且它只读、零风险                                                                        |
| 2 | 是否提供 `pid` 参数精确结束单个实例？ | **提供**，但必须校验该 pid 的进程名在白名单内，否则等于绕过白名单                                                                |
| 3 | 口令策略                   | 全部默认 `true`；只对"杀了也无所谓"的进程（如 PotPlayer）显式设 `false`                                                    |
| 4 | 第一阶段是否展示完整性级别 / 是否提权？  | **展示**。多约 60 行 P/Invoke，换来 AI 能判断"这个进程需不需要特殊处理"                                                      |
| 5 | 白名单条目是否需要 `desc`？      | **要**。AI 把"那个挂机的游戏"映射到 exe 靠的就是它（`services.*` 已验证这条路）                                                |
| 6 | 危险同名进程黑名单是否内置？         | **内置**（`svchost`、`explorer`、`lsass`、`winlogon`、`services`、`wininit`、`csrss`、`smss`、`System` 等），写死不配置 |

---

## 9. 可选后续（不在本次范围）

1. ~~**DACL 阶梯实验**：精确定位 `.NET Process.Kill()` 请求了哪个被 MIC 拒绝的权限位。~~ **已完成**（`temp/verify_kill_bits.py`）：`taskkill` 缺 `PROCESS_QUERY_INFORMATION`，补上即成功；`Stop-Process` 缺口更大（补 `PROCESS_QUERY_INFORMATION` 仍失败，未继续追到底）。
2. **看门狗对抗**：若确认某游戏会被反作弊/看门狗拉起，再讨论"结束父进程树 / 短时循环压制"；风险高，需单独评估。
3. **跨账户进程**：走 `docs/remote-admin/` 通道（SSH/WinRM/Agent，Tailscale 内，人打字）。
4. **进程名模糊匹配**：白名单支持通配符（如 `*game*`）。会削弱白名单的确定性，暂不做。
