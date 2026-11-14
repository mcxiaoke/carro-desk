using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>附录 B：全局并发实例上限，防 watch 风暴 / 多任务同时触发打满线程池。</summary>
    [TestClass]
    public class GlobalConcurrencyTests
    {
        private static TaskSchedulerService NewService()
        {
            return new TaskSchedulerService(
                fireLimiter: new TaskFireLimiter(),
                triggerState: new InMemoryTriggerStateStore());
        }

        [TestMethod]
        public void TryOccupySlot_StopsAtGlobalLimit()
        {
            var svc = NewService();
            string reason;

            for (int i = 0; i < 20; i++)
                Assert.IsTrue(svc.TryOccupySlotForTest("task" + i, false, out reason),
                    "前 20 个不同任务的槽应放行: " + reason);

            Assert.IsFalse(svc.TryOccupySlotForTest("overflow", false, out reason),
                "第 21 个实例必须被全局上限拦下");
            StringAssert.Contains(reason, "global instance limit");
        }

        [TestMethod]
        public void TryOccupySlot_RejectsSameTaskWhenConcurrencyDisabled()
        {
            var svc = NewService();
            string reason;

            Assert.IsTrue(svc.TryOccupySlotForTest("same", false, out reason));
            Assert.IsFalse(svc.TryOccupySlotForTest("same", false, out reason));
            StringAssert.Contains(reason, "concurrent execution not allowed");
        }

        [TestMethod]
        public void TryOccupySlot_StillEnforcesPerTaskLimit()
        {
            var svc = NewService();
            string reason;

            for (int i = 0; i < 5; i++)
                Assert.IsTrue(svc.TryOccupySlotForTest("busy", true, out reason),
                    "allowConcurrent 时同任务最多 5 个实例: " + reason);

            Assert.IsFalse(svc.TryOccupySlotForTest("busy", true, out reason));
            StringAssert.Contains(reason, "instance limit reached");
        }
    }
}
