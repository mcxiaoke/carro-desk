using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services;
using Newtonsoft.Json;

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
                    _audioSwitchConfig = DeserializeConfig<Modules.AudioSwitch.Models.AudioSwitchConfig>(_underlying.AudioSwitchJson);
                    return _audioSwitchConfig as T;
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.AppAutoMute.Models.AppAutoMuteConfig))
                {
                    _appAutoMuteConfig = DeserializeConfig<Modules.AppAutoMute.Models.AppAutoMuteConfig>(_underlying.AppAutoMuteJson);
                    return _appAutoMuteConfig as T;
                }
            }
            else if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.MonitorProfile.Models.MonitorProfileConfig))
                {
                    _monitorProfileConfig = string.IsNullOrWhiteSpace(_underlying.MonitorProfileJson)
                        ? Modules.MonitorProfile.Models.MonitorProfileConfig.CreateDefault()
                        : DeserializeConfig(_underlying.MonitorProfileJson, Modules.MonitorProfile.Models.MonitorProfileConfig.CreateDefault);
                    return _monitorProfileConfig as T;
                }
            }

            return new T();
        }

        private Modules.AudioSwitch.Models.AudioSwitchConfig _audioSwitchConfig;
        private Modules.AppAutoMute.Models.AppAutoMuteConfig _appAutoMuteConfig;
        private Modules.MonitorProfile.Models.MonitorProfileConfig _monitorProfileConfig;

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
                    _underlying.AudioSwitchJson = JsonConvert.SerializeObject(asc, Formatting.None);
                    _underlying.Save();
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.AppAutoMute.Models.AppAutoMuteConfig aam)
                {
                    _appAutoMuteConfig = aam;
                    _underlying.AppAutoMuteJson = JsonConvert.SerializeObject(aam, Formatting.None);
                    _underlying.Save();
                }
            }
            else if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.MonitorProfile.Models.MonitorProfileConfig mpc)
                {
                    _monitorProfileConfig = mpc;
                    _underlying.MonitorProfileJson = JsonConvert.SerializeObject(mpc, Formatting.None);
                    _underlying.Save();
                }
            }
        }

        private static T DeserializeConfig<T>(string json, Func<T> defaultFactory = null) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return defaultFactory != null ? defaultFactory() : new T();
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(json) ?? (defaultFactory != null ? defaultFactory() : new T());
            }
            catch
            {
                return defaultFactory != null ? defaultFactory() : new T();
            }
        }
    }
}
