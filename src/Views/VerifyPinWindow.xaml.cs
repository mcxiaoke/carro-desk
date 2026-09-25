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
        private readonly PinGuard _pinGuard;

        public VerifyPinWindow(IPinService pinService, string title, PinGuard pinGuard = null)
        {
            InitializeComponent();
            _pinService = pinService;
            _pinGuard = pinGuard;
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

            bool verified;
            string error;
            if (_pinGuard != null)
            {
                TimeSpan remaining;
                var result = _pinGuard.Try(PinBox.Password, out remaining);
                verified = result == PinAttemptResult.Success;
                error = result == PinAttemptResult.Blocked
                    ? Loc.T("Lock.PenaltyWait", remaining)
                    : Loc.T("VerifyPin.IncorrectPin");
            }
            else
            {
                verified = _pinService != null && _pinService.Verify(PinBox.Password);
                error = Loc.T("VerifyPin.IncorrectPin");
            }

            if (verified)
            {
                DialogResult = true;
                Close();
                return;
            }
            MessageText.Text = error;
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
