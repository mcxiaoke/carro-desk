using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services;
using SimpleJSON;

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
                    _audioSwitchConfig = string.IsNullOrWhiteSpace(_underlying.AudioSwitchJson)
                        ? new Modules.AudioSwitch.Models.AudioSwitchConfig()
                        : DeserializeAudioSwitch(_underlying.AudioSwitchJson);
                    return _audioSwitchConfig as T;
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.AppAutoMute.Models.AppAutoMuteConfig))
                {
                    _appAutoMuteConfig = string.IsNullOrWhiteSpace(_underlying.AppAutoMuteJson)
                        ? new Modules.AppAutoMute.Models.AppAutoMuteConfig()
                        : DeserializeAppAutoMute(_underlying.AppAutoMuteJson);
                    return _appAutoMuteConfig as T;
                }
            }

            return new T();
        }

        private Modules.AudioSwitch.Models.AudioSwitchConfig _audioSwitchConfig;
        private Modules.AppAutoMute.Models.AppAutoMuteConfig _appAutoMuteConfig;

        private static string SerializeAudioSwitch(Modules.AudioSwitch.Models.AudioSwitchConfig c)
        {
            var o = new JSONObject();
            o["Enabled"] = c.Enabled;
            o["Hotkey"] = c.Hotkey ?? "";
            o["SpeakerPattern"] = c.SpeakerPattern ?? "";
            o["HeadphonePattern"] = c.HeadphonePattern ?? "";
            o["PlayNotificationSound"] = c.PlayNotificationSound;
            return o.ToString();
        }

        private static Modules.AudioSwitch.Models.AudioSwitchConfig DeserializeAudioSwitch(string json)
        {
            var c = new Modules.AudioSwitch.Models.AudioSwitchConfig();
            try
            {
                var node = JSONNode.Parse(json);
                if (node != null && node.IsObject)
                {
                    var o = node.AsObject;
                    if (o.HasKey("Enabled")) c.Enabled = o["Enabled"].AsBool;
                    if (o.HasKey("Hotkey")) c.Hotkey = o["Hotkey"].Value;
                    if (o.HasKey("SpeakerPattern")) c.SpeakerPattern = o["SpeakerPattern"].Value;
                    if (o.HasKey("HeadphonePattern")) c.HeadphonePattern = o["HeadphonePattern"].Value;
                    if (o.HasKey("PlayNotificationSound")) c.PlayNotificationSound = o["PlayNotificationSound"].AsBool;
                }
            }
            catch { }
            return c;
        }

        private static string SerializeAppAutoMute(Modules.AppAutoMute.Models.AppAutoMuteConfig c)
        {
            var o = new JSONObject();
            o["Enabled"] = c.Enabled;
            o["Hotkey"] = c.Hotkey ?? "";
            o["MuteDelayMs"] = c.MuteDelayMs;
            o["UnmuteDelayMs"] = c.UnmuteDelayMs;
            o["Mode"] = c.Mode ?? "";
            var arr = new JSONArray();
            if (c.TargetApps != null)
            {
                foreach (var t in c.TargetApps)
                {
                    if (t != null) arr.Add(t);
                }
            }
            o["TargetApps"] = arr;
            return o.ToString();
        }

        private static Modules.AppAutoMute.Models.AppAutoMuteConfig DeserializeAppAutoMute(string json)
        {
            var c = new Modules.AppAutoMute.Models.AppAutoMuteConfig();
            try
            {
                var node = JSONNode.Parse(json);
                if (node != null && node.IsObject)
                {
                    var o = node.AsObject;
                    if (o.HasKey("Enabled")) c.Enabled = o["Enabled"].AsBool;
                    if (o.HasKey("Hotkey")) c.Hotkey = o["Hotkey"].Value;
                    if (o.HasKey("MuteDelayMs")) c.MuteDelayMs = o["MuteDelayMs"].AsInt;
                    if (o.HasKey("UnmuteDelayMs")) c.UnmuteDelayMs = o["UnmuteDelayMs"].AsInt;
                    if (o.HasKey("Mode")) c.Mode = o["Mode"].Value;
                    if (o.HasKey("TargetApps") && o["TargetApps"].IsArray)
                    {
                        var list = new List<string>();
                        foreach (JSONNode item in o["TargetApps"].AsArray.Children)
                        {
                            var v = item.Value;
                            if (!string.IsNullOrEmpty(v)) list.Add(v);
                        }
                        c.TargetApps = list;
                    }
                }
            }
            catch { }
            return c;
        }

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
                    _underlying.AudioSwitchJson = SerializeAudioSwitch(asc);
                    _underlying.Save();
                }
            }
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.AppAutoMute.Models.AppAutoMuteConfig aam)
                {
                    _appAutoMuteConfig = aam;
                    _underlying.AppAutoMuteJson = SerializeAppAutoMute(aam);
                    _underlying.Save();
                }
            }
        }
    }
}
