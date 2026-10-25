using System;
using System.ComponentModel;

namespace CarroDesk.Modules.MonitorProfile.Models
{
    public class MonitorTimeSetting : INotifyPropertyChanged
    {
        private string _time = "08:00";
        private int _brightness = 60;
        private int _contrast = 70;

        public string Time
        {
            get => _time;
            set { _time = value; OnPropertyChanged(nameof(Time)); }
        }

        public int Brightness
        {
            get => _brightness;
            set { _brightness = value; OnPropertyChanged(nameof(Brightness)); }
        }

        public int Contrast
        {
            get => _contrast;
            set { _contrast = value; OnPropertyChanged(nameof(Contrast)); }
        }

        public TimeSpan ToTimeSpan()
        {
            // 严格 HH:mm[:ss] 解析（MP-2）：TimeSpan.TryParse 会把 "1830" 解析为 1830 天，
            // 该条目从此永不满足 now >= ToTimeSpan()，并在跨午夜回退时被误选为兜底档。
            // 解析失败时按 Zero 处理（当天 0 点起生效），保持旧行为兜底。
            if (DateTime.TryParseExact(
                    (Time ?? "").Trim(),
                    new[] { "HH\\:mm", "HH\\:mm\\:ss" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime parsed))
            {
                return parsed.TimeOfDay;
            }
            return TimeSpan.Zero;
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
