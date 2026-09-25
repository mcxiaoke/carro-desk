using System;
using System.Windows;
using CarroDesk.Core;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.ClipboardHistory.Views
{
    /// <summary>
    /// 剪贴板历史模块设置窗口（P2-16 补齐：MaxItems / RetentionDays 此前无 UI 可改）。
    /// 保存统一走 IConfigManager.SaveModuleConfig，随后由模块 OnConfigReloaded 应用。
    /// </summary>
    public partial class ClipboardHistorySettingsWindow : Window
    {
        private readonly ClipboardHistoryModule _module;
        private readonly IConfigManager _configManager;
        private readonly Action<string> _notifier;

        public ClipboardHistorySettingsWindow(
            ClipboardHistoryModule module = null,
            IConfigManager configManager = null,
            Action<string> notifier = null)
        {
            _module = module;
            _configManager = configManager;
            _notifier = notifier;

            InitializeComponent();
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var config = _module?.Config
                         ?? (_configManager != null ? _configManager.GetModuleConfig<ClipboardHistoryConfig>("ClipboardHistory") : new ClipboardHistoryConfig());

            ChkAutoRecord.IsChecked = config.AutoRecord;
            TxtMaxItems.Text = config.MaxItems.ToString();
            TxtRetentionDays.Text = config.RetentionDays.ToString();
            TxtMaxPreviewChars.Text = config.MaxPreviewChars.ToString();
            TxtHotkey.Text = config.Hotkey ?? "Win+Alt+V";
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtMaxItems.Text.Trim(), out int maxItems) || maxItems < 1)
            {
                MessageBox.Show(Loc.T("Clipboard.InvalidMaxItems", "最大保留条数必须是大于 0 的整数。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(TxtRetentionDays.Text.Trim(), out int retentionDays) || retentionDays < 0 || retentionDays > 3650)
            {
                MessageBox.Show(Loc.T("Clipboard.InvalidRetentionDays", "最大保留天数必须是 0 到 3650 之间的整数。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(TxtMaxPreviewChars.Text.Trim(), out int maxPreview) || maxPreview < 1)
            {
                MessageBox.Show(Loc.T("Clipboard.InvalidMaxPreview", "预览最大字符数必须是大于 0 的整数。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var configMgr = _configManager;
            var config = _module?.Config
                         ?? (configMgr != null ? configMgr.GetModuleConfig<ClipboardHistoryConfig>("ClipboardHistory") : new ClipboardHistoryConfig());
            var previous = config.Clone();

            config.AutoRecord = ChkAutoRecord.IsChecked == true;
            config.MaxItems = maxItems;
            config.RetentionDays = retentionDays;
            config.MaxPreviewChars = maxPreview;
            config.Hotkey = TxtHotkey.Text.Trim();

            if (configMgr == null || !configMgr.SaveModuleConfig("ClipboardHistory", config))
            {
                config.AutoRecord = previous.AutoRecord;
                config.MaxItems = previous.MaxItems;
                config.RetentionDays = previous.RetentionDays;
                config.MaxPreviewChars = previous.MaxPreviewChars;
                config.Hotkey = previous.Hotkey;
                MessageBox.Show(this, Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _module?.OnConfigReloaded();
            _module?.LogInfo("剪贴板历史配置已保存并生效");

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}