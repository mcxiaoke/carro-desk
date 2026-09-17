using System;

namespace CarroDesk.Modules.MonitorProfile.Models
{
    public class MonitorTimeSetting
    {
        public string Time { get; set; } = "08:00";
        public int Brightness { get; set; } = 60;
        public int Contrast { get; set; } = 70;

        public TimeSpan ToTimeSpan()
        {
            TimeSpan ts;
            return TimeSpan.TryParse(Time, out ts) ? ts : TimeSpan.Zero;
        }

        public MonitorTimeSetting Clone()
        {
            return new MonitorTimeSetting
            {
                Time = this.Time,
                Brightness = this.Brightness,
                Contrast = this.Contrast
            };
        }
    }
}
