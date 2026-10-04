using System;
using System.Windows;
using CarroDesk.Core;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

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
            ChkEncryptStorage.IsChecked = config.EncryptStorage;
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
            string hotkey = TxtHotkey.Text.Trim();
            if (!string.IsNullOrWhiteSpace(hotkey) && !HotkeyHelper.Validate(hotkey, out string hkErr))
            {
                MessageBox.Show(Loc.T("Msg.HotkeyInvalid", "快捷键格式不正确: {0}\n支持格式例如: {1}", hkErr, "Ctrl+`, Alt+F11, Win+Ctrl+A"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            config.Hotkey = hotkey;

            bool wantEncrypt = ChkEncryptStorage.IsChecked == true;

            // 开启加密先确认边界（DPAPI 语义 + 数据风险），用户拒绝则回弹复选框
            if (wantEncrypt && !config.EncryptStorage)
            {
                var confirm = MessageBox.Show(this,
                    Loc.T("Clipboard.EncryptConfirm",
                        "启用后，磁盘上的剪贴板历史文件将使用 Windows DPAPI 加密。\n\n" +
                        "· 日常使用无感：本机当前 Windows 用户启动时自动解密；\n" +
                        "· 历史文件拷到其他电脑或其他用户下无法解密；\n" +
                        "· 管理员重置 Windows 密码后，加密历史将无法恢复。\n\n" +
                        "确定启用加密存储吗？"),
                    Loc.T("Clipboard.EncryptConfirmCaption", "启用加密存储"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    ChkEncryptStorage.IsChecked = false;
                    return;
                }
            }

            // 存储格式转换：先落盘、后存配置。失败则中断保存，其余字段保持不变
            if (wantEncrypt != config.EncryptStorage)
            {
                if (_module == null || !_module.EnableStorageEncryption(wantEncrypt))
                {
                    MessageBox.Show(this,
                        Loc.T("Clipboard.EncryptToggleFailed", "切换加密存储失败，历史文件未改变。请检查磁盘与权限后重试。"),
                        Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                config.EncryptStorage = wantEncrypt;
            }

            if (configMgr == null || !configMgr.SaveModuleConfig("ClipboardHistory", config))
            {
                config.AutoRecord = previous.AutoRecord;
                config.MaxItems = previous.MaxItems;
                config.RetentionDays = previous.RetentionDays;
                config.MaxPreviewChars = previous.MaxPreviewChars;
                config.Hotkey = previous.Hotkey;
                if (config.EncryptStorage != previous.EncryptStorage)
                {
                    // 配置保存失败时把磁盘格式一并回滚，保持配置与磁盘一致
                    _module?.EnableStorageEncryption(previous.EncryptStorage);
                    config.EncryptStorage = previous.EncryptStorage;
                }
                MessageBox.Show(this, Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _module?.OnConfigReloaded();
            _module?.LogInfo("剪贴板历史配置已保存并生效");

            DialogResult = true;
            Close();
        }

        private void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (_module == null) return;

            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "ClipboardHistory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    Filter = Loc.T("Clipboard.ExportFilter", "JSON 文件|*.json|所有文件|*.*"),
                    Title = Loc.T("Clipboard.ExportTitle", "导出剪贴板历史"),
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(this) != true) return;

                int count = _module.ExportHistory(dialog.FileName);
                if (count == 0)
                {
                    MessageBox.Show(this,
                        Loc.T("Clipboard.ExportEmpty", "当前没有历史记录可导出。"),
                        Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                MessageBox.Show(this,
                    Loc.T("Clipboard.ExportSuccess",
                        "已导出 {0} 条记录到：\n{1}\n\n注意：导出文件为明文 JSON，请妥善保管（建议存放于加密云盘或私密目录）。", count, dialog.FileName),
                    Loc.T("Clipboard.ExportDone", "导出完成"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    Loc.T("Clipboard.ExportFailed", "导出失败：{0}", ex.Message),
                    Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}