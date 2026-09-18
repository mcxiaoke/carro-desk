using System;
using CarroDesk.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class CronHelperTests
    {
        [TestMethod]
        public void Validate_ValidExpressions_ReturnsTrue()
        {
            string[] valids = new string[]
            {
                "* * * * *",
                "*/5 * * * *",
                "0 12 * * *",
                "0 0 1 1 *",
                "0 0 * * 1-5",
                "0 0 * * 0",
                "0 0 * * 7",
                "15,30,45 * * * *",
                "10-20/2 * * * *"
            };

            foreach (var expr in valids)
            {
                string error;
                bool ok = CronHelper.Validate(expr, out error);
                Assert.IsTrue(ok, $"Expected valid: {expr}, got error: {error}");
                Assert.IsNull(error);
            }
        }

        [TestMethod]
        public void Validate_InvalidExpressions_ReturnsFalseWithError()
        {
            string[] invalids = new string[]
            {
                null,
                "",
                "   ",
                "* * * *",        // 4 fields
                "* * * * * *",    // 6 fields
                "60 * * * *",      // minute > 59
                "* 24 * * *",      // hour > 23
                "* * 0 * *",       // dom < 1
                "* * 32 * *",      // dom > 31
                "* * * 0 *",       // mon < 1
                "* * * 13 *",      // mon > 12
                "* * * * 8",       // dow > 7
                "*/0 * * * *",     // step 0
                "5-2 * * * *",     // min > max
                "abc * * * *"      // non-numeric
            };

            foreach (var expr in invalids)
            {
                string error;
                bool ok = CronHelper.Validate(expr, out error);
                Assert.IsFalse(ok, $"Expected invalid: {expr}");
                Assert.IsFalse(string.IsNullOrEmpty(error), $"Expected error message for: {expr}");
            }
        }

        [TestMethod]
        public void IsMatch_MatchesExactAndRanges()
        {
            // 2026-09-18 (Friday) 14:30:00
            var dt = new DateTime(2026, 9, 18, 14, 30, 0);

            // Exact match
            Assert.IsTrue(CronHelper.IsMatch(dt, "30 14 18 9 *"));
            Assert.IsTrue(CronHelper.IsMatch(dt, "30 14 * * 5"));
            Assert.IsFalse(CronHelper.IsMatch(dt, "31 14 18 9 *"));
            Assert.IsFalse(CronHelper.IsMatch(dt, "30 15 18 9 *"));

            // Step match
            Assert.IsTrue(CronHelper.IsMatch(dt, "*/5 * * * *"));
            Assert.IsTrue(CronHelper.IsMatch(dt, "*/15 * * * *"));
            Assert.IsFalse(CronHelper.IsMatch(dt, "*/7 * * * *")); // 30 is not divisible by 7

            // Range match
            Assert.IsTrue(CronHelper.IsMatch(dt, "20-40 14 * * *"));
            Assert.IsFalse(CronHelper.IsMatch(dt, "35-50 14 * * *"));

            // Day of week range (1-5 is Monday to Friday)
            Assert.IsTrue(CronHelper.IsMatch(dt, "* * * * 1-5"));
            Assert.IsFalse(CronHelper.IsMatch(dt, "* * * * 0,6")); // Not weekend
        }

        [TestMethod]
        public void IsMatch_Sunday_SupportsBoth0And7()
        {
            // 2026-09-20 is Sunday
            var sunday = new DateTime(2026, 9, 20, 10, 0, 0);
            Assert.AreEqual(DayOfWeek.Sunday, sunday.DayOfWeek);

            Assert.IsTrue(CronHelper.IsMatch(sunday, "0 10 * * 0"), "Sunday should match dow 0");
            Assert.IsTrue(CronHelper.IsMatch(sunday, "0 10 * * 7"), "Sunday should match dow 7");
            Assert.IsFalse(CronHelper.IsMatch(sunday, "0 10 * * 1"), "Sunday should not match dow 1");
        }

        [TestMethod]
        public void GetNextOccurrence_ProjectsFutureTimeCorrectly()
        {
            var baseTime = new DateTime(2026, 9, 18, 10, 0, 0);

            // Next 15 minutes mark
            var next1 = CronHelper.GetNextOccurrence("15 10 * * *", baseTime);
            Assert.IsNotNull(next1);
            Assert.AreEqual(new DateTime(2026, 9, 18, 10, 15, 0), next1.Value);

            // Next hour mark
            var next2 = CronHelper.GetNextOccurrence("0 11 * * *", new DateTime(2026, 9, 18, 10, 30, 0));
            Assert.IsNotNull(next2);
            Assert.AreEqual(new DateTime(2026, 9, 18, 11, 0, 0), next2.Value);

            // Cross-day next midnight
            var next3 = CronHelper.GetNextOccurrence("0 0 * * *", new DateTime(2026, 9, 18, 23, 50, 0));
            Assert.IsNotNull(next3);
            Assert.AreEqual(new DateTime(2026, 9, 19, 0, 0, 0), next3.Value);

            // Invalid expression returns null
            var nextInvalid = CronHelper.GetNextOccurrence("invalid", baseTime);
            Assert.IsNull(nextInvalid);
        }

        [TestMethod]
        public void ExplainCron_ReturnsDescriptiveChineseText()
        {
            Assert.IsTrue(CronHelper.ExplainCron("* * * * *").Contains("每分钟"));
            Assert.IsTrue(CronHelper.ExplainCron("*/5 * * * *").Contains("每隔 5 分钟"));
            Assert.IsTrue(CronHelper.ExplainCron("0 14 * * *").Contains("14:00"));
            Assert.IsTrue(CronHelper.ExplainCron("30 9 * * 1-5").Contains("工作日"));
            Assert.IsTrue(CronHelper.ExplainCron("0 10 * * 0,6").Contains("周末"));
            Assert.IsFalse(string.IsNullOrEmpty(CronHelper.ExplainCron(null)));
        }
    }
}
