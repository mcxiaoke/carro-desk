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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
