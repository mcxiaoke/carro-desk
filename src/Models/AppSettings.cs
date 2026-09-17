using System;
using System.Collections.Generic;
using CarroDesk.Models.Converters;
using Newtonsoft.Json;

namespace CarroDesk.Models
{
    public class AppSettings
    {
        public int IdleMinutes { get; set; } = 5;
        public bool AutoStart { get; set; } = true;
        public bool ShowClock { get; set; } = true;
        public double OverlayOpacity { get; set; } = 0.88;
        public string PinSalt { get; set; } = "";
        public string PinHash { get; set; } = "";
        public bool TasksEnabled { get; set; } = true;
        public bool UnlockOnResume { get; set; } = true;
        public string Language { get; set; } = "auto";

        [JsonConverter(typeof(StringOrStringListConverter))]
        public List<string> ExcludeProcesses { get; set; } = new List<string>();

        // 桌面悬浮控制面板配置
        public string FloatingPanelHotkey { get; set; } = "Win+Alt+C";
        public string FloatingPanelPosition { get; set; } = "Tray"; // "Tray", "Center", "TopRight", "Custom"
        public bool FloatingPanelPinned { get; set; } = false;
        public bool FloatingPanelLocked { get; set; } = false;
        public double FloatingPanelX { get; set; } = -1;
        public double FloatingPanelY { get; set; } = -1;

        public bool HasPin()
        {
            return !string.IsNullOrEmpty(PinHash) && !string.IsNullOrEmpty(PinSalt);
        }

        public AppSettings Clone()
        {
            var c = new AppSettings();
            CopyTo(c);
            return c;
        }

        public void CopyTo(AppSettings target)
        {
            target.IdleMinutes = IdleMinutes;
            target.AutoStart = AutoStart;
            target.ShowClock = ShowClock;
            target.OverlayOpacity = OverlayOpacity;
            target.PinSalt = PinSalt;
            target.PinHash = PinHash;
            target.TasksEnabled = TasksEnabled;
            target.UnlockOnResume = UnlockOnResume;
            target.Language = Language;
            target.ExcludeProcesses = ExcludeProcesses != null ? new List<string>(ExcludeProcesses) : new List<string>();
            target.FloatingPanelHotkey = FloatingPanelHotkey;
            target.FloatingPanelPosition = FloatingPanelPosition;
            target.FloatingPanelPinned = FloatingPanelPinned;
            target.FloatingPanelLocked = FloatingPanelLocked;
            target.FloatingPanelX = FloatingPanelX;
            target.FloatingPanelY = FloatingPanelY;
        }

        public static AppSettings Merge(AppSettings loaded)
        {
            var def = new AppSettings();
            if (loaded == null) return def;
            if (loaded.IdleMinutes < 0 || loaded.IdleMinutes > 24 * 60) loaded.IdleMinutes = def.IdleMinutes;
            if (loaded.OverlayOpacity < 0.3 || loaded.OverlayOpacity > 1.0) loaded.OverlayOpacity = def.OverlayOpacity;
            if (string.IsNullOrEmpty(loaded.Language)) loaded.Language = def.Language;
            if (string.IsNullOrEmpty(loaded.FloatingPanelHotkey)) loaded.FloatingPanelHotkey = def.FloatingPanelHotkey;
            if (string.IsNullOrEmpty(loaded.FloatingPanelPosition)) loaded.FloatingPanelPosition = def.FloatingPanelPosition;
            return loaded;
        }
    }
}
