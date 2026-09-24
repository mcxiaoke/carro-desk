using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Host.Modules;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using CarroDesk.Views;

namespace CarroDesk
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\CarroDesk_SingleInstance_2C7A4F10";

        /// <summary>宿主服务容器。仅供 App 内部装配与回调使用，不再对外暴露静态门面。</summary>
        private static ServiceContainer Services { get; set; }

        public static ModuleManager Modules { get; private set; }

        private ConfigManager _configManager;

        // 说明：原 `App.Config`（配置门面）与公开的 `App.Services` 已删除。
        // 它们曾让 View/模块绕过依赖注入直访宿主静态状态，是"宿主零感知"铁律的主要破坏点；
        // 0918 已完成调用点迁移，此处移除残留声明，避免后续再被误用为捷径。
        // 各 View 现在经构造函数注入 IConfigManager / ServiceContainer。
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static TaskbarIcon _tbIcon;
        private static TrayContextMenu _trayMenu;
        private DynamicTrayController _trayController;
        private int _floatingPanelHotkeyId;

        internal static App CurrentApp => Current as App;
        internal static TrayContextMenu TrayMenu => _trayMenu;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch { /* intentionally ignored: environment directory reset */ }

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

            // 4. 构建核心服务与基础设施，装配模块清单
            var audioService = new AudioService();
            Services.AddSingleton(audioService);
            Services.AddSingleton<IAudioService>(audioService);

            var foregroundTracker = new ForegroundTracker(logger);
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

            // 业务模块清单统一装配（P2-12：App 不再逐个 new 模块）
            ModuleRegistry.RegisterStandardModules(Modules, Services, () => IsShuttingDown);

            // 5. 初始化并启动模块
            Modules.InitializeAll(Services);
            Modules.StartAll();

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
            var config = _configManager?.Current;
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
            var c = _configManager?.Current;
            if (c == null) return;
            I18nService.Instance.SetLanguage(c.Language);
            Modules?.ReloadAll();
            AutoStartService.Sync(c.AutoStart);
            try { ProcessExclusionService.InvalidateCache(); } catch { /* intentionally ignored: cache invalidation */ }
            RegisterFloatingPanelHotkey();
            _trayController?.RequestRefresh();
            UpdateTrayText();
            if (_tbIcon != null)
            {
                // 经 Core 契约读取屏锁状态，宿主不 import 模块私有 Model（P2-12）
                var slStatus = Modules.Modules.OfType<IScreenLockStatus>().FirstOrDefault();
                int idleMins = slStatus?.IdleMinutes ?? 5;
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
                // 线程封送由 ShowBalloon 内部统一处理，此处不再重复 BeginInvoke
                app?.ShowBalloon(text);
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

        internal void RefreshMenuChecks()
        {
            _trayController?.RequestRefresh();
        }

        internal void ShowBalloon(string text)
        {
            // 本方法会被线程池线程调用（任务执行完成、剪贴板落盘、音频服务回调等），
            // 而 TaskbarIcon 是 WPF FrameworkElement，跨线程访问会抛
            // InvalidOperationException 并被下面的 catch 吞掉，表现为"通知无故丢失"。
            // 因此统一在这里回到 UI 线程，调用方无需关心自己身处哪条线程。
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowBalloon(text)));
                }
                catch { /* intentionally ignored: dispatcher already shutting down */ }
                return;
            }

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
            // 退出守卫协商可能逐个等待最长 3 秒，绝不能同步阻塞 UI 线程（托盘点击处），
            // 否则多个守卫叠加会让"点退出"看起来像卡死。改为异步协商，完成后再回到 UI 线程。
            _ = PromptExitAsync();
        }

        private async System.Threading.Tasks.Task PromptExitAsync()
        {
            // 退出守卫协商（规范 §3.5）：任一守卫要求阻止时，展示 Host 持有的挑战 UI
            bool blocked = false;
            foreach (var module in Modules.Modules)
            {
                var guard = module as IExitGuard;
                if (guard == null) continue;

                bool b = false;
                bool ok = await SafeInvoker.RunTimeoutAsync(module.Id, TimeSpan.FromSeconds(3),
                    () => b = guard.RequestBlockExit(), (id, ex) => LogError(ex)).ConfigureAwait(true);

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
                ShowBalloonPublic(Loc.T("Msg.UnexpectedError", "操作发生异常，详情请查看日志"));
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
