using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>附录 B P0：触发器 last-fired 状态跨 Apply / 宿主重启存活。</summary>
    [TestClass]
    public class TriggerStateStoreTests
    {
        private string _dir;
        private string _statePath;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "trigger-state", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _statePath = Path.Combine(_dir, "task-trigger-state.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static TaskDefinition NewDailyTask(string name)
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "00:00" },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static TaskDefinition NewCronTask(string name)
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Cron, Expr = "* * * * *" },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static void SetScheduledFor(CronTrigger trigger, DateTime value)
        {
            typeof(CronTrigger)
                .GetField("_scheduledFor", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(trigger, (DateTime?)value);
        }

        [TestMethod]
        public void InMemory_SetGet_AndForgetRemoved()
        {
            var store = new InMemoryTriggerStateStore();
            var when = new DateTime(2026, 11, 14, 9, 30, 0);

            Assert.IsNull(store.Get("a", "daily:lastDate"));
            store.Set("a", "daily:lastDate", when);
            Assert.AreEqual(when, store.Get("a", "daily:lastDate"));

            store.Set("b", "cron:lastMinute", when);
            store.ForgetRemoved(new[] { "a" });

            Assert.IsTrue(store.Get("a", "daily:lastDate").HasValue);
            Assert.IsNull(store.Get("b", "cron:lastMinute"), "已删除任务的状态应被清理");
        }

        [TestMethod]
        public void Persistent_SurvivesRestart()
        {
            var when = new DateTime(2026, 11, 14, 9, 30, 0);
            var first = new PersistentTriggerStateStore(_statePath);
            first.Set("t", "daily:lastDate", when);

            var second = new PersistentTriggerStateStore(_statePath);
            Assert.AreEqual(when, second.Get("t", "daily:lastDate"), "宿主重启后必须读回 last-fired");
        }

        [TestMethod]
        public void Persistent_CorruptFile_FallsBackToMemory()
        {
            File.WriteAllText(_statePath, "{ not valid json");
            var store = new PersistentTriggerStateStore(_statePath);

            Assert.IsNull(store.Get("t", "daily:lastDate"), "损坏文件不得抛异常或产生幻影状态");
            var when = DateTime.Today;
            store.Set("t", "daily:lastDate", when);
            Assert.AreEqual(when, store.Get("t", "daily:lastDate"), "降级内存后仍须可读可写");
        }

        [TestMethod]
        public void DailyTrigger_LastFiredSurvivesRecreate()
        {
            var store = new InMemoryTriggerStateStore();
            var task = NewDailyTask("DailyState");
            store.Set("DailyState", "daily:lastDate", DateTime.Today.AddDays(-1));
            int fired = 0;

            var first = new DailyTrigger(task, store);
            first.Fired += (t, r) => fired++;
            first.Start();
            Assert.AreEqual(1, fired, "上次触发是昨天，今天应触发一次");
            first.Dispose();

            var second = new DailyTrigger(task, store);   // 模拟 ApplyCoreLocked 重建触发器
            second.Fired += (t, r) => fired++;
            second.Start();
            Assert.AreEqual(1, fired, "重建后同一天不得重复触发");
            second.Dispose();
        }

        [TestMethod]
        public void CronTrigger_LastFiredMinuteSurvivesRecreate()
        {
            var store = new InMemoryTriggerStateStore();
            var task = NewCronTask("CronState");
            var past = DateTime.Now.AddMinutes(-2);
            var scheduled = new DateTime(past.Year, past.Month, past.Day, past.Hour, past.Minute, 0);
            int fired = 0;

            var first = new CronTrigger(task, store);
            first.Fired += (t, r) => fired++;
            first.Start();
            SetScheduledFor(first, scheduled);
            first.CheckCatchUp();
            Assert.AreEqual(1, fired, "错过原定分钟应补触发一次");
            first.Dispose();

            var second = new CronTrigger(task, store);
            second.Fired += (t, r) => fired++;
            second.Start();
            SetScheduledFor(second, scheduled);
            second.CheckCatchUp();
            Assert.AreEqual(1, fired, "重建后同一分钟不得重复补触发");
            second.Dispose();
        }

        [TestMethod]
        public void Scheduler_ReloadKeepsState_AndForgetRemovedCleansDeleted()
        {
            var store = new InMemoryTriggerStateStore();
            store.Set("ReloadDaily", "daily:lastDate", DateTime.Today);

            TaskConfigService.Save(new List<TaskDefinition> { NewDailyTask("ReloadDaily") });
            var svc = new TaskSchedulerService(fireLimiter: new TaskFireLimiter(), triggerState: store);
            svc.Reload();

            Assert.IsTrue(store.Get("ReloadDaily", "daily:lastDate").HasValue,
                "配置重载不得清空触发器状态");

            TaskConfigService.Save(new List<TaskDefinition>());
            svc.Reload();
            Assert.IsNull(store.Get("ReloadDaily", "daily:lastDate"),
                "任务被删除后其状态条目应被清理");
        }
    }
}
