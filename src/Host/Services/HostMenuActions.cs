using System;
using System.Diagnostics;
using System.Windows;
using CarroDesk.Core;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Views;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 宿主公共菜单动作汇聚（解耦托盘菜单与浮动面板中的重复行为）
    /// </summary>
    public static class HostMenuActions
    {
        public static bool ToggleAutoStart(ConfigManager configManager, ServiceContainer services, Action onStateChanged = null)
        {
            var config = configManager?.Current;
            if (config == null) return false;

            bool newState = !config.AutoStart;
            bool success = AutoStartService.Sync(newState);
            if (success)
            {
                config.AutoStart = newState;
                configManager.Save();
            }
            else
            {
                services?.GetService<ILoggerService>()?.LogWarning("HostMenu", "切换开机自启失败，可能被安全软件或权限拦截");
                services?.GetService<INotificationService>()?.Show(Loc.T("Tray.AutoStartFailed", "设置开机自启失败，可能被安全软件拦截"), "CarroDesk");
            }
            onStateChanged?.Invoke();
            return success;
        }

        public static void OpenConfigEditor(ServiceContainer services, ConfigManager configManager, Action reloadConfig, Action onClosed = null)
        {
            try
            {
                var pinService = services?.GetService<IPinService>();
                var win = new ConfigEditorWindow(pinService, configManager, reloadConfig)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                win.ShowDialog();
                onClosed?.Invoke();
            }
            catch { }
        }

        public static void OpenConfigDirectory()
        {
            try
            {
                Process.Start(ConfigService.DirPath);
            }
            catch { }
        }

        public static void SetLanguage(ConfigManager configManager, string lang, Action onLanguageChanged = null)
        {
            if (string.IsNullOrWhiteSpace(lang)) return;
            var config = configManager?.Current;
            if (config != null)
            {
                config.Language = lang;
                configManager.Save();
            }
            I18nService.Instance.SetLanguage(lang);
            onLanguageChanged?.Invoke();
        }
    }
}
