using System;

namespace CarroDesk.Models
{
    public class AppSettings
    {
        public bool AutoStart { get; set; } = true;
        public string PinSalt { get; set; } = "";
        public string PinHash { get; set; } = "";
        public string Language { get; set; } = "auto";

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
            target.AutoStart = AutoStart;
            target.PinSalt = PinSalt;
            target.PinHash = PinHash;
            target.Language = Language;
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
            if (string.IsNullOrEmpty(loaded.Language)) loaded.Language = def.Language;
            if (string.IsNullOrEmpty(loaded.FloatingPanelHotkey)) loaded.FloatingPanelHotkey = def.FloatingPanelHotkey;
            if (string.IsNullOrEmpty(loaded.FloatingPanelPosition)) loaded.FloatingPanelPosition = def.FloatingPanelPosition;
            return loaded;
        }
    }
}
