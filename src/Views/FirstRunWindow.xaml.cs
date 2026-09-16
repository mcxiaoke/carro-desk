using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace ScreenLock.Views
{
    public partial class FirstRunWindow : Window
    {
        public string NewPin { get; private set; }

        public FirstRunWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                PinBox1.Focus();
                UpdateKeyLockStatus();
                // If title was modified to "设置新 PIN", sync heading text
                if (Title == "设置新 PIN")
                {
                    HeadingText.Text = "修改解锁 PIN";
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
                MessageText.Text = "PIN 至少需要 4 位。";
                ShakeCard();
                PinBox1.Focus();
                return;
            }
            if (pin1 != pin2)
            {
                MessageText.Text = "两次输入不一致，请重新输入。";
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
