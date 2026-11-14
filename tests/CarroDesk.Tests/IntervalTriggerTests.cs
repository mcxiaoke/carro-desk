using System;
using System.Collections.Generic;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>附录 B P1：interval 相位跨 Apply 保持、休眠补偿、NextFireAt 可观测。</summary>
    [TestClass]
    public class IntervalTriggerTests
    {
        private static TaskDefinition NewIntervalTask(int sec, string name = "IntervalProbe")
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Interval, EverySec = sec },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static TaskDefinition NewCronTask()
        {
            return new TaskDefinition
            {
                Name = "CronProbe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Cron, Expr = "* * * * *" },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static TaskDefinition NewDailyTask()
        {
            return new TaskDefinition
            {
                Name = "DailyProbe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "00:00" },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        [TestMethod]
        public void IntervalTrigger_NextFireSurvivesRecreate()
        {
            var store = new InMemoryTriggerStateStore();
            var task = NewIntervalTask(3600);

            var first = new IntervalTrigger(task, store);
            first.Start();
            var next = first.NextFireAt;
            Assert.IsTrue(next.HasValue, "应能给出下次触发时刻");
            first.Dispose();

            var second = new IntervalTrigger(task, store);   // 模拟 ApplyCoreLocked 重建
            Assert.AreEqual(next, second.NextFireAt, "相位不得因配置重载重置为 now+interval");
        }

        [TestMethod]
        public void IntervalTrigger_CatchUp_FiresOnce_AndRealigns()
        {
            var store = new InMemoryTriggerStateStore();
            var task = NewIntervalTask(3600);
            store.Set("IntervalProbe", "interval:nextFire", DateTime.Now.AddHours(-5));

            var trigger = new IntervalTrigger(task, store);
            int fired = 0;
            trigger.Fired += (t, r) => fired++;

            trigger.CheckCatchUp();
            Assert.AreEqual(1, fired, "错过多个周期只补一次");
            Assert.IsTrue(trigger.NextFireAt > DateTime.Now, "补跑后应重新对齐到未来");

            trigger.CheckCatchUp();
            Assert.AreEqual(1, fired, "对齐后不得连环补发");
            trigger.Dispose();
        }

        [TestMethod]
        public void CronTrigger_NextFireAt_ExposesScheduledTime()
        {
            var trigger = new CronTrigger(NewCronTask(), new InMemoryTriggerStateStore());
            trigger.Start();
            Assert.IsTrue(trigger.NextFireAt.HasValue, "Cron 应暴露 _scheduledFor");
            trigger.Dispose();
        }

        [TestMethod]
        public void DailyTrigger_NextFireAt_IsNextOccurrence()
        {
            var trigger = new DailyTrigger(NewDailyTask(), new InMemoryTriggerStateStore());
            var next = trigger.NextFireAt;

            Assert.IsTrue(next.HasValue);
            Assert.IsTrue(next.Value > DateTime.Now, "今天的 00:00 已过，应给出明天");
            Assert.AreEqual(0, next.Value.Hour);
            Assert.AreEqual(0, next.Value.Minute);
        }

        [TestMethod]
        public void DefaultInterfaceMember_NextFireAt_ReturnsNullForOtherTriggers()
        {
            ITrigger manual = new ManualTrigger(new TaskDefinition
            {
                Name = "ManualProbe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" }
            });
            Assert.IsNull(manual.NextFireAt, "默认接口成员不得要求 10 个触发器全部改写");
        }

        [TestMethod]
        public void Scheduler_GetNextFireTimes_ReturnsEntries()
        {
            TaskConfigService.Save(new List<TaskDefinition> { NewIntervalTask(3600, "NextFireProbe") });
            var svc = new TaskSchedulerService(
                fireLimiter: new TaskFireLimiter(),
                triggerState: new InMemoryTriggerStateStore());
            svc.Reload();

            var times = svc.GetNextFireTimes();
            Assert.IsTrue(times.ContainsKey("NextFireProbe"));
            Assert.IsTrue(times["NextFireProbe"].HasValue, "interval 任务应给出下次触发时刻");
        }
    }
}
