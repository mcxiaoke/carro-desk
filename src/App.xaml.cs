using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.ClipboardHistory;
using CarroDesk.Services.Tasks;
using CarroDesk.Views;

namespace CarroDesk
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\CarroDesk_SingleInstance_2C7A4F10";

        public static ServiceContainer Services { get; private set; }
        public static ModuleManager Modules { get; private set; }

        private ScreenLockModule _screenLockModule;
        private ConfigManager _configManager;

        // 向后兼容各 View 的配置门面（全局 AppSettings，非模块专属）
        public static ConfigService Config => (Services?.GetService<IConfigManager>() as ConfigManager)?.Underlying;
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static TaskbarIcon _tbIcon;
        private static TrayContextMenu _trayMenu;
        private DynamicTrayController _trayController;
        private int _floatingPanelHotkeyId;

        internal static App CurrentApp => Current as App;
        internal DateTime PauseUntil => _screenLockModule != null ? _screenLockModule.PauseUntil : DateTime.MinValue;
        internal bool IsPaused => _screenLockModule != null && _screenLockModule.IsPaused;
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

            _configManager = new ConfigManager(rawConfig, logger);
            Services.AddSingleton<ConfigManager>(_configManager);
            Services.AddSingleton<IConfigManager>(_configManager);

            // PIN 能力下沉 Host（规范 §3.5）：缺模块仍按"有 PIN 则验"兜底
            var hostPinService = new HostPinService(rawConfig);
            Services.AddSingleton<IPinService>(hostPinService);

            I18nService.Instance.Init(_configManager.Current.Language);
            I18nService.Instance.LanguageChanged += () =>
            {
                UpdateTrayText();
                Modules?.OnLanguageChangedAll();
                _trayMenu?.RefreshTray();
            };
            Services.AddSingleton(I18nService.Instance);

            // 2. 首次运行安全向导
            if (!_configManager.Current.HasPin())
            {
                var wizard = new FirstRunWindow();
                if (wizard.ShowDialog() == true)
                {
                    hostPinService.SetNewPin(wizard.NewPin);
                    _configManager.Current.PinSalt = hostPinService.Salt;
                    _configManager.Current.PinHash = hostPinService.Hash;
                    _configManager.Save();
                }
                else
                {
                    Shutdown(0);
                    return;
                }
            }

            AutoStartService.Sync(_configManager.Current.AutoStart);

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

            _screenLockModule = new ScreenLockModule();
            _screenLockModule.Controller.IsShuttingDownProvider = () => IsShuttingDown;
            Modules.RegisterModule(_screenLockModule);

            var taskSchedulerModule = new TaskSchedulerModule();
            Modules.RegisterModule(taskSchedulerModule);
            Services.AddSingleton<ITaskSchedulerService>(sp => taskSchedulerModule.Scheduler);

            var audioSwitchModule = new AudioSwitchModule();
            Modules.RegisterModule(audioSwitchModule);

            var appAutoMuteModule = new AppAutoMuteModule();
            Modules.RegisterModule(appAutoMuteModule);

            var monitorProfileModule = new CarroDesk.Modules.MonitorProfile.MonitorProfileModule();
            Modules.RegisterModule(monitorProfileModule);

            var awakeModule = new AwakeModule();
            Modules.RegisterModule(awakeModule);

            var clipboardHistoryModule = new ClipboardHistoryModule();
            Modules.RegisterModule(clipboardHistoryModule);

            // 5. 初始化并启动模块
            Modules.InitializeAll(Services);
            Modules.StartAll();

            _screenLockModule.Controller.Unlocked += () =>
            {
                UpdateTrayText();
                _trayController?.RequestRefresh();
            };

            Exit += OnAppExit;
            UpdateTrayText();
            RegisterFloatingPanelHotkey();
        }

        public void ToggleFloatingPanel()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CarroDesk.Views.FloatingPanelWindow.Toggle(
                    _configManager,
                    Modules,
                    Services,
                    ReloadConfig,
                    PromptExit,
                    UpdateTrayText);
            }));
        }

        private void RegisterFloatingPanelHotkey()
        {
            UnregisterFloatingPanelHotkey();
            var hotkeys = Services?.GetService<IHotkeyService>();
            var config = Config?.Current;
            if (hotkeys != null && config != null && !string.IsNullOrWhiteSpace(config.FloatingPanelHotkey))
            {
                try
                {
                    _floatingPanelHotkeyId = hotkeys.Register("Host.FloatingPanel", config.FloatingPanelHotkey, () =>
                    {
                        ToggleFloatingPanel();
                    }, out _);
                }
                catch { /* intentionally ignored: hotkey conflict or invalid sequence */ }
            }
        }

        private void UnregisterFloatingPanelHotkey()
        {
            if (_floatingPanelHotkeyId > 0)
            {
                try
                {
                    var hotkeys = Services?.GetService<IHotkeyService>();
                    hotkeys?.Unregister("Host.FloatingPanel", _floatingPanelHotkeyId);
                    _floatingPanelHotkeyId = 0;
                }
                catch { /* intentionally ignored: hotkey already unregistered */ }
            }
        }

        internal void ReloadConfig()
        {
            var configMgr = Services?.GetService<IConfigManager>();
            configMgr?.Reload();
            var c = Config.Current;
            I18nService.Instance.SetLanguage(c.Language);
            Modules?.ReloadAll();
            AutoStartService.Sync(c.AutoStart);
            try { ProcessExclusionService.InvalidateCache(); } catch { /* intentionally ignored: cache invalidation */ }
            RegisterFloatingPanelHotkey();
            _trayController?.RequestRefresh();
            UpdateTrayText();
            if (_tbIcon != null)
            {
                var slConfig = _configManager?.GetModuleConfig<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>("ScreenLock");
                int idleMins = slConfig?.IdleMinutes ?? 5;
                string balloonMsg = Loc.T("Tray.BalloonConfigReloaded", idleMins);
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
            try { RefreshTaskMenu(); } catch { /* intentionally ignored: UI task refresh */ }
            try { RefreshMenuChecks(); } catch { /* intentionally ignored: UI checks refresh */ }
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
            catch { /* intentionally ignored: UI refresh during shutdown */ }
        }

        public static void ShowBalloonPublic(string text)
        {
            try
            {
                var app = Current as App;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() => app.ShowBalloon(text)));
            }
            catch { /* intentionally ignored: dispatcher shutdown */ }
        }

        private void CreateTrayIcon()
        {
            _trayMenu = new TrayContextMenu(
                _configManager,
                Services,
                ToggleFloatingPanel,
                ReloadConfig,
                UpdateTrayText,
                PromptExit);

            _tbIcon = new TaskbarIcon
            {
                Icon = LoadAppIcon(),
                ToolTipText = "CarroDesk",
                ContextMenu = _trayMenu
            };
            _tbIcon.TrayMouseDoubleClick += (s, e) => ToggleFloatingPanel();

            RefreshMenuChecks();
        }

        internal void SetIdleMinutes(int minutes)
        {
            _screenLockModule?.SetIdleMinutes(minutes);
            UpdateTrayText();
            _trayController?.RequestRefresh();
        }

        internal void PauseFor(TimeSpan duration)
        {
            _screenLockModule?.PauseFor(duration);
            UpdateTrayText();
            _trayController?.RequestRefresh();
            ShowBalloon(Loc.T("Tray.BalloonPause", _screenLockModule != null ? _screenLockModule.PauseUntil : DateTime.MinValue));
        }

        internal void ResumeIdle()
        {
            _screenLockModule?.ResumeIdle();
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
            catch { /* intentionally ignored: tray notification tip failure */ }
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
            catch { /* intentionally ignored: tooltip text update */ }
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
            catch { /* intentionally ignored: dispatcher already shutdown */ }
        }

        public static bool IsFatalException(Exception ex)
        {
            if (ex == null) return false;
            return ex is OutOfMemoryException
                || ex is StackOverflowException
                || ex is AccessViolationException
                || ex is AppDomainUnloadedException
                || ex is BadImageFormatException
                || ex is CannotUnloadAppDomainException
                || ex is InvalidProgramException
                || ex is System.Threading.ThreadAbortException;
        }

        private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            LogError(ex);

            if (IsFatalException(ex))
            {
                try
                {
                    var path = Path.Combine(ConfigService.DirPath, "fatal.log");
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] {ex}{Environment.NewLine}");
                }
                catch { /* intentionally ignored: emergency disk log error during crash */ }
                Environment.FailFast("Fatal unhandled exception encountered in CarroDesk.", ex);
                return;
            }

            try
            {
                ShowBalloonPublic("操作发生异常，详情请查看日志");
            }
            catch { /* intentionally ignored: non-fatal balloon tip display failure */ }

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
            catch { /* intentionally ignored: file write failure during emergency logging */ }
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            IsShuttingDown = true;
            try { Modules?.StopAll(); } catch { /* intentionally ignored: best-effort stop during exit */ }
            try { Modules?.Dispose(); } catch { /* intentionally ignored: best-effort module dispose */ }
            try { Services?.Dispose(); } catch { /* intentionally ignored: service container dispose */ }
            try
            {
                if (_tbIcon != null)
                {
                    _tbIcon.Dispose();
                }
            }
            catch { /* intentionally ignored: tray icon cleanup */ }
            try { _mutex?.ReleaseMutex(); } catch { /* intentionally ignored: single instance mutex release */ }
        }
    }
}
