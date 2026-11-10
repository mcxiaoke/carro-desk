using System;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    /// <summary>
    /// 宿主全局设置窗口：语言、开机自启、配置目录、PIN、关于与诊断。
    /// 语言与开机自启修改后立即保存并生效；托盘/悬浮面板不再散落这些入口。
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
        private readonly ServiceContainer _services;

        public ConfigEditorWindow(
            IPinService pinService,
            ConfigManager configManager = null,
            Action onApplied = null,
            PinGuard pinGuard = null,
            ServiceContainer services = null)
        {
            InitializeComponent();
            _pinService = pinService;
            _pinGuard = pinGuard;
            _configManager = configManager;
            _onApplied = onApplied;
            _services = services;
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
                MessageBox.Show(Loc.T("Config.LoadFailed", "加载配置失败: {0}", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_isInitializing || _editing == null) return;
            if (!(LanguageBox.SelectedItem is ComboBoxItem item) || item.Tag == null) return;

            string lang = item.Tag.ToString();
            if (string.Equals(_editing.Language ?? "auto", lang, StringComparison.OrdinalIgnoreCase)) return;

            if (_configManager?.Current == null)
            {
                _editing.Language = lang;
                I18nService.Instance.SetLanguage(lang);
                return;
            }

            // 语言即时生效：保存配置 + 切换运行时语言，并通知宿主重载（托盘/浮窗文案同步）。
            HostMenuActions.SetLanguage(_configManager, _services, lang, () =>
            {
                _onApplied?.Invoke();

                var cur = _configManager?.Current;
                if (cur != null) _editing = cur.Clone();

                _isInitializing = true;
                try
                {
                    ApplyEditingToUi();
                    RefreshDynamicTexts();
                }
                finally
                {
                    _isInitializing = false;
                }
                ValidateText.Text = Loc.T("Config.AppliedImmediately", "已立即生效");
            });

            // 保存失败时 HostMenuActions 已回滚配置并提示，把下拉恢复到真实值。
            if (!string.Equals(_configManager.Current.Language ?? "auto", lang, StringComparison.OrdinalIgnoreCase))
            {
                _isInitializing = true;
                try { ApplyEditingToUi(); }
                finally { _isInitializing = false; }
            }
        }

        private void OnAutoStartClick(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _editing == null) return;
            if (_configManager?.Current == null) return;

            bool desired = AutoStartBox.IsChecked == true;
            if (desired == _configManager.Current.AutoStart) return;

            // 开机自启即时生效：注册表与配置同步提交，失败时 HostMenuActions 会提示并回滚。
            HostMenuActions.ToggleAutoStart(_configManager, _services);

            bool actual = _configManager.Current.AutoStart;
            _editing.AutoStart = actual;
            _isInitializing = true;
            try { AutoStartBox.IsChecked = actual; }
            finally { _isInitializing = false; }

            ValidateText.Text = actual == desired ? Loc.T("Config.AppliedImmediately", "已立即生效") : "";
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            HostMenuActions.OpenAboutDialog(_services);
        }

        private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var modules = _services?.GetService<ModuleManager>()?.Modules;
                string report = AppInfo.GetDiagnosticReport(modules);
                Clipboard.SetDataObject(report);
                ValidateText.Text = Loc.T("Msg.DiagnosticsCopied", "诊断信息已复制到剪贴板");
            }
            catch (Exception ex)
            {
                _services?.GetService<ILoggerService>()?.LogError("HostMenu", "复制诊断信息失败", ex);
                ValidateText.Text = Loc.T("Msg.DiagnosticsFailed", "复制诊断信息失败，详见日志");
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
            HostMenuActions.OpenConfigDirectory(_services);
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

                // PIN 是安全凭据，不能像普通设置那样留在草稿里：
                // 旧实现弹"新 PIN 已生成"后关窗即丢，用户以为已换掉泄露的旧 PIN。
                if (!PersistPinImmediately(saltStr, hashStr)) return;

                _editing.PinSalt = saltStr;
                _editing.PinHash = hashStr;
                RefreshDynamicTexts();
                ValidateText.Text = Loc.T("Config.NewPinGenerated");
                MessageBox.Show(Loc.T("Config.NewPinGenerated"), Loc.T("Common.Success"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 只持久化 PIN 两个字段并让 PIN 服务立即重建凭据；语言/自启由各自控件即时提交。
        /// 失败时回滚内存配置并提示，绝不显示"已生效"。
        /// </summary>
        private bool PersistPinImmediately(string salt, string hash)
        {
            var target = _configManager?.Current;
            if (target == null) return false;

            var previous = target.Clone();
            try
            {
                target.PinSalt = salt;
                target.PinHash = hash;
                _configManager.Save();
                _pinService?.SetFromConfig(salt, hash);   // 清 pending、重建校验凭据
                return true;
            }
            catch (Exception ex)
            {
                previous.CopyTo(target);
                try { _configManager.Save(); } catch { }
                MessageBox.Show(Loc.T("Config.SaveFailed", "保存配置失败") + ": " + ex.Message,
                    Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}