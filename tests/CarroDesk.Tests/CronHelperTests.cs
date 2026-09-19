using System;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
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

        [TestMethod]
        public void IsMatch_WhenBothDomAndDowRestricted_UsesOrSemantics()
        {
            // 标准 cron 语义：Day-of-Month 与 Day-of-Week **同时**被限定时取"或"。
            // 原实现恒用"与"，`0 9 1 * 1` 变成只在"1 号且是周一"触发，用户配置静默失效。
            var compiled = CronHelper.Compile("1 9 1 * 1");
            Assert.IsTrue(compiled.DayOfMonthRestricted);
            Assert.IsTrue(compiled.DayOfWeekRestricted);

            // 2026-09-01 是周二：DOM 命中即可触发
            var firstOfMonth = new DateTime(2026, 9, 1, 9, 1, 0);
            Assert.AreEqual(DayOfWeek.Tuesday, firstOfMonth.DayOfWeek);
            Assert.IsTrue(CronHelper.IsMatch(firstOfMonth, "1 9 1 * 1"),
                "每月 1 号应触发（DOM 命中）");

            // 2026-09-07 是周一：DOW 命中即可触发
            var monday = new DateTime(2026, 9, 7, 9, 1, 0);
            Assert.AreEqual(DayOfWeek.Monday, monday.DayOfWeek);
            Assert.IsTrue(CronHelper.IsMatch(monday, "1 9 1 * 1"),
                "每周一应触发（DOW 命中）");

            // 2026-09-02 是周三且非 1 号：两者都不命中才不触发
            var neither = new DateTime(2026, 9, 2, 9, 1, 0);
            Assert.IsFalse(CronHelper.IsMatch(neither, "1 9 1 * 1"));
        }

        [TestMethod]
        public void IsMatch_WhenOnlyOneOfDomOrDowRestricted_UsesAndSemantics()
        {
            var dt = new DateTime(2026, 9, 18, 14, 30, 0); // 周五

            // 仅限定 DOM：DOW 为 * 时掩码全 1，"与"形式自然退化为只判断 DOM
            var domOnly = CronHelper.Compile("30 14 18 9 *");
            Assert.IsTrue(domOnly.DayOfMonthRestricted);
            Assert.IsFalse(domOnly.DayOfWeekRestricted);
            Assert.IsTrue(domOnly.IsMatch(dt));
            Assert.IsFalse(domOnly.IsMatch(new DateTime(2026, 9, 19, 14, 30, 0)));

            // 仅限定 DOW：DOM 为 * 时同理
            var dowOnly = CronHelper.Compile("30 14 * 9 5");
            Assert.IsFalse(dowOnly.DayOfMonthRestricted);
            Assert.IsTrue(dowOnly.DayOfWeekRestricted);
            Assert.IsTrue(dowOnly.IsMatch(dt));
            Assert.IsFalse(dowOnly.IsMatch(new DateTime(2026, 9, 19, 14, 30, 0))); // 周六
        }

        [TestMethod]
        public void GetNextOccurrence_WhenBothDomAndDowRestricted_ReturnsNearestMatch()
        {
            // 从 9/1 00:00 起，`1 9 1 * 1` 的最近触发点应当是当天 09:01（DOM 命中），
            // 而不是要等到"1 号且是周一"的日子。
            var next = CronHelper.GetNextOccurrence("1 9 1 * 1", new DateTime(2026, 9, 1, 0, 0, 0));
            Assert.IsNotNull(next);
            Assert.AreEqual(new DateTime(2026, 9, 1, 9, 1, 0), next.Value);
        }

        [TestMethod]
        public void GetNextOccurrence_UnreachableExpression_ReturnsNull()
        {
            // 2 月 30 日永不发生：合法语法但不可达，应返回 null 而不是无限循环
            var next = CronHelper.GetNextOccurrence("0 0 30 2 *", new DateTime(2026, 1, 1, 0, 0, 0));
            Assert.IsNull(next);
        }

        [TestMethod]
        public void Compile_InvalidExpression_ReturnsNull()
        {
            Assert.IsNull(CronHelper.Compile("not a cron"));
            Assert.IsNull(CronHelper.Compile(""));
            Assert.IsNull(CronHelper.Compile(null));
        }

        [TestMethod]
        public void CronTrigger_UnreachableExpression_DoesNotFireAndDoesNotThrow()
        {
            var trigger = new CarroDesk.Services.Tasks.Triggers.CronTrigger(new TaskDefinition
            {
                Name = "t_cron",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Cron, Expr = "0 0 30 2 *" }
            });

            int fired = 0;
            trigger.Fired += (t, reason) => fired++;

            trigger.Start();
            trigger.CheckCatchUp();
            trigger.Stop();
            trigger.Dispose();

            Assert.AreEqual(0, fired, "不可达表达式不应触发");
        }

        [TestMethod]
        public void GetNextOccurrence_HighPerformance_ExecutesRapidly()
        {
            var dt = new DateTime(2026, 1, 1, 0, 0, 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 500; i++)
            {
                var next = CronHelper.GetNextOccurrence("30 2 15 8 *", dt);
                Assert.IsNotNull(next);
                Assert.AreEqual(8, next.Value.Month);
                Assert.AreEqual(15, next.Value.Day);
                Assert.AreEqual(2, next.Value.Hour);
                Assert.AreEqual(30, next.Value.Minute);
            }
            sw.Stop();
            // 500 次跨大跨度月份跳跃求值耗时应小于 200ms
            Assert.IsTrue(sw.ElapsedMilliseconds < 200, "500 次跳跃式 Cron 预测耗时: " + sw.ElapsedMilliseconds + "ms");
        }
    }
}
