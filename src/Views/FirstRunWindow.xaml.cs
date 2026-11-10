using System;
using System.Windows;
using System.Windows.Media.Animation;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class FirstRunWindow : Window
    {
        public string NewPin { get; private set; }
        public bool IsChangeMode { get; set; }

        /// <summary>首次运行选项卡：是否随 Windows 启动（默认不勾选，H6）。</summary>
        public bool StartWithWindows { get { return AutoStartCheck.IsChecked == true; } }

        /// <summary>首次运行选项卡：空闲时是否自动锁屏（默认不勾选，H6）。</summary>
        public bool AutoLockWhenIdle { get { return AutoLockCheck.IsChecked == true; } }

        public FirstRunWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                PinBox1.Focus();
                UpdateKeyLockStatus();
                // 改 PIN 模式不展示首次运行选项，避免覆盖用户既有选择
                if (IsChangeMode)
                {
                    Title = Loc.T("FirstRun.TitleChangePin");
                    HeadingText.Text = Loc.T("FirstRun.HeadingChangePin");
                    OptionsPanel.Visibility = Visibility.Collapsed;
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
            string errorKey;
            if (!ValidatePin(pin1, out errorKey))
            {
                MessageText.Text = Loc.T(errorKey);
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

        /// <summary>
        /// PIN 设置校验（纯函数，供单元测试）。
        ///
        /// 必须限定纯数字：锁屏期间的低级键盘钩子（KeyboardBlocker.IsAllowedKey）只放行
        /// 数字/NumPad/Backspace/Enter/Shift，含字母或符号的 PIN 在锁屏后根本无法输入，
        /// 会把"忘记 PIN"放大为永久锁死（见 docs/CODE-REVIEW-20261025.md S2）。
        /// </summary>
        public static bool ValidatePin(string pin, out string errorKey)
        {
            errorKey = null;
            if (string.IsNullOrEmpty(pin) || pin.Length < 4)
            {
                errorKey = "FirstRun.ErrorMinLength";
                return false;
            }
            for (int i = 0; i < pin.Length; i++)
            {
                if (pin[i] < '0' || pin[i] > '9')
                {
                    errorKey = "FirstRun.ErrorDigitsOnly";
                    return false;
                }
            }
            return true;
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
