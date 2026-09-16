using System;
using CarroDesk.Core;
using ScreenLock.Models;
using ScreenLock.Services;

namespace CarroDesk.Host.Services
{
    public class ConfigManager : IConfigManager
    {
        private readonly ConfigService _underlying;

        public ConfigManager(ConfigService underlying)
        {
            _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        }

        public ConfigService Underlying => _underlying;
        public AppSettings Current => _underlying.Current;

        public event Action ConfigReloaded;

        public void LoadOrCreate()
        {
            _underlying.LoadOrCreate();
        }

        public void Save()
        {
            _underlying.Save();
        }

        public void Reload()
        {
            _underlying.Reload();
            ConfigReloaded?.Invoke();
        }

        public T GetModuleConfig<T>(string moduleId) where T : class, new()
        {
            // 模块专属强类型配置适配
            if (string.Equals(moduleId, "ScreenLock", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.ScreenLock.Models.ScreenLockConfig))
                {
                    var slc = new Modules.ScreenLock.Models.ScreenLockConfig
                    {
                        IdleMinutes = _underlying.Current.IdleMinutes,
                        PinHash = _underlying.Current.PinHash,
                        PinSalt = _underlying.Current.PinSalt,
                        ShowClock = _underlying.Current.ShowClock,
                        OverlayOpacity = _underlying.Current.OverlayOpacity,
                        ExcludeProcesses = _underlying.Current.ExcludeProcesses,
                        UnlockOnResume = _underlying.Current.UnlockOnResume,
                        Enabled = true
                    };
                    return slc as T;
                }
            }
            else if (string.Equals(moduleId, "TaskScheduler", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.TaskScheduler.Models.TaskSchedulerConfig))
                {
                    var tsc = new Modules.TaskScheduler.Models.TaskSchedulerConfig
                    {
                        GlobalEnabled = _underlying.Current.TasksEnabled,
                        TasksFile = ConfigService.TaskFilePath
                    };
                    return tsc as T;
                }
            }
            else if (string.Equals(moduleId, "AudioSwitch", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.AudioSwitch.Models.AudioSwitchConfig))
                {
                    return (_audioSwitchConfig ?? (_audioSwitchConfig = new Modules.AudioSwitch.Models.AudioSwitchConfig())) as T;
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.AppAutoMute.Models.AppAutoMuteConfig))
                {
                    return (_appAutoMuteConfig ?? (_appAutoMuteConfig = new Modules.AppAutoMute.Models.AppAutoMuteConfig())) as T;
                }
            }

            return new T();
        }

        private static Modules.AudioSwitch.Models.AudioSwitchConfig _audioSwitchConfig;
        private static Modules.AppAutoMute.Models.AppAutoMuteConfig _appAutoMuteConfig;

        public void SaveModuleConfig<T>(string moduleId, T config) where T : class
        {
            if (string.Equals(moduleId, "ScreenLock", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.ScreenLock.Models.ScreenLockConfig slc)
                {
                    _underlying.Current.IdleMinutes = slc.IdleMinutes;
                    _underlying.Current.PinHash = slc.PinHash;
                    _underlying.Current.PinSalt = slc.PinSalt;
                    _underlying.Current.ShowClock = slc.ShowClock;
                    _underlying.Current.OverlayOpacity = slc.OverlayOpacity;
                    _underlying.Current.ExcludeProcesses = slc.ExcludeProcesses;
                    _underlying.Current.UnlockOnResume = slc.UnlockOnResume;
                    _underlying.Save();
                }
            }
            else if (string.Equals(moduleId, "TaskScheduler", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.TaskScheduler.Models.TaskSchedulerConfig tsc)
                {
                    _underlying.Current.TasksEnabled = tsc.GlobalEnabled;
                    _underlying.Save();
                }
            }
            else if (string.Equals(moduleId, "AudioSwitch", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.AudioSwitch.Models.AudioSwitchConfig asc)
                {
                    _audioSwitchConfig = asc;
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.AppAutoMute.Models.AppAutoMuteConfig aam)
                {
                    _appAutoMuteConfig = aam;
                }
            }
        }
    }
}
