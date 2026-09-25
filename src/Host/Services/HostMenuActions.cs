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

            bool oldState = config.AutoStart;
            bool newState = !oldState;
            bool success = AutoStartService.Sync(newState);
            if (success)
            {
                config.AutoStart = newState;
                try
                {
                    configManager.Save();
                }
                catch (Exception ex)
                {
                    // 注册表和配置必须作为一个可观察的一致状态提交。磁盘保存失败时
                    // 恢复旧内存值，并尽力把注册表恢复到旧状态。
                    config.AutoStart = oldState;
                    try
                    {
                        if (!AutoStartService.Sync(oldState))
                            services?.GetService<ILoggerService>()?.LogWarning("HostMenu", "配置保存失败且开机自启注册表回滚失败");
                    }
                    catch { }
                    services?.GetService<ILoggerService>()?.LogError("HostMenu", "保存开机自启配置失败", ex);
                    services?.GetService<INotificationService>()?.Show(Loc.T("Config.SaveFailed"), "CarroDesk");
                    onStateChanged?.Invoke();
                    return false;
                }
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
                var pinGuard = services?.GetService<PinGuard>();
                var win = new ConfigEditorWindow(pinService, configManager, reloadConfig, pinGuard)
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

        public static void SetLanguage(ConfigManager configManager, ServiceContainer services, string lang, Action onLanguageChanged = null)
        {
            if (string.IsNullOrWhiteSpace(lang)) return;
            var config = configManager?.Current;
            if (config != null)
            {
                string oldLang = config.Language;
                config.Language = lang;
                try
                {
                    configManager.Save();
                }
                catch (Exception ex)
                {
                    config.Language = oldLang;
                    services.GetService<ILoggerService>()?.LogError("HostMenu", "保存语言配置失败", ex);
                    services.GetService<INotificationService>()?.Show(Loc.T("Config.SaveFailed"), "CarroDesk");
                    return;
                }
            }
            I18nService.Instance.SetLanguage(lang);
            onLanguageChanged?.Invoke();
        }
    }
}
