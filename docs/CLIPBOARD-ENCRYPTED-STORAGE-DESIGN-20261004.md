# 剪贴板历史可选加密存储设计（DPAPI）

> 日期：2026-10-04
> 状态：已实施（2026-10-04 18:35，含 6 个新增单测）
> 需求：剪贴板历史支持可选加密存储。目标是无 PIN 依赖（DPAPI），磁盘上永远不明文存放历史数据；内存不受约束。

## 1. 背景与目标

剪贴板历史当前以明文 JSON 全量快照持久化在 `data/ClipboardHistory/history.json`（`JsonClipboardHistoryStorage`：后台合并写线程 + `AtomicFile` 原子替换）。本设计为该文件增加**可选加密**：

- 用户在设置中开启后，磁盘上的历史文件变为密文（DPAPI，绑定本机当前 Windows 用户）；
- 取消后自动转回明文；
- 启动/运行全程无感，无需输入任何凭据；
- 加密状态切换过程中崩溃不丢数据。

## 2. 方案选型记录

### 2.1 候选方案对比

| 方案 | 结论 |
|---|---|
| PIN + PBKDF2 + AES-256-CBC/HMAC（EtM） | 可行，但需要启动解锁窗、KDF、locked 态防覆盖等一整套交互与状态；忘记 PIN 即丢历史 |
| **DPAPI（`ProtectedData`，CurrentUser）** | **采纳**。零交互、零凭据记忆、代码量约为 PIN 方案一半；net48 自带（`System.Security.dll` 框架引用），零 NuGet 依赖 |
| DPAPI + PIN 双模式 | 暂不做。文件格式 magic 已为将来扩展留位，需要时可再加 PIN 实现 |
| PIN + 本地缓存密钥（混合） | 否决。密钥一旦落盘（DPAPI 保护）等价于纯 DPAPI，PIN 沦为摆设 |
| 延迟解锁（首次使用才弹 PIN） | 否决。自动记录在解锁前处于半工作状态，体验更差 |
| 字段级/逐条加密 | 否决。全量快照模型下无收益，复杂度数倍 |

### 2.2 DPAPI 与 PIN 的威胁边界（写入 UI 提示文案）

- **能防**：他人直接拷走 `history.json`（在别的机器/别的 Windows 用户下无法解密）；文件同步网盘/扫描工具读到明文。
- **不能防**：本机同一 Windows 用户下的其他进程（它们同样能调用 DPAPI Unprotect）。
- **依赖**：Windows 登录账户密码强度（空密码/弱密码时保护变弱）。
- **数据风险**：管理员"重置"Windows 密码（非用户正常改密）会导致 DPAPI masterkey 丢失，加密历史无法恢复；数据绑定本机，不能跨机迁移。
- **擦除边界**：明文→密文的切换是原子替换，无法保证旧明文从磁盘扇区物理擦除（卷影副本等不受控），不承诺取证级擦除。

### 2.3 应用级 entropy

`ProtectedData.Protect/Unprotect` 附带一个编译进程序集的常量 `appEntropy`，作用仅是让密文不能被其他恰好调用 DPAPI 的程序按默认方式解开；它不是安全边界（同用户恶意进程可读源码/内存取到 entropy）。成本为零，仍保留。

## 3. 文件格式

保持现有路径与文件名 `data/ClipboardHistory/history.json` 不变，内容变为两种格式之一，**靠文件头自识别，不信任配置状态**：

```
明文模式：UTF-8 JSON（现状不变）
密文模式：Base64( magic("CDCE1\0\0\0") | DPAPI密文 )
```

- `Load` 读入文本 → 尝试 `Convert.FromBase64String` 且前 8 字节等于 magic → 走解密；否则按明文 JSON 走原路径。明文 JSON 以 `[` 开头，不可能是合法 Base64，两个方向都不会误判。
- 配置丢失、被手改（`EncryptStorage` 与磁盘实际不符）时能自愈：Load 按实际格式读取，Save 按当前开关写，下次落盘即恢复一致。
- Base64 文本封装使文件保持"文本文件"属性：复用 `AtomicFile.WriteAllText` 一行不改，编辑器打开不乱码（体积 +33%，可忽略）。

## 4. 存储层改动

`JsonClipboardHistoryStorage` 构造函数新增可选参数 `ClipboardStorageCrypto crypto = null`，现有调用与测试（不传参）行为完全不变：

- `Save`：明文 JSON 序列化后，`crypto != null && crypto.Enabled` 时经 `crypto.Encrypt` 写密文，否则写明文。
- `Load`：按第 3 节自识别；识别为密文但**无法解密**（无 crypto 实例，或 `Unprotect` 抛异常——典型为 masterkey 丢失/换用户运行）时：
  1. 原文件改名留档：`history.json → history.json.undecryptable-<yyyyMMddHHmmss>`（改名而非复制，腾出干净路径）；
  2. 返回空列表，应用以空历史正常启动；
  3. 触发 `DecryptionFailed` 事件（参数为留档路径），模块层弹通知告知用户。
  4. 不走 `.corrupt-*` 备份逻辑（文件没有损坏），也不重试（重试无意义）。
- 解密成功但 JSON 反序列化失败（理论不可能，防 bug）：仍走现有 `.corrupt-*` 备份。
- 后台合并写线程、`Flush`、`ioLock`、防抖逻辑全部复用，零改动。

## 5. 配置与模块接线

- `ClipboardHistoryConfig` 新增 `EncryptStorage`（`bool`，默认 false）。配置文件明文记录"是否加密"这一事实本身不暴露内容；**密文内容的解密不依赖该配置**（自识别）。
- `ClipboardHistoryModule.Initialize`：创建 `ClipboardStorageCrypto` 并按 `Config.EncryptStorage` 设置 `Enabled`，注入 storage；订阅 `DecryptionFailed`，经 Dispatcher 弹通知。
- `OnConfigReloaded`：同步 `crypto.Enabled = Config.EncryptStorage`。
- 新增 `public bool EnableStorageEncryption(bool enable)`：设置窗口触发转换用。流程：`crypto.Enabled = enable` → `_service.GetItems()` → `_storage.Save(items)` → `Flush()`（确定性落盘）。磁盘写失败时回滚 `Enabled` 并返回 false。成功后窗口再保存模块配置；若配置保存失败，窗口回滚调用 `EnableStorageEncryption(!enable)` 恢复磁盘格式。

## 6. 设置 UI

设置窗口新增"加密历史存储（绑定本机当前 Windows 用户）"复选框：

- **开启**：弹确认框（说明 DPAPI 语义与本节开头威胁边界，重点提示"重置 Windows 密码将无法恢复历史"）→ `EnableStorageEncryption(true)` → 保存配置。
- **取消**：`EnableStorageEncryption(false)` → 保存配置。无需任何输入。
- 失败路径：存储转换失败 → 提示并中断保存；配置保存失败 → 回滚存储转换并提示。
- 崩溃安全性：`AtomicFile` 原子替换 + 格式自识别保证任意时刻磁盘上只有一份完整有效文件。

## 7. 测试计划

扩展现有 `tests/CarroDesk.Tests/ClipboardStorageTests.cs`（MSTest）：

1. DPAPI 往返一致性（加密写入 → Load 解密 → 数据逐条等价）；
2. 密文文件在 `crypto == null` 或解密失败时的留档行为（原文件改名 `.undecryptable-*`、返回空列表、触发事件）；
3. magic 自识别四象限：配置与文件状态任意组合（明文文件+Enabled、密文文件+Disabled 等）下 Load 正确、后续 Save 按开关落盘；
4. 明文↔密文多次切换数据不丢；
5. `crypto == null` 时全部既有用例保持绿。

DPAPI 依赖当前用户 profile，单元测试以登录用户身份运行，可直接执行。

## 8. 不做清单

- PIN / 双模式（格式 magic 已留 `CDCE1` 之外的空间，将来可加 `CDCP1` 等）；
- 字段级加密、新第三方依赖（BouncyCastle 等）、密钥本地缓存；
- 取证级磁盘擦除、反内存转储。

## 9. 实施文件清单

| 文件 | 动作 |
|---|---|
| `src/CarroDesk.csproj` | 添加 `<Reference Include="System.Security" />` |
| `src/Modules/ClipboardHistory/Services/ClipboardStorageCrypto.cs` | 新建：加密/解密 + magic 封装 |
| `src/Modules/ClipboardHistory/Services/JsonClipboardHistoryStorage.cs` | 改造：crypto 参数、自识别、undecryptable 留档、事件 |
| `src/Modules/ClipboardHistory/Models/ClipboardHistoryConfig.cs` | 新增 `EncryptStorage` |
| `src/Modules/ClipboardHistory/ClipboardHistoryModule.cs` | 接线：crypto 创建/同步、事件通知、`EnableStorageEncryption` |
| `src/Modules/ClipboardHistory/Views/ClipboardHistorySettingsWindow.xaml(.cs)` | 复选框 + 确认框 + 转换/回滚 |
| `src/Assets/Locales/zh-CN.json`、`en-US.json` | 新词条 |
| `tests/CarroDesk.Tests/ClipboardStorageTests.cs` | 新用例 |
