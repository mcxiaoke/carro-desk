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
            else if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase))
            {
                if (typeof(T) == typeof(Modules.MonitorProfile.Models.MonitorProfileConfig))
                {
                    _monitorProfileConfig = string.IsNullOrWhiteSpace(_underlying.MonitorProfileJson)
                        ? Modules.MonitorProfile.Models.MonitorProfileConfig.CreateDefault()
                        : DeserializeMonitorProfile(_underlying.MonitorProfileJson);
                    return _monitorProfileConfig as T;
                }
            }

            return new T();
        }

        private Modules.AudioSwitch.Models.AudioSwitchConfig _audioSwitchConfig;
        private Modules.AppAutoMute.Models.AppAutoMuteConfig _appAutoMuteConfig;
        private Modules.MonitorProfile.Models.MonitorProfileConfig _monitorProfileConfig;

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
            else if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase))
            {
                if (config is Modules.MonitorProfile.Models.MonitorProfileConfig mpc)
                {
                    _monitorProfileConfig = mpc;
                    _underlying.MonitorProfileJson = SerializeMonitorProfile(mpc);
                    _underlying.Save();
                }
            }
        }

        private static string SerializeMonitorProfile(Modules.MonitorProfile.Models.MonitorProfileConfig c)
        {
            var o = new JSONObject();
            o["Enabled"] = c.Enabled;
            o["AutoSchedule"] = c.AutoSchedule;
            o["ActiveProfile"] = c.ActiveProfile ?? "Daily";
            o["BrightnessStep"] = c.BrightnessStep;

            var hk = new JSONObject();
            if (c.Hotkeys != null)
            {
                hk["SwitchToDailyMode"] = c.Hotkeys.SwitchToDailyMode ?? "";
                hk["SwitchToGameMode"] = c.Hotkeys.SwitchToGameMode ?? "";
                hk["SwitchToNightMode"] = c.Hotkeys.SwitchToNightMode ?? "";
                hk["ManualRefresh"] = c.Hotkeys.ManualRefresh ?? "";
                hk["IncreaseBrightness"] = c.Hotkeys.IncreaseBrightness ?? "";
                hk["DecreaseBrightness"] = c.Hotkeys.DecreaseBrightness ?? "";
            }
            o["Hotkeys"] = hk;

            var profilesObj = new JSONObject();
            if (c.Profiles != null)
            {
                foreach (var kvp in c.Profiles)
                {
                    var arr = new JSONArray();
                    if (kvp.Value != null)
                    {
                        foreach (var s in kvp.Value)
                        {
                            if (s != null)
                            {
                                var so = new JSONObject();
                                so["Time"] = s.Time ?? "08:00";
                                so["Brightness"] = s.Brightness;
                                so["Contrast"] = s.Contrast;
                                arr.Add(so);
                            }
                        }
                    }
                    profilesObj[kvp.Key] = arr;
                }
            }
            o["Profiles"] = profilesObj;

            return o.ToString();
        }

        private static Modules.MonitorProfile.Models.MonitorProfileConfig DeserializeMonitorProfile(string json)
        {
            var c = Modules.MonitorProfile.Models.MonitorProfileConfig.CreateDefault();
            try
            {
                var node = JSONNode.Parse(json);
                if (node != null && node.IsObject)
                {
                    var o = node.AsObject;
                    if (o.HasKey("Enabled")) c.Enabled = o["Enabled"].AsBool;
                    if (o.HasKey("AutoSchedule")) c.AutoSchedule = o["AutoSchedule"].AsBool;
                    if (o.HasKey("ActiveProfile")) c.ActiveProfile = o["ActiveProfile"].Value;
                    if (o.HasKey("BrightnessStep")) c.BrightnessStep = o["BrightnessStep"].AsInt;

                    if (o.HasKey("Hotkeys") && o["Hotkeys"].IsObject)
                    {
                        var hk = o["Hotkeys"].AsObject;
                        if (hk.HasKey("SwitchToDailyMode")) c.Hotkeys.SwitchToDailyMode = hk["SwitchToDailyMode"].Value;
                        if (hk.HasKey("SwitchToGameMode")) c.Hotkeys.SwitchToGameMode = hk["SwitchToGameMode"].Value;
                        if (hk.HasKey("SwitchToNightMode")) c.Hotkeys.SwitchToNightMode = hk["SwitchToNightMode"].Value;
                        if (hk.HasKey("ManualRefresh")) c.Hotkeys.ManualRefresh = hk["ManualRefresh"].Value;
                        if (hk.HasKey("IncreaseBrightness")) c.Hotkeys.IncreaseBrightness = hk["IncreaseBrightness"].Value;
                        if (hk.HasKey("DecreaseBrightness")) c.Hotkeys.DecreaseBrightness = hk["DecreaseBrightness"].Value;
                    }

                    if (o.HasKey("Profiles") && o["Profiles"].IsObject)
                    {
                        var profilesObj = o["Profiles"].AsObject;
                        var dict = new Dictionary<string, List<Modules.MonitorProfile.Models.MonitorTimeSetting>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var key in profilesObj.Keys)
                        {
                            var nodeItem = profilesObj[key];
                            if (nodeItem.IsArray)
                            {
                                var list = new List<Modules.MonitorProfile.Models.MonitorTimeSetting>();
                                foreach (JSONNode sNode in nodeItem.AsArray.Children)
                                {
                                    if (sNode.IsObject)
                                    {
                                        var so = sNode.AsObject;
                                        list.Add(new Modules.MonitorProfile.Models.MonitorTimeSetting
                                        {
                                            Time = so.HasKey("Time") ? so["Time"].Value : "08:00",
                                            Brightness = so.HasKey("Brightness") ? so["Brightness"].AsInt : 60,
                                            Contrast = so.HasKey("Contrast") ? so["Contrast"].AsInt : 70
                                        });
                                    }
                                }
                                dict[key] = list;
                            }
                        }
                        if (dict.Count > 0)
                        {
                            c.Profiles = dict;
                        }
                    }
                }
            }
            catch { }
            return c;
        }
    }
}
