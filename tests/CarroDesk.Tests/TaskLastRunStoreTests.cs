using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>「最近运行」列的持久化数据源。</summary>
    [TestClass]
    public class TaskLastRunStoreTests
    {
        private string _dir;
        private string _statePath;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "last-run", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _statePath = Path.Combine(_dir, "task-last-run.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [TestMethod]
        public void Record_Get_AndPersistAcrossRestart()
        {
            var when = new DateTime(2026, 11, 14, 20, 30, 45);
            var first = new TaskLastRunStore(_statePath);
            Assert.IsNull(first.Get("t"));
            first.Record("t", when);
            Assert.AreEqual(when, first.Get("t"));

            var second = new TaskLastRunStore(_statePath);
            Assert.AreEqual(when, second.Get("t"), "宿主重启后最近运行时间仍应可读");
        }

        [TestMethod]
        public void ForgetRemoved_CleansOrphans()
        {
            var store = new TaskLastRunStore(_statePath);
            var when = DateTime.Now;
            store.Record("keep", when);
            store.Record("gone", when);

            store.ForgetRemoved(new[] { "keep" });

            Assert.IsTrue(store.Get("keep").HasValue);
            Assert.IsNull(store.Get("gone"));
        }

        [TestMethod]
        public void CorruptFile_FallsBackToMemory()
        {
            File.WriteAllText(_statePath, "{ not valid json");
            var store = new TaskLastRunStore(_statePath);

            Assert.IsNull(store.Get("t"), "损坏文件不得抛异常或产生幻影记录");
            var when = DateTime.Now;
            store.Record("t", when);
            Assert.AreEqual(when, store.Get("t"));
        }

        [TestMethod]
        public void Scheduler_GetLastRunTimes_ReturnsRecordedTime()
        {
            var when = new DateTime(2026, 11, 14, 20, 31, 0);
            TaskConfigService.Save(new List<TaskDefinition>
            {
                new TaskDefinition
                {
                    Name = "LastRunProbe",
                    Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                    Action = new TaskAction { File = "x.bat" }
                }
            });

            var store = new TaskLastRunStore(_statePath);
            var svc = new TaskSchedulerService(
                fireLimiter: new TaskFireLimiter(),
                triggerState: new InMemoryTriggerStateStore(),
                lastRunStore: store);
            svc.Reload();
            store.Record("LastRunProbe", when);

            var times = svc.GetLastRunTimes();
            Assert.IsTrue(times.ContainsKey("LastRunProbe"));
            Assert.AreEqual(when, times["LastRunProbe"]);
        }
    }
}
