using System;
using System.Collections.Generic;

namespace CarroDesk.Modules.MonitorProfile.Models
{
    public class MonitorProfileConfig
    {
        public bool Enabled { get; set; } = true;
        public bool AutoSchedule { get; set; } = true;
        public string ActiveProfile { get; set; } = "Daily";
        public int BrightnessStep { get; set; } = 5;
        public Dictionary<string, List<MonitorTimeSetting>> Profiles { get; set; } = new Dictionary<string, List<MonitorTimeSetting>>(StringComparer.OrdinalIgnoreCase);
        public MonitorHotkeyConfig Hotkeys { get; set; } = new MonitorHotkeyConfig();

        public MonitorProfileConfig Clone()
        {
            var copy = new MonitorProfileConfig
            {
                Enabled = Enabled,
                AutoSchedule = AutoSchedule,
                ActiveProfile = ActiveProfile,
                BrightnessStep = BrightnessStep,
                Hotkeys = Hotkeys != null ? new MonitorHotkeyConfig
                {
                    SwitchToDailyMode = Hotkeys.SwitchToDailyMode,
                    SwitchToGameMode = Hotkeys.SwitchToGameMode,
                    SwitchToNightMode = Hotkeys.SwitchToNightMode,
                    ManualRefresh = Hotkeys.ManualRefresh,
                    IncreaseBrightness = Hotkeys.IncreaseBrightness,
                    DecreaseBrightness = Hotkeys.DecreaseBrightness
                } : new MonitorHotkeyConfig(),
                Profiles = new Dictionary<string, List<MonitorTimeSetting>>(StringComparer.OrdinalIgnoreCase)
            };
            if (Profiles != null)
            {
                foreach (var pair in Profiles)
                    copy.Profiles[pair.Key] = pair.Value != null ? new List<MonitorTimeSetting>(pair.Value) : new List<MonitorTimeSetting>();
            }
            return copy;
        }

        public static MonitorProfileConfig CreateDefault()
        {
            var cfg = new MonitorProfileConfig
            {
                Enabled = true,
                AutoSchedule = true,
                ActiveProfile = "Daily",
                BrightnessStep = 5,
                Hotkeys = new MonitorHotkeyConfig()
            };

            cfg.Profiles["Daily"] = new List<MonitorTimeSetting>
            {
                new MonitorTimeSetting { Time = "07:00", Brightness = 65, Contrast = 75 },
                new MonitorTimeSetting { Time = "18:00", Brightness = 50, Contrast = 70 },
                new MonitorTimeSetting { Time = "22:30", Brightness = 35, Contrast = 65 }
            };

            cfg.Profiles["Game"] = new List<MonitorTimeSetting>
            {
                new MonitorTimeSetting { Time = "07:00", Brightness = 80, Contrast = 80 },
                new MonitorTimeSetting { Time = "19:00", Brightness = 65, Contrast = 75 }
            };

            cfg.Profiles["Night"] = new List<MonitorTimeSetting>
            {
                new MonitorTimeSetting { Time = "00:00", Brightness = 30, Contrast = 60 },
                new MonitorTimeSetting { Time = "21:00", Brightness = 25, Contrast = 55 }
            };

            return cfg;
        }
    }
}
