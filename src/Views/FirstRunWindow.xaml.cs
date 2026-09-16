using System;
using System.Windows;
using System.Windows.Media.Animation;
using ScreenLock.Services.Localization;

namespace ScreenLock.Views
{
    public partial class FirstRunWindow : Window
    {
        public string NewPin { get; private set; }
        public bool IsChangeMode { get; set; }

        public FirstRunWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                PinBox1.Focus();
                UpdateKeyLockStatus();
                if (IsChangeMode)
                {
                    Title = Loc.T("FirstRun.TitleChangePin");
                    HeadingText.Text = Loc.T("FirstRun.HeadingChangePin");
                }
            };

            PinBox1.PreviewKeyDown += (s, e) => UpdateKeyLockStatus();
            PinBox1.PreviewKeyUp += (s, e) => UpdateKeyLockStatus();
            PinBox2.PreviewKeyDown += (s, e) => UpdateKeyLockStatus();
            PinBox2.PreviewKeyUp += (s, e) => UpdateKeyLockStatus();

            PinBox1.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    PinBox2.Focus();
                    e.Handled = true;
                }
            };
            PinBox2.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    OnOkClick(this, null);
                    e.Handled = true;
                }
            };
        }

        private void UpdateKeyLockStatus()
        {
            if (KeyHintText == null) return;
            bool caps = Console.CapsLock;
            KeyHintText.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            var pin1 = PinBox1.Password;
            var pin2 = PinBox2.Password;
            if (string.IsNullOrEmpty(pin1) || pin1.Length < 4)
            {
                MessageText.Text = Loc.T("FirstRun.ErrorMinLength");
                ShakeCard();
                PinBox1.Focus();
                return;
            }
            if (pin1 != pin2)
            {
                MessageText.Text = Loc.T("FirstRun.ErrorMismatch");
                ShakeCard();
                PinBox2.Clear();
                PinBox2.Focus();
                return;
            }
            NewPin = pin1;
            DialogResult = true;
            Close();
        }

        private void ShakeCard()
        {
            if (CardTransform == null) return;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.Duration = TimeSpan.FromMilliseconds(320);
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(0)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-10, TimeSpan.FromMilliseconds(60)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(10, TimeSpan.FromMilliseconds(120)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-6, TimeSpan.FromMilliseconds(180)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(6, TimeSpan.FromMilliseconds(240)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(320)));
            CardTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
