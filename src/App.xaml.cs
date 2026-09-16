using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using ScreenLock.Services;
using ScreenLock.Services.Localization;
using ScreenLock.Services.Tasks;
using ScreenLock.Views;

namespace ScreenLock
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\ScreenLock_SingleInstance_2C7A4F10";

        public static ConfigService Config { get; private set; }
        public static LockController Controller { get; private set; }
        public static IdleDetector Idle { get; private set; }
        public static TaskSchedulerService TaskScheduler { get; private set; }
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static TaskbarIcon _tbIcon;

        private static TrayContextMenu _trayMenu;

        private bool _sessionLocked;
        private DateTime _pauseUntil = DateTime.MinValue;

        internal static App CurrentApp => Current as App;
        internal DateTime PauseUntil => _pauseUntil;
        internal bool IsPaused => DateTime.Now < _pauseUntil;
        internal static TrayContextMenu TrayMenu => _trayMenu;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                Shutdown(0);
                return;
            }

            DispatcherUnhandledException += OnDispatcherException;

            Config = new ConfigService();
            Config.LoadOrCreate();

            I18nService.Instance.Init(Config.Current.Language);
            I18nService.Instance.LanguageChanged += () =>
            {
                UpdateTrayText();
                _trayMenu?.RefreshAll();
            };

            Controller = new LockController(Config);

            if (!Config.Current.HasPin())
            {
                var wizard = new FirstRunWindow();
                if (wizard.ShowDialog() == true)
                {
                    Controller.Pins.SetNewPin(wizard.NewPin);
                    Config.Current.PinSalt = Controller.Pins.Salt;
                    Config.Current.PinHash = Controller.Pins.Hash;
                    Config.Save();
                }
                else
                {
                    Shutdown(0);
                    return;
                }
            }

            AutoStartService.Sync(Config.Current.AutoStart);

            CreateTrayIcon();

            Idle = new IdleDetector();
            Idle.Threshold = TimeSpan.FromMinutes(Config.Current.IdleMinutes);
            Idle.WarnBefore = TimeSpan.FromSeconds(30);
            Idle.ShouldSuspend = ShouldSuspendIdle;
            Idle.Warning += OnIdleWarning;
            Idle.ThresholdReached += OnIdleThresholdReached;
            Idle.Start();

            Controller.Unlocked += () =>
            {
                Idle.Reset();
                UpdateTrayText();
                _trayMenu?.RefreshStatus();
            };

            SystemEvents.SessionSwitch += OnSessionSwitch;

            // AutoRun tasks - independent shell, logs to logs/task-*.log
            try
            {
                TaskScheduler = new TaskSchedulerService(Idle);
                // ensure scripts dir exists early
                try { System.IO.Directory.CreateDirectory(ConfigService.ScriptsDirPath); } catch { }
                TaskScheduler.Start();
                try { RefreshTaskMenu(); } catch { }
                try { RefreshMenuChecks(); } catch { }
            }
            catch (Exception ex) { LogError(ex); }

            Exit += OnAppExit;

            UpdateTrayText();
        }

        private bool ShouldSuspendIdle()
        {
            if (_sessionLocked) return true;
            if (DateTime.Now < _pauseUntil) return true;
            if (IdleDetector.IsSystemBusy()) return true;
            try
            {
                var excl = Config.Current != null ? Config.Current.ExcludeProcesses : null;
                if (excl != null && excl.Count > 0 && ProcessExclusionService.IsExcludedRunning(excl))
                    return true;
            }
            catch { }
            return false;
        }

        private void OnIdleThresholdReached()
        {
            if (_sessionLocked) return;
            Controller.LockSafe();
        }

        private void OnIdleWarning()
        {
            if (_tbIcon == null) return;
            ShowBalloon(Loc.T("Tray.BalloonIdleWarn", Config.Current.IdleMinutes));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsShuttingDown || Idle == null || Controller == null) return;
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _sessionLocked = true;
                    Idle.Suspend();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _sessionLocked = false;
                    // 若配置开启 UnlockOnResume（默认 true），当 Windows 会话解锁时自动解除 ScreenLock 锁屏
                    if (Config.Current.UnlockOnResume && Controller.IsLocked)
                    {
                        Controller.Unlock();
                    }
                    Idle.Reset();
                    UpdateTrayText();
                    _trayMenu?.RefreshStatus();
                }
                else if (e.Reason == SessionSwitchReason.RemoteDisconnect)
                {
                    Controller.LockSafe();
                }
            }));
        }

        internal void ReloadConfig()
        {
            var ok = Config.Reload();
            var c = Config.Current;
            I18nService.Instance.SetLanguage(c.Language);
            Idle.Threshold = TimeSpan.FromMinutes(c.IdleMinutes);
            Idle.Reset();
            Controller.ApplyPinFromConfig();
            AutoStartService.Sync(c.AutoStart);
            try { ProcessExclusionService.InvalidateCache(); } catch { }
            RefreshMenuChecks();
            UpdateTrayText();
            _trayMenu?.RefreshStatus();
            if (_tbIcon != null)
            {
                string balloonMsg = ok
                    ? Loc.T("Tray.BalloonConfigReloaded", c.IdleMinutes)
                    : Loc.T("Tray.BalloonConfigFailed");
                _tbIcon.ShowBalloonTip("ScreenLock", balloonMsg, BalloonIcon.Info);
            }
        }

        internal void ReloadTasks()
        {
            if (TaskScheduler == null)
            {
                ShowBalloon(Loc.T("Tray.BalloonTasksNotInit"));
                return;
            }
            var result = TaskScheduler.Reload();
            try { RefreshTaskMenu(); } catch { }
            try { RefreshMenuChecks(); } catch { }
            var msg = result.Errors.Count == 0
                ? Loc.T("Tray.BalloonTasksReloadedSuccess", result.Tasks.Count)
                : Loc.T("Tray.BalloonTasksReloadedErrors", result.Tasks.Count, result.Errors.Count);
            if (result.Errors.Count > 0)
                msg += Loc.T("Tray.BalloonTasksErrorsHint");
            if (TaskScheduler != null && !TaskScheduler.IsGlobalEnabled)
                msg += Loc.T("Tray.BalloonTasksDisabledHint");
            ShowBalloon(msg);
        }

        public void RefreshTaskMenu()
        {
            try
            {
                _trayMenu?.RefreshTaskSubmenu();
            }
            catch { }
        }

        public static void ShowBalloonPublic(string text)
        {
            try
            {
                var app = Current as App;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() => app.ShowBalloon(text)));
                else
                {
                    // fallback if no app
                }
            }
            catch { }
        }

        private void CreateTrayIcon()
        {
            _trayMenu = new TrayContextMenu();

            _tbIcon = new TaskbarIcon
            {
                Icon = LoadAppIcon(),
                ToolTipText = "ScreenLock",
                ContextMenu = _trayMenu
            };
            _tbIcon.TrayMouseDoubleClick += (s, e) => Controller.LockSafe();

            RefreshMenuChecks();
        }

        internal void SetIdleMinutes(int minutes)
        {
            Config.Current.IdleMinutes = minutes;
            Config.Save();
            Idle.Threshold = TimeSpan.FromMinutes(minutes);
            Idle.Reset();
            RefreshMenuChecks();
            UpdateTrayText();
            _trayMenu?.RefreshStatus();
        }

        internal void PauseFor(TimeSpan duration)
        {
            _pauseUntil = DateTime.Now.Add(duration);
            Idle.Reset();
            UpdateTrayText();
            _trayMenu?.RefreshStatus();
            ShowBalloon(Loc.T("Tray.BalloonPause", _pauseUntil));
        }

        internal void ResumeIdle()
        {
            _pauseUntil = DateTime.MinValue;
            Idle.Reset();
            UpdateTrayText();
            _trayMenu?.RefreshStatus();
        }

        internal void RefreshMenuChecks()
        {
            _trayMenu?.RefreshChecks();
        }

        internal void ShowBalloon(string text)
        {
            if (_tbIcon == null) return;
            try
            {
                _tbIcon.ShowBalloonTip("ScreenLock", text, BalloonIcon.Info);
            }
            catch { }
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Icon.ico");
                using (var stream = GetResourceStream(uri).Stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
            catch
            {
                return System.Drawing.SystemIcons.Shield;
            }
        }

        internal void UpdateTrayText()
        {
            if (_tbIcon == null) return;
            try
            {
                string text;
                if (DateTime.Now < _pauseUntil)
                    text = Loc.T("Tray.TooltipPaused", _pauseUntil);
                else if (Config.Current.IdleMinutes <= 0)
                    text = Loc.T("Tray.TooltipDisabled");
                else
                    text = Loc.T("Tray.TooltipIdle", Config.Current.IdleMinutes);
                _tbIcon.ToolTipText = text;
            }
            catch { }
        }

        internal void PromptExit()
        {
            var win = new VerifyPinWindow(Controller, Loc.T("Tray.ExitPrompt"))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            if (win.ShowDialog() == true)
                ExitApp();
        }

        public static void ExitApp()
        {
            IsShuttingDown = true;
            try
            {
                Current.Dispatcher.BeginInvoke(new Action(() => Current.Shutdown()));
            }
            catch { }
        }

        private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            e.Handled = true;
        }

        private static void LogError(Exception ex)
        {
            try
            {
                var path = Path.Combine(ConfigService.DirPath, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Delete(path);
                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
            }
            catch { }
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            IsShuttingDown = true;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { if (TaskScheduler != null) TaskScheduler.Stop(); } catch { }
            try { if (TaskScheduler != null) TaskScheduler.Dispose(); } catch { }
            try { if (Controller != null) Controller.Dispose(); } catch { }
            try { if (Idle != null) Idle.Dispose(); } catch { }
            try
            {
                if (_tbIcon != null)
                {
                    _tbIcon.Dispose();
                }
            }
            catch { }
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }
}
