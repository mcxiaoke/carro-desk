using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

        public TrayContextMenu()
        {
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
                var config = App.Config?.Current;
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
            App.CurrentApp?.ToggleFloatingPanel();
        }

        private void OnAutoStartClick(object sender, RoutedEventArgs e)
        {
            if (App.Config?.Current != null)
            {
                bool newState = !App.Config.Current.AutoStart;
                App.Config.Current.AutoStart = newState;
                AutoStartService.Sync(newState);
                App.Config.Save();
                RefreshHostChecks();
            }
        }

        private void OnConfigEditorClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new ConfigEditorWindow(App.Services?.GetService<IPinService>())
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
            App.CurrentApp?.ReloadConfig();
        }

        private void OnLanguageSelectClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag != null)
            {
                string lang = mi.Tag.ToString();
                if (App.Config != null && App.Config.Current != null)
                {
                    App.Config.Current.Language = lang;
                    App.Config.Save();
                }
                I18nService.Instance.SetLanguage(lang);
                RefreshTray();
                App.CurrentApp?.UpdateTrayText();
            }
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.PromptExit();
        }
    }
}