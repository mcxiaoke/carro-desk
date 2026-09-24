using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Modules.Awake.Models;
using CarroDesk.Services;

namespace CarroDesk.Modules.Awake.Services
{
    public class AwakeService : IDisposable
    {
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_USER_PRESENT = 0x00000004,
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;        // 0: Offline (battery), 1: Online (AC), 255: Unknown
            public byte BatteryFlag;         // 128: No system battery
            public byte BatteryLifePercent;  // 0 - 100, 255 = Unknown
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        private readonly Dispatcher _dispatcher;
        private readonly ILoggerService _logger;
        private readonly DispatcherTimer _timer;

        private AwakeConfig _config;
        private AwakeMode _mode = AwakeMode.Passive;
        private bool _keepDisplayOn = true;
        private DateTime _expireTime = DateTime.MinValue;
        private bool _isBatteryPaused = false;
        private bool _isProcessTriggered = false;
        private string _activeProcessTrigger = string.Empty;
        private int _processExitPendingSeconds = 0;
        private int _tickCount = 0;

        /// <summary>
        /// 用户在守护进程运行期间手动关闭保持唤醒后置位：
        /// 本次进程会话内不再自动联动，直到守护进程全部退出才解除（见 CheckProcessTriggers）。
        /// </summary>
        private bool _userSuppressedProcessLink = false;

        public AwakeMode Mode => _mode;
        public bool KeepDisplayOn => _keepDisplayOn;
        public DateTime ExpireTime => _expireTime;
        public bool IsActive => !_isBatteryPaused && (_mode != AwakeMode.Passive || _isProcessTriggered);
        public bool IsBatteryPaused => _isBatteryPaused;
        public bool IsProcessTriggered => _isProcessTriggered;
        public bool IsProcessExiting => _processExitPendingSeconds > 0;
        public int ProcessExitPendingSeconds => _processExitPendingSeconds;
        public string ActiveProcessTrigger => _activeProcessTrigger;

        /// <summary>智能进程联动是否启用（总开关 + 名单非空）</summary>
        public bool IsProcessLinkEnabled => _config == null || _config.ProcessLinkEnabled;

        /// <summary>名单中是否有可用的目标进程</summary>
        public bool HasProcessTargets =>
            _config?.AutoAwakeProcesses != null && _config.AutoAwakeProcesses.Count > 0;

        public TimeSpan RemainingTime
        {
            get
            {
                if ((_mode == AwakeMode.Timed || _mode == AwakeMode.UntilTime) && _expireTime > DateTime.Now)
                {
                    return _expireTime - DateTime.Now;
                }
                return TimeSpan.Zero;
            }
        }

        public event Action StateChanged;
        public event Action Expired;
        public event Action<bool, byte> BatteryStateChanged; // (isPaused, batteryPercent)
        public event Action<bool, string> ProcessTriggered;   // (isTriggered, procName)
        public event Action Tick;

        public AwakeService(Dispatcher dispatcher, ILoggerService logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;

            _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTimerTick;
        }

        public void Initialize(AwakeConfig config)
        {
            _config = config ?? AwakeConfig.CreateDefault();
            _mode = _config.Mode;
            _keepDisplayOn = _config.KeepDisplayOn;
        }

        public void UpdateConfig(AwakeConfig config)
        {
            _config = config ?? AwakeConfig.CreateDefault();
            _keepDisplayOn = _config.KeepDisplayOn;

            // 开关被关闭或目标名单已清空时，立即撤销正在进行的进程联动，避免状态残留
            if ((!IsProcessLinkEnabled || !HasProcessTargets) && _isProcessTriggered)
            {
                string oldProc = _activeProcessTrigger;
                _isProcessTriggered = false;
                _activeProcessTrigger = string.Empty;
                _processExitPendingSeconds = 0;
                _logger?.LogInfo("Awake", $"智能进程联动已关闭或名单已清空，撤销进程 '{oldProc}' 触发的保持唤醒");
                ProcessTriggered?.Invoke(false, oldProc);
            }
            else if (IsProcessLinkEnabled)
            {
                // 重新启用联动时清除用户抑制，允许立即检测
                _userSuppressedProcessLink = false;
            }

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        public void Start()
        {
            _timer.Start();
            ApplyExecutionState();
        }

        public void Stop()
        {
            _timer.Stop();
            SetPassive();
        }

        public void SetPassive()
        {
            _mode = AwakeMode.Passive;
            _expireTime = DateTime.MinValue;
            _isProcessTriggered = false;
            _activeProcessTrigger = string.Empty;
            _processExitPendingSeconds = 0;

            // 注意：这里不再重置 _isBatteryPaused——该标志归电池判定逻辑所有。

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 用户主动关闭保持唤醒（托盘/设置界面）。
        ///
        /// 与内部 <see cref="SetPassive"/> 的区别：若此时守护进程正在运行，
        /// 记下"本次进程会话内不再自动联动"，否则 5 秒后 CheckProcessTriggers 会把
        /// 用户刚刚的"关闭"直接反转回开启。该抑制在守护进程全部退出后自动解除。
        /// </summary>
        public void SetPassiveByUser()
        {
            _userSuppressedProcessLink = true;
            SetPassive();
        }

        /// <summary>显式复位用户手动关闭抑制（在设置保存、总开关开启时调用）</summary>
        public void ResetUserSuppression()
        {
            _userSuppressedProcessLink = false;
        }

        public void SetIndefinite()
        {
            _userSuppressedProcessLink = false;
            _mode = AwakeMode.Indefinite;
            _expireTime = DateTime.MinValue;
            _isProcessTriggered = false;
            _activeProcessTrigger = string.Empty;
            _processExitPendingSeconds = 0;

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        public void SetTimed(int minutes)
        {
            if (minutes <= 0)
            {
                SetPassive();
                return;
            }

            _userSuppressedProcessLink = false;
            _mode = AwakeMode.Timed;
            _expireTime = DateTime.Now.AddMinutes(minutes);
            _isProcessTriggered = false;
            _activeProcessTrigger = string.Empty;
            _processExitPendingSeconds = 0;

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        public void SetUntilTime(DateTime targetTime)
        {
            DateTime now = DateTime.Now;
            DateTime target = new DateTime(now.Year, now.Month, now.Day, targetTime.Hour, targetTime.Minute, 0);
            if (target <= now)
            {
                target = target.AddDays(1);
            }

            _userSuppressedProcessLink = false;
            _mode = AwakeMode.UntilTime;
            _expireTime = target;
            _isProcessTriggered = false;
            _activeProcessTrigger = string.Empty;
            _processExitPendingSeconds = 0;

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        public void ToggleKeepDisplayOn()
        {
            SetKeepDisplayOn(!_keepDisplayOn);
        }

        public void SetKeepDisplayOn(bool keepOn)
        {
            if (_keepDisplayOn == keepOn) return;
            _keepDisplayOn = keepOn;
            if (_config != null)
            {
                _config.KeepDisplayOn = keepOn;
            }

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        private void ApplyExecutionState()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(ApplyExecutionState));
                return;
            }

            try
            {
                if (!IsActive)
                {
                    SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                    _logger?.LogInfo("Awake", "已恢复系统默认电源策略 (ES_CONTINUOUS)");
                }
                else
                {
                    EXECUTION_STATE flags = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED;
                    if (_keepDisplayOn)
                    {
                        flags |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;
                    }
                    SetThreadExecutionState(flags);
                    _logger?.LogInfo("Awake", $"已应用保持唤醒状态: {flags} (DisplayOn={_keepDisplayOn})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Awake", "调用 SetThreadExecutionState 发生异常", ex);
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _tickCount++;

            // 1. 倒计时检查
            if (_mode == AwakeMode.Timed || _mode == AwakeMode.UntilTime)
            {
                if (DateTime.Now >= _expireTime)
                {
                    _logger?.LogInfo("Awake", "保持唤醒计时已到期，恢复默认电源状态");
                    SetPassive();
                    Expired?.Invoke();
                    return;
                }
            }

            // 进程退出缓冲倒计时（若处于缓冲期）
            if (_processExitPendingSeconds > 0)
            {
                _processExitPendingSeconds--;
                if (_processExitPendingSeconds <= 0)
                {
                    _logger?.LogInfo("Awake", "目标进程退出缓冲期已结束，恢复默认电源状态");
                    if (_isProcessTriggered)
                    {
                        string oldProc = _activeProcessTrigger;
                        _isProcessTriggered = false;
                        _activeProcessTrigger = string.Empty;
                        ApplyExecutionState();
                        ProcessTriggered?.Invoke(false, oldProc);
                        StateChanged?.Invoke();
                    }
                }
            }

            // 2. 电池状态轮询（每 3 秒检查一次）
            if (_config != null && _config.DisableOnBattery && _tickCount % 3 == 0)
            {
                CheckBatteryStatus();
            }

            // 3. 自动唤醒进程监测（每 5 秒检查一次；开关关闭时完全不检测）
            if (IsProcessLinkEnabled && HasProcessTargets && _tickCount % 5 == 0)
            {
                CheckProcessTriggers();
            }

            // 4. 定期刷新事件（用于 UI 倒计时更新）
            Tick?.Invoke();
        }

        private void CheckBatteryStatus()
        {
            try
            {
                if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                {
                    // BatteryFlag 128 表示无电池（台式机）
                    if (status.BatteryFlag != 128)
                    {
                        bool isUnplugged = status.ACLineStatus == 0; // 离线/电池供电
                        bool isLowBattery = status.BatteryLifePercent != 255 && status.BatteryLifePercent <= _config.BatteryThreshold;

                        bool shouldPause = isUnplugged || isLowBattery;

                        if (shouldPause && !_isBatteryPaused && IsActive)
                        {
                            _isBatteryPaused = true;
                            ApplyExecutionState();
                            BatteryStateChanged?.Invoke(true, status.BatteryLifePercent);
                            StateChanged?.Invoke();
                        }
                        else if (!shouldPause && _isBatteryPaused)
                        {
                            _isBatteryPaused = false;
                            ApplyExecutionState();
                            BatteryStateChanged?.Invoke(false, status.BatteryLifePercent);
                            StateChanged?.Invoke();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Awake", "检查电源状态异常", ex);
            }
        }

        private void CheckProcessTriggers()
        {
            // 总开关关闭时彻底不参与联动（含测试反射直调路径）
            if (!IsProcessLinkEnabled) return;

            try
            {
                if (_config.AutoAwakeProcesses == null || _config.AutoAwakeProcesses.Count == 0)
                {
                    if (_isProcessTriggered)
                    {
                        string old = _activeProcessTrigger;
                        _isProcessTriggered = false;
                        _activeProcessTrigger = string.Empty;
                        _processExitPendingSeconds = 0;
                        ApplyExecutionState();
                        ProcessTriggered?.Invoke(false, old);
                        StateChanged?.Invoke();
                    }
                    return;
                }

                var targets = new List<Tuple<string, string>>();
                foreach (var procSetting in _config.AutoAwakeProcesses)
                {
                    if (string.IsNullOrWhiteSpace(procSetting)) continue;
                    string nameOnly = ProcessHelper.NormalizeNameOnly(procSetting);
                    if (string.IsNullOrEmpty(nameOnly)) continue;
                    targets.Add(Tuple.Create(nameOnly.ToLowerInvariant(), ProcessHelper.Normalize(procSetting)));
                }

                if (targets.Count == 0) return;

                // 单次快照系统所有进程名，避免在循环中对每个监控项反复全系统枚举
                string matchedProc = null;
                var allProcesses = Process.GetProcesses();
                try
                {
                    var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in allProcesses)
                    {
                        try { runningNames.Add(p.ProcessName); } catch { }
                    }

                    foreach (var target in targets)
                    {
                        if (runningNames.Contains(target.Item1))
                        {
                            matchedProc = target.Item2;
                            break;
                        }
                    }
                }
                finally
                {
                    foreach (var p in allProcesses)
                    {
                        try { p?.Dispose(); } catch { }
                    }
                }

                if (!string.IsNullOrEmpty(matchedProc))
                {
                    // 检测到目标进程正在运行
                    if (_processExitPendingSeconds > 0)
                    {
                        // 处于退出缓冲期内进程重新启动或检测到其它目标进程，取消退出倒计时
                        _logger?.LogInfo("Awake", $"目标进程 '{matchedProc}' 正在运行，已取消退出倒计时");
                        _processExitPendingSeconds = 0;
                        _activeProcessTrigger = matchedProc;
                        StateChanged?.Invoke();
                    }
                    else if (_isProcessTriggered)
                    {
                        // 已经在进程联动中，若活跃进程切换（例如 A 退出，B 运行），同步当前名称
                        if (!string.Equals(_activeProcessTrigger, matchedProc, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.LogInfo("Awake", $"活跃进程联动目标切换: '{_activeProcessTrigger}' -> '{matchedProc}'");
                            _activeProcessTrigger = matchedProc;
                            StateChanged?.Invoke();
                        }
                    }
                    else
                    {
                        if (_userSuppressedProcessLink)
                        {
                            _logger?.LogInfo("Awake", $"目标进程 '{matchedProc}' 正在运行，但用户已手动关闭保持唤醒，暂不自动联动");
                        }
                        else
                        {
                            _isProcessTriggered = true;
                            _activeProcessTrigger = matchedProc;
                            // 进程联动是一个独立的叠加态，不篡改用户基底模式 _mode
                            ApplyExecutionState();
                            ProcessTriggered?.Invoke(true, matchedProc);
                            StateChanged?.Invoke();
                        }
                    }
                }
                else
                {
                    // 守护进程已全部退出：解除用户抑制
                    _userSuppressedProcessLink = false;

                    // 目标进程当前未检测到
                    if (_isProcessTriggered)
                    {
                        int delaySeconds = _config != null ? Math.Max(0, _config.AutoAwakeExitDelaySeconds) : 120;
                        if (delaySeconds <= 0)
                        {
                            // 未配置缓冲延时，立即退出
                            _processExitPendingSeconds = 0;
                            string oldProc = _activeProcessTrigger;
                            _isProcessTriggered = false;
                            _activeProcessTrigger = string.Empty;
                            ApplyExecutionState();
                            ProcessTriggered?.Invoke(false, oldProc);
                            StateChanged?.Invoke();
                        }
                        else if (_processExitPendingSeconds <= 0)
                        {
                            // 首次检测到退出，开启倒计时缓冲
                            _processExitPendingSeconds = delaySeconds;
                            _logger?.LogInfo("Awake", $"检测到目标进程已无运行实例，进入退出缓冲倒计时 ({delaySeconds} 秒)");
                            StateChanged?.Invoke();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Awake", "检测自动唤醒进程异常", ex);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
