using System;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    /// <summary>
    /// 宿主全局设置编辑器：仅承载应用级设置（语言、开机自启、配置目录、PIN）。
    /// P2-16：屏锁 / 任务调度等模块专属设置已拆分到各模块自有设置窗口，
    /// 此处不再 import 任何模块私有 Model，宿主零反向依赖业务模块。
    /// </summary>
    public partial class ConfigEditorWindow : Window
    {
        private AppSettings _editing;
        private bool _isInitializing = false;
        private readonly IPinService _pinService;
        private readonly PinGuard _pinGuard;
        private readonly ConfigManager _configManager;
        private readonly Action _onApplied;

        public ConfigEditorWindow(IPinService pinService, ConfigManager configManager = null, Action onApplied = null, PinGuard pinGuard = null)
        {
            InitializeComponent();
            _pinService = pinService;
            _pinGuard = pinGuard;
            _configManager = configManager;
            _onApplied = onApplied;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            try
            {
                _isInitializing = true;
                var cur = _configManager?.Current ?? new AppSettings();
                _editing = cur.Clone();

                PathText.Text = ConfigService.FilePath;
                RefreshDynamicTexts();
                ApplyEditingToUi();

                ValidateText.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.LoadFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void ApplyEditingToUi()
        {
            string curLang = _editing.Language ?? "auto";
            for (int i = 0; i < LanguageBox.Items.Count; i++)
            {
                if (LanguageBox.Items[i] is ComboBoxItem item && item.Tag != null)
                {
                    if (string.Equals(item.Tag.ToString(), curLang, StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            AutoStartBox.IsChecked = _editing.AutoStart;
        }

        private void RefreshDynamicTexts()
        {
            ModeText.Text = ConfigService.IsPortableMode ? Loc.T("Config.PortableMode") : Loc.T("Config.StandardMode");
            if (_editing != null)
            {
                PinStatusText.Text = _editing.HasPin() ? Loc.T("Config.PinConfigured") + " (" + MaskHash(_editing.PinHash) + ")" : Loc.T("Config.PinNotConfigured");
            }
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string lang = item.Tag.ToString();
                // 语言是草稿字段，只在“保存并应用”成功后切换运行时语言。
                // 旧实现立即 SetLanguage，关闭窗口也不会回滚，导致本次进程与磁盘配置分叉。
                _editing.Language = lang;
                RefreshDynamicTexts();
            }
        }

        private string MaskHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            if (hash.Length <= 8) return "***";
            return hash.Substring(0, 6) + "***";
        }

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = ConfigService.DirPath;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(dir);
            }
            catch { }
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Config.ResetDefaultsConfirm"), Loc.T("Config.ResetDefaultsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var defHost = new AppSettings();
            try
            {
                _isInitializing = true;
                _editing.AutoStart = defHost.AutoStart;
                ApplyEditingToUi();
            }
            finally
            {
                _isInitializing = false;
            }

            // 刻意不在"恢复默认"范围内：
            //   - PIN（安全凭据，清掉会让用户被锁在门外）
            //   - Language（语言偏好，重置会导致界面语言突变）
            ValidateText.Text = Loc.T("Config.ResetDefaultsTooltip");
        }

        private void SyncFromUI()
        {
            if (_editing == null) _editing = new AppSettings();
            _editing.AutoStart = AutoStartBox.IsChecked == true;
            _editing.Language = _editing.Language ?? "auto";
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(true)) return;
            MessageBox.Show(Loc.T("Config.SaveSuccessApplied"), Loc.T("Common.Success"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool DoSave(bool apply)
        {
            SyncFromUI();
            AppSettings previous = null;
            var target = _configManager?.Current;
            if (target != null) previous = target.Clone();

            try
            {
                if (_configManager != null)
                {
                    if (target == null) return false;
                    _editing.CopyTo(target);
                    _editing = target.Clone();
                    _configManager.Save();
                }
                ValidateText.Text = Loc.T("Common.Success");

                if (apply && !ApplyRuntime())
                {
                    MessageBox.Show(Loc.T("Config.ApplyFailed", "配置已保存，但运行时应用失败"), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (target != null && previous != null) previous.CopyTo(target);
                MessageBox.Show(Loc.T("Config.SaveFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool ApplyRuntime()
        {
            // 规范 M9：Config 编辑器不越权直驱模块/Controller。
            // 仅在持久化后向 Host 发一次重载通知，由各模块 OnConfigReloaded 自行应用。
            try
            {
                _onApplied?.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnPinChangeClick(object sender, RoutedEventArgs e)
        {
            // verify old pin first if exists
            if (_editing.HasPin())
            {
                var verify = new VerifyPinWindow(_pinService, Loc.T("Config.VerifyOldPinPrompt"), _pinGuard);
                verify.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                verify.Owner = this;
                if (verify.ShowDialog() != true)
                    return;
            }
            var first = new FirstRunWindow { IsChangeMode = true };
            first.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            first.Owner = this;
            if (first.ShowDialog() == true)
            {
                string newPin = first.NewPin;
                byte[] salt = PinService.GenerateSalt();
                string saltStr = Convert.ToBase64String(salt);
                string hashStr = PinService.ComputeHash(salt, newPin);
                _editing.PinSalt = saltStr;
                _editing.PinHash = hashStr;
                PinStatusText.Text = Loc.T("Config.PinConfigured") + " (" + MaskHash(_editing.PinHash) + ") *" + Loc.T("Config.PinModifiedHint");
                ValidateText.Text = Loc.T("Config.PinModifiedHint");
                MessageBox.Show(Loc.T("Config.NewPinGenerated"), Loc.T("Common.Prompt"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}