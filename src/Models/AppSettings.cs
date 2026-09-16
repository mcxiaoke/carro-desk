using System;
using System.Collections.Generic;

namespace ScreenLock.Models
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
        public List<string> ExcludeProcesses { get; set; } = new List<string>();

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
        }

        public static AppSettings Merge(AppSettings loaded)
        {
            var def = new AppSettings();
            if (loaded == null) return def;
            if (loaded.IdleMinutes < 0 || loaded.IdleMinutes > 24 * 60) loaded.IdleMinutes = def.IdleMinutes;
            if (loaded.OverlayOpacity < 0.3 || loaded.OverlayOpacity > 1.0) loaded.OverlayOpacity = def.OverlayOpacity;
            if (string.IsNullOrEmpty(loaded.Language)) loaded.Language = def.Language;
            return loaded;
        }
    }
}
