# ScreenLock 全面改进与优化实施计划

> 创建时间：2026-09-16  
> 目标版本：v0.2  
> 适用平台：Windows 10 / 11 (.NET Framework 4.8 + WPF)

---

## 1. 背景与核心问题诊断

经过对项目代码全面审查与用户实际使用反馈走查，定位到以下核心问题：

1. **Windows 锁定与解锁冲突（定时器与会话状态 Bug）**：
   - 用户按下 Win+L 锁定或系统锁屏后，已完成 Windows 官方安全身份认证；但在解锁 Windows 返回桌面后，ScreenLock 仍经常处于锁定状态或在数秒后再次触发锁屏，导致用户“刚解锁完又得解锁一遍”。
   - 根因：IdleDetector 在会话锁定时挂起，但内部保留了陈旧的 _lastRaw 和未清空的 _effectiveMs；解锁瞬间若未被判定为新输入，旧累计毫秒数立即加满越界；且 ScreenLock 在 SessionUnlock 时没有自动联动解锁机制。
2. **Alt + F4 拦截失效（严重安全漏洞）**：
   - KeyboardBlocker.cs 将 VK_F4 常量写为 0x70（实际为 VK_F1，VK_F4 为 0x73），锁屏界面直接按 Alt+F4 即可绕过 PIN 将锁屏窗口关闭。
3. **DailyTrigger 启动/重载时误触发已过去的任务**：
   - _lastFiredDate 初始为 DateTime.MinValue，当天设定时间若早于软件启动或重载时刻，会被立即当作“错过的任务”补跑。
4. **LockWindow 锁屏 UI/UX 质感单调、交互反馈粗糙**：
   - 缺乏现代时间日期排版（如大字时间+星期格式）；
   - 缺少小键盘（NumLock）及大写锁定（CapsLock）状态提示，容易导致输入 PIN 误操作；
   - 缺少淡入淡出动画，进入与退出界面生硬；
   - 密码输错缺乏震颤提示动效。
5. **任务与配置编辑器体验门槛高**：
   - 排除进程需用户手打进程名，容易拼错；
   - 任务编辑器缺乏“测试运行”与即时日志预览，脚本调试成本高；
   - Cron 表达式缺乏自然语言翻译与下次执行时间预览；
   - 模板变量（{{date}} 等）全靠记忆，缺少可视化插入工具。

---

## 2. 详细实施路线图

### 阶段 1：核心 Bug 修复与 Windows 会话联动（P0 紧急性最高）

- **1.1 Services/IdleDetector.cs**：
  1. 彻底重构 Reset()，清空 _effectiveMs = 0 并更新 _lastRaw = GetIdleMilliseconds()；
  2. 增加近期输入判定保底：若 raw < 2000ms，无条件认定有活动输入并同步 _effectiveMs = raw；
  3. 修复 Environment.TickCount 回绕与边界比较问题。
- **1.2 App.xaml.cs & Models/AppSettings.cs**：
  1. 新增配置项 UnlockOnResume（默认 true）；
  2. 监听 SessionUnlock 时，若 UnlockOnResume == true 且当前处于锁定，自动调用 Controller.Unlock()；
  3. 无论是否已锁，Windows 解锁时全面重置 Idle.Reset()。
- **1.3 Services/KeyboardBlocker.cs & Views/LockWindow.xaml.cs**：
  1. 修复 VK_F4 = 0x73；
  2. LockWindow 增加 Closing 防御拦截，非经 PIN 解锁或授权退出流程一律取消关闭；
  3. 严密按键白名单拦截。
- **1.4 Services/Tasks/Triggers/DailyTrigger.cs**：
  启动/重载初始化时，若当前时间已超过当天设定时间，将 _lastFiredDate 置为 DateTime.Today，禁止错跑。
- **1.5 App.xaml.cs & Views/ConfigEditorWindow.xaml.cs**：
  移除 App 私有方法的反射调用，改为 internal 方法直接调用。

### 阶段 2：锁屏界面 UI/UX 视觉与交互重塑（P1 核心体验）

- **2.1 Views/LockWindow.xaml (.cs)**：
  - 时间排版：大号无衬线字体 HH:mm + 次级优雅文本 M月d日 星期X。
  - 键盘状态指示：实时检测并提示 NumLock（小键盘关闭提示）与 CapsLock（大写锁定开启提示）。
  - 错误反馈：PIN 错误时增加左右抖动震颤动效（Shake Animation）与红色微光提示。
- **2.2 Views/LockWindow.xaml (.cs)**：
  - 平滑过渡：进入锁屏与解锁加入 200ms Opacity 淡入淡出动画。
  - 副屏体验统一：副屏居中显示优雅大时钟与日期，保持多屏视觉协同。
- **2.3 Views/LockWindow.xaml.cs**：
  - 监听 SystemEvents.DisplaySettingsChanged，分辨率或屏幕拓扑变化时自动重建遮罩；
  - 规范物理像素与 WPF 设备独立像素（DIPs）转换。

### 阶段 3：配置编辑器与任务编辑器生产力强化（P1 易用性）

- **3.1 Views/ConfigEditorWindow.xaml (.cs)**：
  1. 排除进程增加“从运行中选择...”弹窗列表，一键勾选当前运行的前台进程加入排除；
  2. 合并“保存”与“保存并应用”；
  3. 增加“恢复默认值”快捷操作。
- **3.2 Views/TaskEditorWindow.xaml (.cs)**：
  1. 任务列表增加状态指示（🟢 启用 / ⚪ 禁用）及触发器类型徽章；
  2. 增加“测试运行（Test Run）”按钮，无需关闭编辑器即可立即执行当前任务并在小面板显示返回码与日志；
  3. Cron 表达式增加自然语言翻译与下次运行时间预览；
  4. 参数输入框旁增加模板变量下拉快捷插入菜单（{{date}}、{{time}}、{{task}} 等）。

### 阶段 4：托盘菜单收敛与健壮性回归（P2 系统级打磨）

- **4.1 App.xaml.cs**：托盘右键菜单收敛分层，增加左键单击快捷交互。
- **4.2 回归测试**：完整回归测试 Windows 锁屏解锁、闲时锁屏、任务定时调度、多屏显示、配置持久化。