using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CarroDesk.Core.Models
{
    public class TrayMenuItem : INotifyPropertyChanged
    {
        private string _header;
        private bool _isChecked;
        private bool _isEnabled = true;
        private bool _isVisible = true;
        private string _inputGestureText;

        public string Id { get; set; }
        public bool IsSeparator { get; set; }

        public string Header
        {
            get => _header;
            set { if (_header != value) { _header = value; OnPropertyChanged(); } }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        public string InputGestureText
        {
            get => _inputGestureText;
            set { if (_inputGestureText != value) { _inputGestureText = value; OnPropertyChanged(); } }
        }

        public ICommand Command { get; set; }
        public object CommandParameter { get; set; }
        public Action ClickAction { get; set; }

        public ObservableCollection<TrayMenuItem> Children { get; } = new ObservableCollection<TrayMenuItem>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }

        public static TrayMenuItem Separator()
        {
            return new TrayMenuItem { IsSeparator = true };
        }
    }
}
