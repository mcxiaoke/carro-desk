using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.TaskScheduler;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Modules.AudioSwitch;
using CarroDesk.Modules.AppAutoMute;
using CarroDesk.Services.Tasks;
using CarroDesk.Views;

namespace CarroDesk
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\CarroDesk_SingleInstance_2C7A4F10";

        public static ServiceContainer Services { get; private set; }
        public static ModuleManager Modules { get; private set; }

        public static ScreenLockModule ScreenLockMod => ScreenLockModule.Instance;
        public static TaskSchedulerModule TaskSchedulerMod => TaskSchedulerModule.Instance;
        public static AppAutoMuteModule AppAutoMuteMod => AppAutoMuteModule.Instance;

        // 向后兼容各 View 的配置门面（全局 AppSettings，非模块专属）
        public static ConfigService Config => (Services?.GetService<IConfigManager>() as ConfigManager)?.Underlying;
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static TaskbarIcon _tbIcon;
        private static TrayContextMenu _trayMenu;
        private DynamicTrayController _trayController;

        internal static App CurrentApp => Current as App;
        internal DateTime PauseUntil => ScreenLockMod != null ? ScreenLockMod.PauseUntil : DateTime.MinValue;
        internal bool IsPaused => ScreenLockMod != null && ScreenLockMod.IsPaused;
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

            // 宿主顶级三层未捕获异常防御网
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 1. 初始化基础设施与服务容器
            Services = new ServiceContainer();
            var logger = new DefaultLoggerService();
            Services.AddSingleton<ILoggerService>(logger);

            var rawConfig = new ConfigService();
            rawConfig.LoadOrCreate();

            var configMgr = new ConfigManager(rawConfig);
            Services.AddSingleton<IConfigManager>(configMgr);

            // PIN 能力下沉 Host（规范 §3.5）：缺模块仍按"有 PIN 则验"兜底
            var hostPinService = new HostPinService(rawConfig);
            Services.AddSingleton<IPinService>(hostPinService);

            I18nService.Instance.Init(configMgr.Current.Language);
            I18nService.Instance.LanguageChanged += () =>
            {
                UpdateTrayText();
                Modules?.OnLanguageChangedAll();
                _trayMenu?.RefreshTray();
            };
            Services.AddSingleton(I18nService.Instance);

            // 2. 首次运行安全向导
            if (!configMgr.Current.HasPin())
            {
                var wizard = new FirstRunWindow();
                if (wizard.ShowDialog() == true)
                {
                    hostPinService.SetNewPin(wizard.NewPin);
                    configMgr.Current.PinSalt = hostPinService.Salt;
                    configMgr.Current.PinHash = hostPinService.Hash;
                    configMgr.Save();
                }
                else
                {
                    Shutdown(0);
                    return;
                }
            }

            AutoStartService.Sync(configMgr.Current.AutoStart);

            // 3. 构建托盘
            CreateTrayIcon();

            // 4. 构建并注册核心业务模块与基础设施
            var audioService = new AudioService();
            Services.AddSingleton(audioService);
            Services.AddSingleton<IAudioService>(audioService);

            var foregroundTracker = new ForegroundTracker();
            Services.AddSingleton(foregroundTracker);
            Services.AddSingleton<IForegroundTracker>(foregroundTracker);

            Services.AddSingleton<IHotkeyService>(HotkeyService.Instance);
            Services.AddSingleton<INotificationService>(new DelegatedNotificationService((msg, title) => ShowBalloon(msg)));
            Services.AddSingleton<IIdleService>(new SystemIdleService());

            Modules = new ModuleManager
            {
                Dispatcher = Dispatcher,
                RequestTrayRefresh = () => { _trayController?.RequestRefresh(); },
                ShowNotification = (msg, title) => ShowBalloon(msg)
            };
            Services.AddSingleton(Modules);

            var trayLogger = Services.GetService<ILoggerService>();
            _trayController = new DynamicTrayController(Modules, Dispatcher, trayLogger,
                (msg, title) => ShowBalloon(msg));
            _trayController.Attach(_trayMenu);

            var screenLockModule = new ScreenLockModule(rawConfig)
            {
                BalloonNotifier = ShowBalloon
            };
            Modules.RegisterModule(screenLockModule);

            var taskSchedulerModule = new TaskSchedulerModule();
            Modules.RegisterModule(taskSchedulerModule);
            Services.AddSingleton<ITaskSchedulerService>(sp => taskSchedulerModule.Scheduler);

            var audioSwitchModule = new AudioSwitchModule()
            {
                NotificationCallback = ShowBalloon
            };
            Modules.RegisterModule(audioSwitchModule);

            var appAutoMuteModule = new AppAutoMuteModule()
            {
                NotificationCallback = ShowBalloon
            };
            Modules.RegisterModule(appAutoMuteModule);

            // 5. 初始化并启动模块
            Modules.InitializeAll(Services);
            Modules.StartAll();

            screenLockModule.Controller.Unlocked += () =>
            {
                UpdateTrayText();
                _trayController?.RequestRefresh();
            };

            Exit += OnAppExit;
            UpdateTrayText();
        }

        internal void ReloadConfig()
        {
            var configMgr = Services?.GetService<IConfigManager>();
            configMgr?.Reload();
            var c = Config.Current;
            I18nService.Instance.SetLanguage(c.Language);
            Modules?.ReloadAll();
            AutoStartService.Sync(c.AutoStart);
            try { ProcessExclusionService.InvalidateCache(); } catch { }
            _trayController?.RequestRefresh();
            UpdateTrayText();
            if (_tbIcon != null)
            {
                string balloonMsg = Loc.T("Tray.BalloonConfigReloaded", c.IdleMinutes);
                _tbIcon.ShowBalloonTip("CarroDesk", balloonMsg, BalloonIcon.Info);
            }
        }

        internal void ReloadTasks()
        {
            var scheduler = Services?.GetService<ITaskSchedulerService>();
            if (scheduler == null)
            {
                ShowBalloon(Loc.T("Tray.BalloonTasksNotInit"));
                return;
            }
            var result = scheduler.Reload();
            try { RefreshTaskMenu(); } catch { }
            try { RefreshMenuChecks(); } catch { }
            var msg = result.Errors == 0
                ? Loc.T("Tray.BalloonTasksReloadedSuccess", result.Tasks)
                : Loc.T("Tray.BalloonTasksReloadedErrors", result.Tasks, result.Errors);
            if (result.Errors > 0)
                msg += Loc.T("Tray.BalloonTasksErrorsHint");
            if (!scheduler.IsGlobalEnabled)
                msg += Loc.T("Tray.BalloonTasksDisabledHint");
            ShowBalloon(msg);
        }

        public void RefreshTaskMenu()
        {
            try
            {
                _trayController?.RequestRefresh();
            }
            catch { }
        }

        public static void ShowBalloonPublic(string text)
        {
            try
            {
                var app = Current as App;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() => app.ShowBalloon(text)));
            }
            catch { }
        }

        private void CreateTrayIcon()
        {
            _trayMenu = new TrayContextMenu();

            _tbIcon = new TaskbarIcon
            {
                Icon = LoadAppIcon(),
                ToolTipText = "CarroDesk",
                ContextMenu = _trayMenu
            };
            _tbIcon.TrayMouseDoubleClick += (s, e) => ScreenLockMod?.LockSafe();

            RefreshMenuChecks();
        }

        internal void SetIdleMinutes(int minutes)
        {
            ScreenLockMod?.SetIdleMinutes(minutes);
            UpdateTrayText();
            _trayController?.RequestRefresh();
        }

        internal void PauseFor(TimeSpan duration)
        {
            ScreenLockMod?.PauseFor(duration);
            UpdateTrayText();
            _trayController?.RequestRefresh();
            ShowBalloon(Loc.T("Tray.BalloonPause", ScreenLockMod.PauseUntil));
        }

        internal void ResumeIdle()
        {
            ScreenLockMod?.ResumeIdle();
            UpdateTrayText();
            _trayController?.RequestRefresh();
        }

        internal void RefreshMenuChecks()
        {
            _trayController?.RequestRefresh();
        }

        internal void ShowBalloon(string text)
        {
            if (_tbIcon == null) return;
            try
            {
                _tbIcon.ShowBalloonTip("CarroDesk", text, BalloonIcon.Info);
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
                // 不做四态聚合（§4.4/§M13）：ToolTipText 保持最简应用名，状态由各模块菜单项自述
                _tbIcon.ToolTipText = "CarroDesk";
            }
            catch { }
        }

        internal void PromptExit()
        {
            // 退出守卫协商（规范 §3.5）：任一守卫要求阻止时，展示 Host 持有的挑战 UI
            bool blocked = false;
            foreach (var module in Modules.Modules)
            {
                var guard = module as IExitGuard;
                if (guard == null) continue;
                bool b = false;
                bool ok = SafeInvoker.RunTimeout(module.Id, TimeSpan.FromSeconds(3),
                    () => b = guard.RequestBlockExit(), (id, ex) => LogError(ex));
                if (ok && b) { blocked = true; break; }
            }

            if (!blocked)
            {
                ExitApp();
                return;
            }

            var pinService = Services?.GetService<IPinService>();
            var win = new VerifyPinWindow(pinService, Loc.T("Tray.ExitPrompt"))
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
                Current?.Dispatcher?.BeginInvoke(new Action(() => Current.Shutdown()));
            }
            catch { }
        }

        private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            e.Handled = true;
        }

        private void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogError(ex);
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogError(e.Exception);
            e.SetObserved();
        }

        private static void LogError(Exception ex)
        {
            try
            {
                var logger = Services?.GetService<ILoggerService>();
                if (logger != null)
                {
                    logger.LogError("Host", "全局未处理异常", ex);
                }
                else
                {
                    var path = Path.Combine(ConfigService.DirPath, "log.txt");
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [CRITICAL] {ex}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            IsShuttingDown = true;
            try { Modules?.StopAll(); } catch { }
            try { Modules?.Dispose(); } catch { }
            try { Services?.GetService<ForegroundTracker>()?.Dispose(); } catch { }
            try { Services?.GetService<AudioService>()?.Dispose(); } catch { }
            try
            {
                if (_tbIcon != null)
                {
                    _tbIcon.Dispose();
                }
            }
            catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
        }
    }
}
