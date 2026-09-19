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

        public AwakeMode Mode => _mode;
        public bool KeepDisplayOn => _keepDisplayOn;
        public DateTime ExpireTime => _expireTime;
        public bool IsActive => _mode != AwakeMode.Passive && !_isBatteryPaused;
        public bool IsBatteryPaused => _isBatteryPaused;
        public bool IsProcessTriggered => _isProcessTriggered;
        public string ActiveProcessTrigger => _activeProcessTrigger;

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
            _keepDisplayOn = _config.KeepDisplayOn;
        }

        public void UpdateConfig(AwakeConfig config)
        {
            _config = config ?? AwakeConfig.CreateDefault();
            _keepDisplayOn = _config.KeepDisplayOn;
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
            _isBatteryPaused = false;
            _processExitPendingSeconds = 0;

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        public void SetIndefinite()
        {
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
                if (_mode == AwakeMode.Passive || _isBatteryPaused)
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
                    if (_isProcessTriggered && _mode == AwakeMode.Indefinite)
                    {
                        string oldProc = _activeProcessTrigger;
                        _isProcessTriggered = false;
                        _activeProcessTrigger = string.Empty;
                        SetPassive();
                        ProcessTriggered?.Invoke(false, oldProc);
                    }
                }
            }

            // 2. 电池状态轮询（每 3 秒检查一次）
            if (_config != null && _config.DisableOnBattery && _tickCount % 3 == 0)
            {
                CheckBatteryStatus();
            }

            // 3. 自动唤醒进程监测（每 5 秒检查一次）
            if (_config != null && _config.AutoAwakeProcesses != null && _config.AutoAwakeProcesses.Count > 0 && _tickCount % 5 == 0)
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

                        if (shouldPause && !_isBatteryPaused && _mode != AwakeMode.Passive)
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
            try
            {
                string matchedProc = null;
                foreach (var procSetting in _config.AutoAwakeProcesses)
                {
                    if (string.IsNullOrWhiteSpace(procSetting)) continue;
                    string nameOnly = ProcessHelper.NormalizeNameOnly(procSetting);
                    if (string.IsNullOrEmpty(nameOnly)) continue;

                    var processes = Process.GetProcessesByName(nameOnly);
                    try
                    {
                        if (processes != null && processes.Length > 0)
                        {
                            matchedProc = ProcessHelper.Normalize(procSetting);
                            break;
                        }
                    }
                    finally
                    {
                        if (processes != null)
                        {
                            foreach (var p in processes)
                            {
                                try { p?.Dispose(); } catch { }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(matchedProc))
                {
                    // 检测到目标进程正在运行
                    if (_processExitPendingSeconds > 0)
                    {
                        // 处于退出缓冲期内进程重新启动，直接取消退出倒计时，继续保持唤醒
                        _logger?.LogInfo("Awake", $"目标进程 '{matchedProc}' 在退出缓冲期内重新恢复运行，已取消退出倒计时");
                        _processExitPendingSeconds = 0;
                        _activeProcessTrigger = matchedProc;
                    }
                    else if (_mode == AwakeMode.Passive)
                    {
                        _isProcessTriggered = true;
                        _activeProcessTrigger = matchedProc;
                        _mode = AwakeMode.Indefinite;
                        _expireTime = DateTime.MinValue;
                        ApplyExecutionState();
                        ProcessTriggered?.Invoke(true, matchedProc);
                        StateChanged?.Invoke();
                    }
                }
                else
                {
                    // 目标进程当前未检测到
                    if (_isProcessTriggered && _mode == AwakeMode.Indefinite)
                    {
                        int delaySeconds = _config != null ? Math.Max(0, _config.AutoAwakeExitDelaySeconds) : 120;
                        if (delaySeconds <= 0)
                        {
                            // 未配置缓冲延时，立即退出
                            _processExitPendingSeconds = 0;
                            string oldProc = _activeProcessTrigger;
                            _isProcessTriggered = false;
                            _activeProcessTrigger = string.Empty;
                            SetPassive();
                            ProcessTriggered?.Invoke(false, oldProc);
                        }
                        else if (_processExitPendingSeconds <= 0)
                        {
                            // 首次检测到退出，开启倒计时缓冲
                            _processExitPendingSeconds = delaySeconds;
                            _logger?.LogInfo("Awake", $"检测到目标进程已无运行实例，进入退出缓冲倒计时 ({delaySeconds} 秒)");
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
