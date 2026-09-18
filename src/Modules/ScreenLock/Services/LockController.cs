using System;
using System.Collections.Generic;
using System.Windows.Forms;
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

        private readonly List<LockWindow> _lockWindows = new List<LockWindow>();
        private bool _locked;

        public event Action Unlocked;

        public LockController(Func<IPinService> pinServiceProvider, Func<ScreenLockConfig> configProvider)
        {
            _pinServiceProvider = pinServiceProvider;
            _configProvider = configProvider;
            _blocker = new KeyboardBlocker();
            _pinGuard = new PinGuard(() => _pinServiceProvider?.Invoke());
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }
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
                    foreach (Screen screen in Screen.AllScreens)
                    {
                        var win = new LockWindow(this, this, screen, screen.Primary, IsShuttingDownProvider);
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

        public void Lock()
        {
            var pinService = _pinServiceProvider?.Invoke();
            if (_locked || pinService == null || !pinService.IsConfigured) return;
            _locked = true;
            _blocker.Install();

            foreach (Screen screen in Screen.AllScreens)
            {
                var win = new LockWindow(this, this, screen, screen.Primary, IsShuttingDownProvider);
                _lockWindows.Add(win);
                win.Show();
                win.ActivateIfNeeded();
            }
        }

        public void LockSafe()
        {
            try
            {
                Lock();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
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
            if (!_locked) return;
            _locked = false;
            foreach (var win in _lockWindows)
            {
                win.CloseSafe();
            }
            _lockWindows.Clear();
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
