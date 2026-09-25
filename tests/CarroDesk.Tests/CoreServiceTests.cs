using System;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Services;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 热键解析行为测试（P1-12）。此前 HotkeyHelper 完全没有测试覆盖。
    ///
    /// 解析优先使用“+”分隔，同时兼容不含“+”的“-”分隔，因此 `Ctrl+-` 可正确表达。
    /// </summary>
    [TestClass]
    public class HotkeyHelperTests
    {
        [TestMethod]
        public void TryParse_WithModifiers_ProducesExpectedFlagsAndVirtualKey()
        {
            int mods, vk;
            string error;

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+Shift+F9", out mods, out vk, out error), error);
            Assert.AreEqual(0x0002 | 0x0004, mods, "Ctrl+Shift 的修饰位不正确");
            Assert.AreEqual(0x70 + 8, vk, "F9 的虚拟键码应为 0x78");

            Assert.IsTrue(HotkeyHelper.TryParse("Win+Alt+C", out mods, out vk, out error), error);
            Assert.AreEqual(0x0008 | 0x0001, mods);
            Assert.AreEqual('C', vk);

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+V", out mods, out vk, out error), error);
            Assert.AreEqual(0x0002, mods);
            Assert.AreEqual('V', vk);
        }

        [TestMethod]
        public void TryParse_IsCaseInsensitive_AndAcceptsDashSeparator()
        {
            int mods, vk;
            string error;

            Assert.IsTrue(HotkeyHelper.TryParse("ctrl+shift+v", out mods, out vk, out error), error);
            Assert.AreEqual(0x0002 | 0x0004, mods);
            Assert.AreEqual('V', vk);

            // '-' 与 '+' 等价作为分隔符
            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl-Alt-M", out mods, out vk, out error), error);
            Assert.AreEqual(0x0002 | 0x0001, mods);
            Assert.AreEqual('M', vk);

            // meta / windows 均为 Win 的别名
            Assert.IsTrue(HotkeyHelper.TryParse("meta+F1", out mods, out vk, out error), error);
            Assert.AreEqual(0x0008, mods);
            Assert.AreEqual(0x70, vk);
        }

        [TestMethod]
        public void TryParse_NamedAndSymbolKeys_MapToVirtualKeys()
        {
            int mods, vk;
            string error;

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+Space", out mods, out vk, out error), error);
            Assert.AreEqual(0x20, vk);

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+Enter", out mods, out vk, out error), error);
            Assert.AreEqual(0x0D, vk);

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+Esc", out mods, out vk, out error), error);
            Assert.AreEqual(0x1B, vk);

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+`", out mods, out vk, out error), error);
            Assert.AreEqual(0xC0, vk);

            Assert.IsTrue(HotkeyHelper.TryParse("Ctrl+PageUp", out mods, out vk, out error), error);
            Assert.AreEqual(0x21, vk);
        }

        [TestMethod]
        public void TryParse_FunctionKeyRange_IsBounded()
        {
            int mods, vk;
            string error;

            Assert.IsTrue(HotkeyHelper.TryParse("F1", out mods, out vk, out error), error);
            Assert.AreEqual(0x70, vk);

            Assert.IsTrue(HotkeyHelper.TryParse("F24", out mods, out vk, out error), error);
            Assert.AreEqual(0x70 + 23, vk);

            Assert.IsFalse(HotkeyHelper.TryParse("F25", out mods, out vk, out error), "F25 超出支持范围，应失败");
            Assert.IsFalse(HotkeyHelper.TryParse("F0", out mods, out vk, out error), "F0 非法，应失败");
        }

        [TestMethod]
        public void TryParse_InvalidInput_ReturnsFalseWithMessage()
        {
            int mods, vk;
            string error;

            Assert.IsFalse(HotkeyHelper.TryParse(null, out mods, out vk, out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));

            Assert.IsFalse(HotkeyHelper.TryParse("   ", out mods, out vk, out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));

            Assert.IsFalse(HotkeyHelper.TryParse("Ctrl+NotAKey", out mods, out vk, out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));

            Assert.IsFalse(HotkeyHelper.TryParse("Bogus+A", out mods, out vk, out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }

        [TestMethod]
        public void Validate_RejectsBareCharacterButAllowsFunctionKeysAndMinus()
        {
            string error;
            Assert.IsFalse(HotkeyHelper.Validate("A", out error));
            Assert.IsFalse(HotkeyHelper.Validate("Space", out error));
            Assert.IsTrue(HotkeyHelper.Validate("F1", out error), error);
            Assert.IsTrue(HotkeyHelper.Validate("Ctrl+-", out error), error);
        }

        [TestMethod]
        public void Validate_MatchesTryParseOutcome()
        {
            string error;
            Assert.IsTrue(HotkeyHelper.Validate("Ctrl+Shift+F9", out error));
            Assert.IsNull(error);

            Assert.IsFalse(HotkeyHelper.Validate("", out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }
    }

    /// <summary>
    /// 前台窗口追踪的原生回调异常隔离测试（P1-3 / P1-12）。
    ///
    /// SetWinEventHook 的回调由 user32 在安装线程的消息泵内直接调用，
    /// 订阅者异常若逸出回调帧将不被 DispatcherUnhandledException 保证捕获，
    /// 可能直接终止进程——这里通过反射调用私有回调验证隔离确实生效。
    /// </summary>
    [TestClass]
    public class ForegroundTrackerTests
    {
        private static void InvokeWinEvent(ForegroundTracker tracker, IntPtr hwnd)
        {
            var method = typeof(ForegroundTracker).GetMethod(
                "OnWinEvent", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "未找到 OnWinEvent，测试需随实现同步更新");

            // 注意：不能吞掉异常——若隔离失效，这里会以 TargetInvocationException 抛出并被断言捕获
            method.Invoke(tracker, new object[] { IntPtr.Zero, 3u, hwnd, 0, 0, 0u, 0u });
        }

        [TestMethod]
        public void ForegroundChanged_ThrowingSubscriber_DoesNotEscapeNativeCallback()
        {
            using (var tracker = new ForegroundTracker())
            {
                int invoked = 0;
                tracker.ForegroundChanged += (h, name) =>
                {
                    invoked++;
                    throw new InvalidOperationException("订阅者故意抛出");
                };

                // 不抛异常即为通过（异常会从 native 回调帧逸出并可能终止进程）
                InvokeWinEvent(tracker, new IntPtr(1));

                Assert.AreEqual(1, invoked, "订阅者应当被调用");
            }
        }

        [TestMethod]
        public void ForegroundChanged_OneThrowingSubscriber_DoesNotBlockOthers()
        {
            using (var tracker = new ForegroundTracker())
            {
                bool secondCalled = false;
                tracker.ForegroundChanged += (h, name) => { throw new InvalidOperationException("第一个订阅者抛出"); };
                tracker.ForegroundChanged += (h, name) => { secondCalled = true; };

                InvokeWinEvent(tracker, new IntPtr(1));

                Assert.IsTrue(secondCalled, "逐订阅者隔离后，首个订阅者抛出不应影响其它订阅者");
            }
        }

        [TestMethod]
        public void Dispose_IsIdempotent_AndConstructionIsSafe()
        {
            var tracker = new ForegroundTracker();
            tracker.UpdateCurrent();

            // 无 GUI 会话时可能读不到前台进程，因此只在有值时才校验格式
            if (!string.IsNullOrEmpty(tracker.CurrentProcessName))
            {
                StringAssert.EndsWith(tracker.CurrentProcessName, ".exe");
            }

            tracker.Dispose();
            tracker.Dispose(); // 重复释放不得抛异常
        }
    }

    /// <summary>
    /// 锁屏控制器在非 UI 线程与缺依赖场景下的安全性测试（P0-2 / P1-12）。
    ///
    /// 注意：不构造真实 PinService，避免测试期间真的弹出全屏锁屏窗口。
    /// </summary>
    [TestClass]
    public class LockControllerTests
    {
        private sealed class FakePinService : IPinService
        {
            public bool IsConfigured { get; set; }
            public string Salt { get { return ""; } }
            public string Hash { get { return ""; } }
            public bool Verify(string pin) { return false; }
            public void SetNewPin(string pin) { }
            public void SetFromConfig(string salt, string hash) { }
        }

        [TestMethod]
        public void Lock_WithoutConfiguredPin_IsSafeNoOp()
        {
            using (var controller = new LockController(() => null, () => null))
            {
                controller.Lock();
                Assert.IsFalse(controller.IsLocked, "缺少 PIN 服务时不应进入锁定状态");
                Assert.IsFalse(controller.LockSafe(), "缺少 PIN 服务必须报告锁定失败，让闲时状态机重试");
            }
        }

        [TestMethod]
        public void Lock_WithUnconfiguredPin_IsSafeNoOp()
        {
            var pinService = new FakePinService { IsConfigured = false };
            using (var controller = new LockController(() => pinService, () => new ScreenLockConfig()))
            {
                Assert.IsFalse(controller.LockSafe(), "未配置 PIN 时必须报告失败");
                Assert.IsFalse(controller.IsLocked, "未配置 PIN 时不应锁定，否则用户无法解锁");
            }
        }

        [TestMethod]
        public void Unlock_FromPoolThread_DoesNotThrow()
        {
            using (var controller = new LockController(() => null, () => null))
            {
                Exception captured = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        controller.Unlock();
                        controller.Unlock(); // 未锁定时的重复解锁同样应安全
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                });
                thread.Start();
                thread.Join();

                Assert.IsNull(captured, "从非 UI 线程解锁不应抛异常: " + captured);
                Assert.IsFalse(controller.IsLocked);
            }
        }

        [TestMethod]
        public void VerifyForExit_WithoutPinService_ReturnsFalse()
        {
            using (var controller = new LockController(() => null, () => null))
            {
                Assert.IsFalse(controller.VerifyForExit("1234"));
            }
        }

        [TestMethod]
        public void GetBlockRemaining_WhenNotLocked_IsZero()
        {
            using (var controller = new LockController(() => null, () => null))
            {
                Assert.AreEqual(TimeSpan.Zero, controller.GetBlockRemaining());
            }
        }
    }
}
