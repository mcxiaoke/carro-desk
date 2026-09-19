using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarroDesk.Common;
using CarroDesk.Services;
using CarroDesk.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 锁屏窗口关闭语义的 UI 端到端测试（P0-2 / P1-8）。
    ///
    /// 在 STA 线程上真实创建并显示窗口，验证"解锁一定能把浮层关掉"这一用户可感知的保证：
    /// 原先关窗依赖淡出动画的 Completed 回调，且解锁可能从非 UI 线程调用，
    /// 两者叠加会导致全屏置顶浮层永久残留、用户只能杀进程。
    /// </summary>
    [TestClass]
    public class ScreenLockWindowTests
    {
        private sealed class FakeLockService : ILockService
        {
            public bool IsLocked { get; set; }
            public TimeSpan GetBlockRemaining() { return TimeSpan.Zero; }
            public PinAttemptResult TryUnlock(string pin, out string error)
            {
                error = null;
                return PinAttemptResult.Success;
            }
        }

        private sealed class FakeAppearance : ILockAppearance
        {
            public bool ShowClock { get { return true; } }
            public double OverlayOpacity { get { return 0.88; } }
        }

        private static void RunInSta(Action action)
        {
            TestEnvironment.RunInSta(action);
        }

        private static void Pump()
        {
            TestEnvironment.Pump();
        }

        /// <summary>
        /// 用一个较小的矩形代替真实显示器，避免测试期间弹出全屏置顶窗口。
        /// 注意 LockWindow 会用 WndProc 把窗口钉在"显示器"范围内，
        /// 所以渲染截图时需要传入足够大的尺寸，否则内容会被裁切。
        /// </summary>
        private static DisplayMonitorInfo SmallMonitor(int width = 320, int height = 240)
        {
            return new DisplayMonitorInfo { Left = 0, Top = 0, Width = width, Height = height, IsPrimary = true };
        }

        private static LockWindow NewWindow(DisplayMonitorInfo monitor = null)
        {
            return new LockWindow(new FakeLockService(), new FakeAppearance(), monitor ?? SmallMonitor(), true, () => false);
        }

        private static void WaitUntilClosed(Window win, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && win.IsVisible)
            {
                Pump();
                Thread.Sleep(50);
            }
        }

        /// <summary>读取锁屏根 Border 的当前透明度（入场动画进度）。</summary>
        private static double ContentOpacity(FrameworkElement win)
        {
            var border = win.FindName("RootBorder") as UIElement;
            return border != null ? border.Opacity : 1.0;
        }

        [TestMethod]
        public void LockWindow_CloseSafe_ActuallyClosesWindow()
        {
            RunInSta(() =>
            {
                var win = NewWindow();
                win.Show();
                Pump();
                Assert.IsTrue(win.IsVisible, "锁屏窗口应已显示");

                Assert.IsTrue(win.CloseSafe(), "CloseSafe 应报告已确保关闭");

                // 关窗不得依赖淡出动画回调：给 2 秒上限，兜底定时器也会在此期间生效
                WaitUntilClosed(win, 2000);
                Assert.IsFalse(win.IsVisible, "CloseSafe 之后窗口仍然可见（全屏浮层会永久残留）");
            });
        }

        [TestMethod]
        public void LockWindow_CloseSafe_IsIdempotent()
        {
            RunInSta(() =>
            {
                var win = NewWindow();
                win.Show();
                Pump();

                Assert.IsTrue(win.CloseSafe());
                Assert.IsTrue(win.CloseSafe(), "重复调用 CloseSafe 不应抛异常且仍应报告成功");
                WaitUntilClosed(win, 2000);
                Assert.IsFalse(win.IsVisible);
            });
        }

        [TestMethod]
        public void LockWindow_ForceClose_ClosesEvenWithoutUnlockAuthorization()
        {
            RunInSta(() =>
            {
                var win = NewWindow();
                win.Show();
                Pump();

                // ForceClose 用于锁定失败回滚：必须绕过 OnClosing 的关闭拦截
                Assert.IsTrue(win.ForceClose());
                WaitUntilClosed(win, 2000);
                Assert.IsFalse(win.IsVisible, "回滚路径未能关闭已创建的锁屏窗口");
            });
        }

        [TestMethod]
        public void LockWindow_PlainClose_IsBlockedWhileLocked()
        {
            RunInSta(() =>
            {
                var win = NewWindow();
                win.Show();
                Pump();

                // 未授权（非 CloseSafe/ForceClose、且非程序退出）时外部关闭请求应被拦截，
                // 这是锁屏的防护行为，不能被本次修复破坏。
                win.Close();
                Pump();
                Assert.IsTrue(win.IsVisible, "锁定期间的外部关闭请求应被拦截");

                win.ForceClose();
                WaitUntilClosed(win, 2000);
            });
        }

        [TestMethod]
        public void LockWindow_RendersSnapshot()
        {
            RunInSta(() =>
            {
                var win = NewWindow(SmallMonitor(960, 620));
                win.Show();
                win.ActivateIfNeeded();
                Pump();

                // RootBorder 初始 Opacity=0，由入场动画在 Loaded 后拉满；
                // 必须等动画结束再截图，否则截到的是尚未显形的空壳。
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1500)
                {
                    Pump();
                    Thread.Sleep(50);
                    if (ContentOpacity(win) >= 0.95) break;
                }

                int pxW = (int)Math.Max(1, win.ActualWidth > 0 ? win.ActualWidth : 480);
                int pxH = (int)Math.Max(1, win.ActualHeight > 0 ? win.ActualHeight : 320);                var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);

                // 锁屏窗口是分层窗口（AllowsTransparency），其深色 Background 不会被
                // RenderTargetBitmap 合成，直接渲染会得到"白色文字浮在白底上"的发白空壳。
                // 这里先把内容视觉树合成到深色底上，产出可读的验收截图。
                Visual target = win;
                if (win.AllowsTransparency && win.Content is Visual contentVisual)
                {
                    target = contentVisual;
                }

                var composite = new DrawingVisual();
                using (var dc = composite.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, pxW, pxH));
                    dc.DrawRectangle(new VisualBrush(target), null, new Rect(0, 0, pxW, pxH));
                }
                rtb.Render(composite);

                string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
                string dir = Path.Combine(projectRoot, @"temp\screenshots");
                Directory.CreateDirectory(dir);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, "LockWindow.png")))
                {
                    encoder.Save(fs);
                }

                win.ForceClose();
                WaitUntilClosed(win, 2000);
            });
        }
    }
}
