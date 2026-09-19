using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class LockWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const uint SWP_NOMOVE = 0x2;
        private const uint SWP_NOSIZE = 0x1;
        private const uint SWP_NOACTIVATE = 0x10;
        private const uint GW_HWNDPREV = 3;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        private readonly ILockService _lockService;
        private readonly ILockAppearance _appearance;
        private readonly bool _primary;
        private readonly CarroDesk.Common.DisplayMonitorInfo _monitor;
        private readonly DispatcherTimer _keepAliveTimer;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _deactivateTimer;
        private DateTime _lastKeepAlive = DateTime.MinValue;
        private DateTime _lastActivateAttempt = DateTime.MinValue;
        private bool _isClosing;

        private readonly Func<bool> _isShuttingDown;
        private bool IsAppShuttingDown => _isShuttingDown != null ? _isShuttingDown() : false;

        public LockWindow(ILockService lockService, ILockAppearance appearance, CarroDesk.Common.DisplayMonitorInfo monitor, bool primary, Func<bool> isShuttingDown = null)
        {
            InitializeComponent();
            _lockService = lockService;
            _appearance = appearance;
            _primary = primary;
            _monitor = monitor;
            _isShuttingDown = isShuttingDown;

            if (monitor != null)
            {
                Left = monitor.Left;
                Top = monitor.Top;
                Width = monitor.Width;
                Height = monitor.Height;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;

            InputPanel.Visibility = primary ? Visibility.Visible : Visibility.Collapsed;
            CoverPanel.Visibility = primary ? Visibility.Collapsed : Visibility.Visible;
            Cursor = primary ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.None;

            bool showClock = _appearance != null && _appearance.ShowClock;
            LargeClockText.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
            DateText.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
            CoverLargeClockText.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
            CoverDateText.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;

            // 修复前 500ms 无条件 SetWindowPos(TOPMOST) 与网速等 TOPMOST 浮层抢 Z 序导致闪烁
            // 改为 2.5s 节流 + 仅当真正被覆盖时才置顶
            _keepAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _keepAliveTimer.Tick += OnKeepAliveTick;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += OnUiTick;

            // Deactivated 抢焦点防抖：400ms 延迟且节流 800ms，避免与浮层焦点抢占造成闪烁
            _deactivateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _deactivateTimer.Tick += OnDeactivateTimerTick;

            Loaded += OnLoaded;
            Deactivated += OnDeactivated;
            Closing += OnClosing;

            PinBox.KeyDown += OnPinKeyDown;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);

            int x = _monitor != null ? _monitor.Left : (int)Left;
            int y = _monitor != null ? _monitor.Top : (int)Top;
            int w = _monitor != null ? _monitor.Width : (int)Width;
            int h = _monitor != null ? _monitor.Height : (int)Height;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);

            // 注册 WndProc 拦截 WM_WINDOWPOSCHANGING 以无闪烁方式维持 TOPMOST
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null)
                source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            if (msg == WM_WINDOWPOSCHANGING && !_isClosing && !IsAppShuttingDown)
            {
                try
                {
                    var pos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                    // 强制保持 TOPMOST 且不激活，避免 SetWindowPos 轮询带来的闪烁
                    // 仅在窗口试图去掉 TOPMOST 时修正，正常 DWM 合成不干预
                    pos.flags &= ~((uint)0x0020); // 去掉 SWP_NOZORDER
                    pos.hwndInsertAfter = HWND_TOPMOST;
                    Marshal.StructureToPtr(pos, lParam, true);
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // 平滑淡入动效
            double targetOpacity = _appearance != null ? _appearance.OverlayOpacity : 0.88;
            targetOpacity = Math.Max(0.1, Math.Min(1.0, targetOpacity));
            var fadeIn = new DoubleAnimation(0, targetOpacity, TimeSpan.FromMilliseconds(200));
            RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _keepAliveTimer.Start();
            _uiTimer.Start();

            if (_primary)
            {
                PinBox.Focus();
                UpdateClock();
                UpdateKeyLockStatus();
            }
            else
            {
                UpdateClock();
            }
        }

        private bool IsAlreadyOnTop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            // GW_HWNDPREV == 0 表示已在 Z 序最顶端（TOPMOST 组内最前），无需再 SetWindowPos
            IntPtr prev = GetWindow(hwnd, GW_HWNDPREV);
            return prev == IntPtr.Zero;
        }

        private void OnKeepAliveTick(object sender, EventArgs e)
        {
            if (IsAppShuttingDown || _isClosing) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;

            // 节流：距离上次置顶 <2s 则跳过，避免与网速浮层等 TOPMOST 窗口高频互抢导致闪烁
            if ((DateTime.Now - _lastKeepAlive).TotalMilliseconds < 2000) return;

            // 已在最前则不做任何操作，彻底消除无条件 SetWindowPos 引发的 DWM 重合成闪烁
            if (IsAlreadyOnTop(hwnd)) return;

            // 额外 guard：若当前前台窗口就是我们，避免重复置顶
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;

            _lastKeepAlive = DateTime.Now;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            UpdateClock();
            if (!_primary) return;
            UpdatePenaltyState();
            UpdateKeyLockStatus();
        }

        private void UpdateClock()
        {
            if (_appearance == null || !_appearance.ShowClock) return;
            var now = DateTime.Now;
            string timeStr = now.ToString("HH:mm:ss");
            string datePattern = Loc.T("Lock.DateFormat");
            string dateStr = now.ToString(datePattern, I18nService.Instance.CurrentCulture);

            if (_primary)
            {
                LargeClockText.Text = timeStr;
                DateText.Text = dateStr;
            }
            else
            {
                CoverLargeClockText.Text = timeStr;
                CoverDateText.Text = dateStr;
            }
        }

        private void UpdateKeyLockStatus()
        {
            if (!_primary || KeyHintText == null) return;
            bool caps = Console.CapsLock;
            bool num = Console.NumberLock;
            if (caps)
            {
                KeyHintText.Text = Loc.T("Lock.CapsLockWarning");
                KeyHintText.Visibility = Visibility.Visible;
            }
            else if (!num)
            {
                KeyHintText.Text = Loc.T("Lock.NumLockWarning");
                KeyHintText.Visibility = Visibility.Visible;
            }
            else
            {
                KeyHintText.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdatePenaltyState()
        {
            var blocked = _lockService != null ? _lockService.GetBlockRemaining() : TimeSpan.Zero;
            bool blockedNow = blocked > TimeSpan.Zero;
            if (blockedNow)
            {
                PinBox.IsEnabled = false;
                UnlockButton.IsEnabled = false;
                MessageText.Text = Loc.T("Lock.PenaltyWait", blocked);
            }
            else if (PinBox.IsEnabled == false)
            {
                PinBox.IsEnabled = true;
                UnlockButton.IsEnabled = true;
                MessageText.Text = "";
                PinBox.Clear();
                PinBox.Focus();
            }
        }

        public void ActivateIfNeeded()
        {
            if (_primary) Activate();
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            if (!_primary || _isClosing || IsAppShuttingDown) return;
            // 防抖：不立即 Activate，延迟 400ms 并节流，避免与 TOPMOST 浮层焦点抢占导致闪烁
            _deactivateTimer.Stop();
            _deactivateTimer.Start();
        }

        private void OnDeactivateTimerTick(object sender, EventArgs e)
        {
            _deactivateTimer.Stop();
            if (_isClosing || IsAppShuttingDown) return;
            if (!_primary) return;
            // 节流 800ms，避免高频 Activate 造成任务栏/浮层闪烁
            if ((DateTime.Now - _lastActivateAttempt).TotalMilliseconds < 800) return;
            // 若我们已是前台或已被 KeepAlive 置顶，则不抢焦点
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd) return;
            if (hwnd != IntPtr.Zero && IsAlreadyOnTop(hwnd) && IsActive) return;

            _lastActivateAttempt = DateTime.Now;
            try { Activate(); } catch { }
            // 重新置顶但不激活，避免闪烁
            try
            {
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void OnPinKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                TryUnlock();
                e.Handled = true;
            }
        }

        private void OnUnlockClick(object sender, RoutedEventArgs e)
        {
            TryUnlock();
        }

        private void TryUnlock()
        {
            string error = null;
            var result = _lockService != null
                ? _lockService.TryUnlock(PinBox.Password, out error)
                : new Func<PinAttemptResult>(() => { error = Loc.T("Lock.IncorrectPin"); return PinAttemptResult.Wrong; })();
            if (result == PinAttemptResult.Success) return;
            MessageText.Text = error;
            ShakeCard();
            PinBox.Clear();
            PinBox.Focus();
        }

        private void OnWindowsLockEscapeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                LockWorkStation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "调用系统锁屏失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShakeCard()
        {
            if (CardTransform == null) return;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.Duration = TimeSpan.FromMilliseconds(320);
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(0)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-12, TimeSpan.FromMilliseconds(60)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(12, TimeSpan.FromMilliseconds(120)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-8, TimeSpan.FromMilliseconds(180)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(8, TimeSpan.FromMilliseconds(240)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(320)));
            CardTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 如果不是通过 CloseSafe 授权解锁关闭且非程序退出，则拦截外部关闭请求
            if (!_isClosing && !IsAppShuttingDown)
            {
                e.Cancel = true;
                return;
            }
            _isClosing = true;
            _keepAliveTimer.Stop();
            _uiTimer.Stop();
            _deactivateTimer.Stop();
        }

        public void CloseSafe()
        {
            try
            {
                _isClosing = true;
                _keepAliveTimer.Stop();
                _uiTimer.Stop();
                _deactivateTimer.Stop();

                var fadeOut = new DoubleAnimation(RootBorder.Opacity, 0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (s, e) =>
                {
                    try { Close(); } catch { }
                };
                RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch
            {
                try { Close(); } catch { }
            }
        }
    }
}
