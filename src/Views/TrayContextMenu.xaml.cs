using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    /// <summary>
    /// 托盘宿主菜单（规范 §4.1）。
    /// 只承载宿主标题项 + 通用区（开机自启/配置目录/语言/退出）。
    /// 各模块业务项由 <see cref="CarroDesk.Host.Services.DynamicTrayController"/> 动态注入到双锚点之间。
    /// 本类属宿主层，不含任何业务模块类名与业务概念词。
    /// </summary>
    public partial class TrayContextMenu : ContextMenu
    {
        public DynamicTrayController DynamicController { get; set; }

        // 双锚点 Separator 供 DynamicTrayController 定位插入区间
        public Separator SlotAnchorTop => PluginSlotAnchorTop;
        public Separator SlotAnchorBottom => PluginSlotAnchorBottom;

        private readonly ConfigManager _configManager;
        private readonly ServiceContainer _services;
        private readonly Action _toggleFloatingPanel;
        private readonly Action _reloadConfig;
        private readonly Action _updateTrayText;
        private readonly Action _promptExit;

        public TrayContextMenu(
            ConfigManager configManager = null,
            ServiceContainer services = null,
            Action toggleFloatingPanel = null,
            Action reloadConfig = null,
            Action updateTrayText = null,
            Action promptExit = null)
        {
            _configManager = configManager;
            _services = services;
            _toggleFloatingPanel = toggleFloatingPanel;
            _reloadConfig = reloadConfig;
            _updateTrayText = updateTrayText;
            _promptExit = promptExit;

            InitializeComponent();
            Opened += (s, e) =>
            {
                RefreshTray();
            };
            Closed += (s, e) => DynamicController?.NotifyClosed();
        }

        /// <summary>请求动态区重刷（防抖/延迟到 Closed 后由控制器处理）。</summary>
        public void RequestTrayRefresh()
        {
            DynamicController?.RequestRefresh();
        }

        internal void RefreshTray()
        {
            RefreshHostChecks();
            DynamicController?.RefreshNow();
        }

        /// <summary>宿主通用区的勾选态（开机自启/语言）。</summary>
        private void RefreshHostChecks()
        {
            try
            {
                var config = _configManager?.Current;
                if (config == null) return;

                if (AutoStartItem != null)
                {
                    AutoStartItem.IsChecked = config.AutoStart;
                }

                string currentLang = config.Language ?? "auto";
                if (LangAutoItem != null) LangAutoItem.IsChecked = string.Equals(currentLang, "auto", StringComparison.OrdinalIgnoreCase);
                if (LangZhItem != null) LangZhItem.IsChecked = string.Equals(currentLang, "zh-CN", StringComparison.OrdinalIgnoreCase);
                if (LangEnItem != null) LangEnItem.IsChecked = string.Equals(currentLang, "en-US", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        private void OnFloatingPanelClick(object sender, RoutedEventArgs e)
        {
            _toggleFloatingPanel?.Invoke();
        }

        private void OnAutoStartClick(object sender, RoutedEventArgs e)
        {
            var config = _configManager?.Current;
            if (config != null)
            {
                bool newState = !config.AutoStart;
                bool success = AutoStartService.Sync(newState);
                if (success)
                {
                    config.AutoStart = newState;
                    _configManager?.Save();
                }
                else
                {
                    var logger = _services?.GetService<ILoggerService>();
                    logger?.LogWarning("TrayContextMenu", "切换开机自启失败，可能被安全软件或权限拦截");
                    var notif = _services?.GetService<INotificationService>();
                    notif?.Show(Loc.T("Tray.AutoStartFailed", "设置开机自启失败，可能被安全软件拦截"), "CarroDesk");
                }
                RefreshHostChecks();
            }
        }

        private void OnConfigEditorClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var pinService = _services?.GetService<IPinService>();
                var win = new ConfigEditorWindow(pinService, _configManager, _reloadConfig)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                win.ShowDialog();
                RefreshTray();
            }
            catch { }
        }

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(ConfigService.DirPath);
            }
            catch { }
        }

        private void OnReloadConfigClick(object sender, RoutedEventArgs e)
        {
            _reloadConfig?.Invoke();
        }

        private void OnLanguageSelectClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag != null)
            {
                string lang = mi.Tag.ToString();
                var config = _configManager?.Current;
                if (config != null)
                {
                    config.Language = lang;
                    _configManager?.Save();
                }
                I18nService.Instance.SetLanguage(lang);
                RefreshTray();
                _updateTrayText?.Invoke();
            }
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            _promptExit?.Invoke();
        }
    }
}