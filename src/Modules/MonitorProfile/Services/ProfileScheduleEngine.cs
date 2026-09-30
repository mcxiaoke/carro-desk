using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using CarroDesk.Core;
using CarroDesk.Modules.MonitorProfile.Models;

namespace CarroDesk.Modules.MonitorProfile.Services
{
    /// <summary>
    /// 显示器情境时间表调度与系统电源事件自愈引擎。
    /// 负责定时轮询当前时间段生效配置、平滑切换模式、处理系统休眠与解锁唤醒后的延迟重试。
    /// </summary>
    public class ProfileScheduleEngine : IDisposable
    {
        private readonly MonitorDdcService _ddcService;
        private readonly ILoggerService _logger;
        private readonly Dispatcher _dispatcher;
        private readonly IDisplayPowerListener _displayPowerListener;
        private readonly bool _ownsDisplayListener;

        private readonly object _reapplyLock = new object();
        private CancellationTokenSource _reapplyCts;

        private MonitorProfileConfig _config;
        private DispatcherTimer _timer;
        private bool _isDisposed;
        private int _lifecycleGeneration;

        public IDisplayPowerListener DisplayPowerListener => _displayPowerListener;
        public bool IsDisplayOn => _displayPowerListener == null || _displayPowerListener.IsDisplayOn;

        public MonitorTimeSetting LastAppliedSetting { get; private set; }
        public int CurrentBrightness { get; private set; } = -1;
        public int CurrentContrast { get; private set; } = -1;

        public event Action<string, int, int> SettingApplied;
        public event Action StateChanged;

        public ProfileScheduleEngine(MonitorDdcService ddcService, Dispatcher dispatcher, ILoggerService logger = null, IDisplayPowerListener displayPowerListener = null)
        {
            _ddcService = ddcService ?? throw new ArgumentNullException(nameof(ddcService));
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
            _logger = logger;

            if (displayPowerListener != null)
            {
                _displayPowerListener = displayPowerListener;
                _ownsDisplayListener = false;
            }
            else
            {
                _displayPowerListener = new DisplayPowerListener(_dispatcher, _logger);
                _ownsDisplayListener = true;
            }
        }

        private void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] [MonitorSchedule] {message}");
        }

        public void Start(MonitorProfileConfig config)
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            _config = config ?? MonitorProfileConfig.CreateDefault();

            // 启动显示器电源监听
            if (_displayPowerListener != null)
            {
                _displayPowerListener.DisplayPowerChanged += OnDisplayPowerChanged;
                _displayPowerListener.Start();
            }

            // 监听系统电源事件与锁屏唤醒事件
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            // 启动定时检查定时器（每 30 秒轮询一次）
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                _timer.Tick += OnTimerTick;
            }
            _timer.Start();

            // 启动时初次异步应用当前设置（若显示器点亮开启）
            if (IsDisplayOn)
            {
                ApplyCurrentSetting(force: true);
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            lock (_reapplyLock)
            {
                _reapplyCts?.Cancel();
                _reapplyCts?.Dispose();
                _reapplyCts = null;
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
                _timer = null;
            }

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;

            if (_displayPowerListener != null)
            {
                _displayPowerListener.DisplayPowerChanged -= OnDisplayPowerChanged;
                _displayPowerListener.Stop();
            }
        }

        public void UpdateConfig(MonitorProfileConfig newConfig)
        {
            _config = newConfig ?? MonitorProfileConfig.CreateDefault();
            ApplyCurrentSetting(force: true);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_config == null || !_config.Enabled || !_config.AutoSchedule)
                return;

            if (!IsDisplayOn)
            {
                LogDebug("显示器处于关闭或休眠状态，跳过定时检查");
                return;
            }

            ApplyCurrentSetting(force: false);
        }

        private void OnDisplayPowerChanged(bool isDisplayOn)
        {
            if (isDisplayOn)
            {
                if (_config == null || !_config.Enabled || !_config.AutoSchedule) return;
                _logger?.LogInfo("MonitorSchedule", "检测到显示器点亮开启，延迟 3 秒应用当前时间段配置");
                ScheduleDelayedReapply(3000, true);
            }
            else
            {
                _logger?.LogInfo("MonitorSchedule", "检测到显示器已关闭或休眠，暂停定时调节与硬件通信");
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                if (_config == null || !_config.Enabled || !_config.AutoSchedule) return;
                _logger?.LogInfo("MonitorSchedule", "检测到系统从睡眠/休眠唤醒，准备延迟应用亮度配置");
                ScheduleDelayedReapply(3000, true);
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
            {
                if (_config == null || !_config.Enabled || !_config.AutoSchedule) return;
                _logger?.LogInfo("MonitorSchedule", "检测到用户会话解锁，准备重新校准显示器配置");
                ScheduleDelayedReapply(3000, false);
            }
        }

        private void ScheduleDelayedReapply(int delayMs, bool retryOnFail)
        {
            lock (_reapplyLock)
            {
                _reapplyCts?.Cancel();
                _reapplyCts?.Dispose();
                _reapplyCts = new CancellationTokenSource();
                var cts = _reapplyCts;

                int generation = Volatile.Read(ref _lifecycleGeneration);
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, cts.Token);
                        if (cts.IsCancellationRequested || generation != Volatile.Read(ref _lifecycleGeneration) ||
                            _config == null || !_config.Enabled || !_config.AutoSchedule || _isDisposed) return;

                        if (!IsDisplayOn)
                        {
                            LogDebug("延迟重试触发时显示器仍处于熄灭状态，跳过应用");
                            return;
                        }

                        int count = ApplyCurrentSettingSync(force: true);
                        if (count == 0 && retryOnFail)
                        {
                            _logger?.LogWarning("MonitorSchedule", "唤醒初次重连未探测到物理显示器，将在 5 秒后执行兜底重试");
                            await Task.Delay(5000, cts.Token);
                            if (cts.IsCancellationRequested || generation != Volatile.Read(ref _lifecycleGeneration) ||
                                _config == null || !_config.Enabled || !_config.AutoSchedule || _isDisposed) return;

                            if (!IsDisplayOn) return;

                            ApplyCurrentSettingSync(force: true);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常取消
                    }
                });
            }
        }

        /// <summary>
        /// 根据当前时间计算指定情境生效的时间段设置。
        /// </summary>
        public MonitorTimeSetting GetActiveSettingForProfile(string profileName)
        {
            return GetActiveSettingForProfile(profileName, DateTime.Now.TimeOfDay);
        }

        /// <summary>
        /// 根据指定时间点计算指定情境生效的时间段设置（供单测与内部调度使用）。
        /// </summary>
        public MonitorTimeSetting GetActiveSettingForProfile(string profileName, TimeSpan now)
        {
            if (_config?.Profiles == null || !_config.Profiles.TryGetValue(profileName, out var settings))
                return null;

            if (settings == null || settings.Count == 0)
                return null;

            var sorted = settings.OrderByDescending(s => s.ToTimeSpan()).ToList();

            // 寻找不大于当前时间的最近时间点；若早于当天第一个时间点，回退到前一天夜间（即降序列表第一项/时间最大项）
            return sorted.FirstOrDefault(s => now >= s.ToTimeSpan()) ?? sorted.First();
        }

        /// <summary>
        /// 获取下一个时间段设置及生效倒计时。
        /// </summary>
        public MonitorTimeSetting GetNextSettingForProfile(string profileName, out TimeSpan untilNext)
        {
            untilNext = TimeSpan.Zero;
            if (_config?.Profiles == null || !_config.Profiles.TryGetValue(profileName, out var settings))
                return null;

            if (settings == null || settings.Count == 0)
                return null;

            var sorted = settings.OrderBy(s => s.ToTimeSpan()).ToList();
            TimeSpan now = DateTime.Now.TimeOfDay;

            var next = sorted.FirstOrDefault(s => now < s.ToTimeSpan());
            if (next != null)
            {
                untilNext = next.ToTimeSpan() - now;
                return next;
            }

            // 跨午夜：明天第一个时间点
            var first = sorted.First();
            untilNext = (TimeSpan.FromDays(1) - now) + first.ToTimeSpan();
            return first;
        }

        /// <summary>
        /// 异步应用当前情境模式在当前时间的配置。
        /// </summary>
        public void ApplyCurrentSetting(bool force = false)
        {
            if (_config == null || !_config.Enabled) return;

            if (!IsDisplayOn)
            {
                LogDebug("显示器处于关闭或休眠状态，跳过应用配置");
                return;
            }

            string activeProfile = _config.ActiveProfile ?? "Daily";
            var activeSetting = GetActiveSettingForProfile(activeProfile);
            if (activeSetting == null) return;

            if (force || LastAppliedSetting == null ||
                LastAppliedSetting.Brightness != activeSetting.Brightness ||
                LastAppliedSetting.Contrast != activeSetting.Contrast)
            {
                // 硬件调用成功前不能写 LastAppliedSetting，否则失败后轮询会误判为已应用。
                ApplyValuesAsync(activeSetting.Brightness, activeSetting.Contrast, activeProfile, true, activeSetting);
            }
        }

        private int ApplyCurrentSettingSync(bool force = false)
        {
            if (_config == null || !_config.Enabled) return 0;

            if (!IsDisplayOn)
            {
                LogDebug("显示器处于关闭或休眠状态，跳过同步应用配置");
                return 0;
            }

            string activeProfile = _config.ActiveProfile ?? "Daily";
            var activeSetting = GetActiveSettingForProfile(activeProfile);
            if (activeSetting == null) return 0;

            int res = _ddcService.SetBrightnessAndContrastSync(activeSetting.Brightness, activeSetting.Contrast);
            if (res > 0)
            {
                LastAppliedSetting = activeSetting;
                CurrentBrightness = activeSetting.Brightness;
                CurrentContrast = activeSetting.Contrast;
                NotifyApplied(activeProfile, activeSetting.Brightness, activeSetting.Contrast);
            }
            else
            {
                _logger?.LogWarning("MonitorSchedule", "显示器硬件设置失败，保留待重试状态");
            }
            return res;
        }

        /// <summary>
        /// 切换当前生效的情境模式并立即生效。
        /// </summary>
        public void SwitchProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName) || _config == null) return;

            if (!_config.Profiles.ContainsKey(profileName))
                return;

            _config.ActiveProfile = profileName;
            ApplyCurrentSetting(force: true);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 步进调节亮度（正数为增加，负数为降低）。
        /// </summary>
        public void StepBrightness(int delta)
        {
            int baseBrightness = CurrentBrightness >= 0 ? CurrentBrightness : 50;
            int newBrightness = Math.Max(0, Math.Min(100, baseBrightness + delta));
            int contrast = CurrentContrast >= 0 ? CurrentContrast : 70;

            ApplyValuesAsync(newBrightness, contrast, _config?.ActiveProfile ?? "Manual", false, null);
        }

        /// <summary>
        /// 手动设置绝对亮度和对比度。
        /// </summary>
        public void SetDirectValues(int brightness, int contrast)
        {
            ApplyValuesAsync(brightness, contrast, _config?.ActiveProfile ?? "Manual", false, null);
        }

        private void ApplyValuesAsync(int brightness, int contrast, string profileName,
            bool markScheduleApplied, MonitorTimeSetting scheduleSetting)
        {
            int generation = Volatile.Read(ref _lifecycleGeneration);
            StateChanged?.Invoke();

            _ddcService.SetBrightnessAndContrastAsync(brightness, contrast).ContinueWith(t =>
            {
                if (generation != Volatile.Read(ref _lifecycleGeneration) || _isDisposed) return;
                if (t.IsFaulted)
                {
                    _logger?.LogError("MonitorSchedule", "异步应用显示器设置失败", t.Exception);
                    return;
                }

                DdcApplyResult applied = t.Result;
                if (applied.AppliedCount <= 0)
                {
                    _logger?.LogWarning("MonitorSchedule", "显示器硬件设置失败，保留待重试状态");
                    return;
                }

                // 合并请求的多个 continuation 会共享同一 Task；硬件最终采用的是 Task
                // 返回的最终快照，旧请求不得把自己的值写回 Current/LastAppliedSetting。
                CurrentBrightness = applied.Brightness;
                CurrentContrast = applied.Contrast;
                if (markScheduleApplied && scheduleSetting != null &&
                    scheduleSetting.Brightness == applied.Brightness &&
                    scheduleSetting.Contrast == applied.Contrast)
                    LastAppliedSetting = scheduleSetting;
                NotifyApplied(profileName, applied.Brightness, applied.Contrast);
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void NotifyApplied(string profile, int brightness, int contrast)
        {
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => NotifyApplied(profile, brightness, contrast)));
                return;
            }

            SettingApplied?.Invoke(profile, brightness, contrast);
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                Stop();
                if (_ownsDisplayListener && _displayPowerListener != null)
                {
                    _displayPowerListener.Dispose();
                }
            }
        }
    }
}
