using CarroDesk.Host.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class SystemIdleServiceTests
    {
        [TestMethod]
        public void SystemIdleService_QunsConstants_MatchWin32Standard()
        {
            // 验证常量必须与 Win32 QUERY_USER_NOTIFICATION_STATE 枚举严格对齐
            Assert.AreEqual(1, SystemIdleService.QUNS_NOT_PRESENT);
            Assert.AreEqual(2, SystemIdleService.QUNS_BUSY);
            Assert.AreEqual(3, SystemIdleService.QUNS_RUNNING_D3D_FULL_SCREEN);
            Assert.AreEqual(4, SystemIdleService.QUNS_PRESENTATION_MODE);
            Assert.AreEqual(5, SystemIdleService.QUNS_ACCEPTS_NOTIFICATIONS);
            Assert.AreEqual(6, SystemIdleService.QUNS_QUIET_TIME);
            Assert.AreEqual(7, SystemIdleService.QUNS_APP);
        }

        [TestMethod]
        public void SystemIdleService_IsNotificationStateBusy_EvaluatesCorrectly()
        {
            // 1: Not Present -> false
            Assert.IsFalse(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_NOT_PRESENT));

            // 2: Busy -> true
            Assert.IsTrue(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_BUSY));

            // 3: Running D3D Full Screen -> true (全屏游戏判定为繁忙，避免误锁屏)
            Assert.IsTrue(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_RUNNING_D3D_FULL_SCREEN));

            // 4: Presentation Mode -> true (演示模式判定为繁忙)
            Assert.IsTrue(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_PRESENTATION_MODE));

            // 5: Accepts Notifications -> false (标准普通桌面状态，绝不能被判定为忙碌而冻结锁屏)
            Assert.IsFalse(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_ACCEPTS_NOTIFICATIONS));

            // 6: Quiet Time -> false
            Assert.IsFalse(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_QUIET_TIME));

            // 7: Windows Store App -> false
            Assert.IsFalse(SystemIdleService.IsNotificationStateBusy(SystemIdleService.QUNS_APP));
        }
    }
}
