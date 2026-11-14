using System;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>GetNextOccurrence 改为分钟位跳跃后的正确性与耗时回归。</summary>
    [TestClass]
    public class CronOptimizationTests
    {
        [TestMethod]
        public void MinuteMaskJump_FindsExactNextMinute()
        {
            var next = CronHelper.GetNextOccurrence("15 10 * * *", new DateTime(2026, 9, 18, 10, 0, 0));
            Assert.IsNotNull(next);
            Assert.AreEqual(new DateTime(2026, 9, 18, 10, 15, 0), next.Value);
        }

        [TestMethod]
        public void MinuteMaskJump_RollsToNextHourWhenNoMinuteLeft()
        {
            // 5/2 => 5,7,9..59；14:59 之后本小时无匹配分钟，应落到 15:05
            var next = CronHelper.GetNextOccurrence("5/2 * * * *", new DateTime(2026, 9, 18, 14, 59, 30));
            Assert.IsNotNull(next);
            Assert.AreEqual(new DateTime(2026, 9, 18, 15, 5, 0), next.Value);
        }

        [TestMethod]
        public void MinuteMaskJump_StillHandlesLeapDayAndUnreachable()
        {
            var leap = CronHelper.GetNextOccurrence("0 0 29 2 *", new DateTime(2024, 2, 29, 0, 0, 0));
            Assert.IsTrue(leap.HasValue);
            Assert.AreEqual(new DateTime(2028, 2, 29, 0, 0, 0), leap.Value);

            Assert.IsNull(CronHelper.GetNextOccurrence("0 0 30 2 *", new DateTime(2026, 1, 1, 0, 0, 0)));
        }

        [TestMethod]
        public void UnreachableExpression_CompletesQuicklyOnUiThread()
        {
            // 最坏路径：8 年跨度、永不可达表达式。小时级跳跃应把耗时压到毫秒级。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var next = CronHelper.GetNextOccurrence("0 0 30 2 *", new DateTime(2026, 1, 1, 0, 0, 0));
            sw.Stop();

            Assert.IsNull(next);
            Assert.IsTrue(sw.ElapsedMilliseconds < 200,
                "不可达表达式求值耗时 " + sw.ElapsedMilliseconds + "ms，不应阻塞 UI 线程");
        }
    }
}
