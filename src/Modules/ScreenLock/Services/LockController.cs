using System;
using System.Collections.Generic;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Services.Localization;
using CarroDesk.Views;

namespace CarroDesk.Services
{
    public class LockController : IDisposable, ILockService, ILockAppearance
    {
        private readonly Func<IPinService> _pinServiceProvider;
        private readonly Func<ScreenLockConfig> _configProvider;
        private readonly PinGuard _pinGuard;
        private readonly KeyboardBlocker _blocker;
        private readonly ILoggerService _logger;

        private const string LogModuleId = "ScreenLock";

        private readonly List<LockWindow> _lockWindows = new List<LockWindow>();
        private bool _locked;

        public event Action Unlocked;

        public LockController(Func<IPinService> pinServiceProvider, Func<ScreenLockConfig> configProvider, ILoggerService logger = null)
        {
            _pinServiceProvider = pinServiceProvider;
            _configProvider = configProvider;
            _logger = logger;
            _blocker = new KeyboardBlocker();
            _pinGuard = new PinGuard(() => _pinServiceProvider?.Invoke());
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }
        }

        private void LogError(string message, Exception ex)
        {
            _logger?.LogError(LogModuleId, message, ex);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!_locked) return;
            try
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (!_locked) return;
                    foreach (var win in _lockWindows)
                    {
                        win.CloseSafe();
                    }
                    _lockWindows.Clear();
                    foreach (var monitor in DisplayMonitorHelper.GetAllMonitors())
                    {
                        var win = new LockWindow(this, this, monitor, monitor.IsPrimary, IsShuttingDownProvider);
                        _lockWindows.Add(win);
                        win.Show();
                        win.ActivateIfNeeded();
                    }
                }));
            }
            catch { }
        }

        public bool IsLocked { get { return _locked; } }

        // ILockAppearance：只读活引用，锁定窗口每次读取当前生效值
        public bool ShowClock { get { return _configProvider?.Invoke()?.ShowClock ?? true; } }
        public double OverlayOpacity { get { return _configProvider?.Invoke()?.OverlayOpacity ?? 0.88; } }

        public TimeSpan GetBlockRemaining()
        {
            return _locked ? _pinGuard.RemainingBlock() : TimeSpan.Zero;
        }

        public IPinService Pins { get { return _pinServiceProvider?.Invoke(); } }

        public void ApplyPinFromConfig()
        {
        }

        public Func<bool> IsShuttingDownProvider { get; set; }

        /// <summary>
        /// 锁定/解锁涉及 WPF 窗口与全局键盘钩子，必须在 UI 线程执行。
        /// 本控制器会把 Lock/Unlock 暴露给 SystemEvents（会话/显示设置变更）与
        /// SystemIdleService（线程池）等非 UI 线程回调，因此入口统一封送。
        /// </summary>
        private static void RunOnUiThread(Action action, Action<string, Exception> onError = null)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { onError?.Invoke("UI 线程执行锁定/解锁操作失败", ex); }
            }));
        }

        public void Lock()
        {
            var pinService = _pinServiceProvider?.Invoke();
            if (_locked || pinService == null || !pinService.IsConfigured) return;

            RunOnUiThread(() => LockCore(pinService), LogError);
        }

        private void LockCore(IPinService pinService)
        {
            if (_locked || pinService == null || !pinService.IsConfigured) return;

            _locked = true;

            var createdWindows = new List<LockWindow>();
            bool hookInstalled = false;
            try
            {
                _blocker.Install();
                hookInstalled = true;

                foreach (var monitor in DisplayMonitorHelper.GetAllMonitors())
                {
                    var win = new LockWindow(this, this, monitor, monitor.IsPrimary, IsShuttingDownProvider);
                    createdWindows.Add(win);
                    _lockWindows.Add(win);
                    win.Show();
                    win.ActivateIfNeeded();
                }
            }
            catch (Exception ex)
            {
                // 回滚：绝不允许停留在"Win/Alt 组合键已被钩子屏蔽、但没有任何 PIN 输入界面"
                // 的死状态——那会让用户彻底无法操作，只能杀进程。
                LogError("创建锁屏窗口失败，正在回滚锁定状态", ex);

                foreach (var win in createdWindows)
                {
                    try { win.ForceClose(); } catch { }
                }
                _lockWindows.Clear();

                if (hookInstalled)
                {
                    try { _blocker.Remove(); } catch { }
                }

                _locked = false;
                throw;
            }
        }

        /// <summary>
        /// 锁定并返回是否成功。失败时调用方应重置空闲状态机以便重试。
        /// </summary>
        public bool LockSafe()
        {
            try
            {
                Lock();
                return true;
            }
            catch (Exception ex)
            {
                LogError("锁定失败", ex);
                return false;
            }
        }

        public PinAttemptResult TryUnlock(string pin, out string error)
        {
            TimeSpan remaining;
            var result = _pinGuard.Try(pin, out remaining);
            if (result == PinAttemptResult.Success)
            {
                error = null;
                Unlock();
                return PinAttemptResult.Success;
            }
            if (result == PinAttemptResult.Blocked)
            {
                error = Loc.T("Lock.PenaltyWait", remaining);
                return PinAttemptResult.Blocked;
            }
            error = Loc.T("Lock.IncorrectPin");
            if (_pinGuard.RemainingBlock() > TimeSpan.Zero)
            {
                var left = _pinGuard.RemainingBlock();
                error += Loc.T("Lock.PinLockedWithTime", left);
            }
            return PinAttemptResult.Wrong;
        }

        public void Unlock()
        {
            // 本方法会从 SystemEvents 的专用线程（会话解锁）与 Dispatcher 线程（PIN 校验）
            // 两处调用。窗口操作必须回到 UI 线程：原先直接在这里操作 WPF 对象，
            // 跨线程异常被 catch 吞掉后 `_lockWindows.Clear()` 仍会执行，导致引用丢失，
            // 全屏置顶浮层永久残留、用户只能杀进程。
            RunOnUiThread(UnlockCore, LogError);
        }

        private void UnlockCore()
        {
            // 允许 _locked 已为 false 但仍有窗口未关闭时重试（见下方 failed 处理）
            if (!_locked && _lockWindows.Count == 0) return;

            _locked = false;

            // 先关窗，仅保留真正关闭失败的引用，避免"窗口还在但句柄已丢"
            var failed = new List<LockWindow>();
            foreach (var win in _lockWindows)
            {
                if (!win.CloseSafe()) failed.Add(win);
            }
            _lockWindows.Clear();
            if (failed.Count > 0)
            {
                _lockWindows.AddRange(failed);
                LogError(string.Format("有 {0} 个锁屏窗口关闭失败，已保留引用以便重试", failed.Count), null);
            }

            _blocker.Remove();

            var handler = Unlocked;
            if (handler != null) handler();
        }

        public bool VerifyForExit(string pin)
        {
            var pinService = _pinServiceProvider?.Invoke();
            return pinService != null && pinService.Verify(pin);
        }

        public void Dispose()
        {
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
            Unlock();
            _blocker.Dispose();
        }
    }
}
