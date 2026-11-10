using System;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Common;
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
        private readonly Action _promptExit;

        public TrayContextMenu(
            ConfigManager configManager = null,
            ServiceContainer services = null,
            Action toggleFloatingPanel = null,
            Action reloadConfig = null,
            Action promptExit = null)
        {
            _configManager = configManager;
            _services = services;
            _toggleFloatingPanel = toggleFloatingPanel;
            _reloadConfig = reloadConfig;
            _promptExit = promptExit;

            InitializeComponent();
            if (TitleVersionText != null)
            {
                TitleVersionText.Text = AppInfo.DisplayVersion;
            }
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
            RefreshHostState();
            DynamicController?.RefreshNow();
        }

        /// <summary>宿主通用区的动态态（浮动面板热键文案）。</summary>
        private void RefreshHostState()
        {
            try
            {
                var config = _configManager?.Current;
                if (config == null) return;

                // 热键文案跟随配置，避免用户改了组合后托盘仍显示默认值
                if (FloatingPanelItem != null)
                {
                    FloatingPanelItem.InputGestureText = config.FloatingPanelHotkey;
                }
            }
            catch { }
        }

        private void OnFloatingPanelClick(object sender, RoutedEventArgs e)
        {
            _toggleFloatingPanel?.Invoke();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            HostMenuActions.OpenConfigEditor(_services, _configManager, _reloadConfig, RefreshTray);
        }

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            HostMenuActions.OpenConfigDirectory(_services);
        }

        private void OnReloadConfigClick(object sender, RoutedEventArgs e)
        {
            _reloadConfig?.Invoke();
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            _promptExit?.Invoke();
        }
    }
}