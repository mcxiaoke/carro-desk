using System;
using System.Windows;
using System.Windows.Media.Animation;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class VerifyPinWindow : Window
    {
        private readonly IPinService _pinService;

        public VerifyPinWindow(IPinService pinService, string title)
        {
            InitializeComponent();
            _pinService = pinService;
            if (!string.IsNullOrWhiteSpace(title))
                TitleText.Text = title;

            Loaded += (s, e) =>
            {
                PinBox.Focus();
                UpdateKeyLockStatus();
            };

            PinBox.PreviewKeyDown += (s, e) => UpdateKeyLockStatus();
            PinBox.PreviewKeyUp += (s, e) => UpdateKeyLockStatus();
        }

        private void UpdateKeyLockStatus()
        {
            if (KeyHintText == null) return;
            bool caps = Console.CapsLock;
            KeyHintText.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(PinBox.Password))
            {
                MessageText.Text = Loc.T("Lock.InputPinHint");
                ShakeCard();
                PinBox.Focus();
                return;
            }

            if (_pinService != null && _pinService.Verify(PinBox.Password))
            {
                DialogResult = true;
                Close();
                return;
            }
            MessageText.Text = Loc.T("VerifyPin.IncorrectPin");
            ShakeCard();
            PinBox.Clear();
            PinBox.Focus();
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
