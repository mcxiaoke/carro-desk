using System;
using System.IO;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// idle 触发器的冷却/额度补触发语义（设计文档 §7 用例 13-14）。
    /// Fired 回调里同步调用 limiter.TryAcquire，模拟服务层 OnTriggerFired 的统一消耗，
    /// 从而在不开真实进程的前提下覆盖完整链路。
    /// </summary>
    [TestClass]
    public class TaskFireLimitIdleTests
    {
        private sealed class FakeIdle : IIdleService
        {
            public TimeSpan RawIdle { get; set; }
            public bool IsSystemBusyCached { get { return false; } }
            public event Action<TimeSpan> IdleTick;
            public event Action UserActiveDetected;
            public event Action<bool> SystemBusyChanged { add { } remove { } }

            public void Tick(TimeSpan idle) { RawIdle = idle; IdleTick?.Invoke(idle); }
            public void Active() { UserActiveDetected?.Invoke(); }
        }

        private DateTime _utc;
        private DateTime _local;
        private TaskFireLimiter _limiter;
        private int _fired;
        private string _name;

        [TestInitialize]
        public void Init()
        {
            _utc = new DateTime(2026, 11, 14, 2, 0, 0, DateTimeKind.Utc);
            _local = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Local);
            _limiter = new TaskFireLimiter(null, () => _utc, () => _local);
            _fired = 0;
            _name = "IdleLimit_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private TaskDefinition NewTask(int maxRuns, int minIntervalSec)
        {
            return new TaskDefinition
            {
                Name = _name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Idle, AfterMinutes = 5 },
                Action = new TaskAction { File = "x.bat" },
                Options = new TaskOptions { MaxRunsPerDay = maxRuns, MinIntervalSec = minIntervalSec }
            };
        }

        private static FireLimitPolicy PolicyOf(TaskDefinition task)
        {
            return new FireLimitPolicy
            {
                MaxRunsPerDay = task.Options.MaxRunsPerDay,
                MinIntervalSec = task.Options.MinIntervalSec
            };
        }

        private IdleTrigger NewTrigger(TaskDefinition task, FakeIdle idle, bool withLimiter = true)
        {
            var trigger = new IdleTrigger(task, idle, withLimiter ? _limiter : null);
            trigger.Fired += (t, reason) =>
            {
                _fired++;
                string refusal;
                _limiter.TryAcquire(t.Name, PolicyOf(t), reason, out refusal);
            };
            trigger.Start();
            return trigger;
        }

        [TestMethod]
        public void Idle_WithoutLimiter_KeepsLegacyBehavior()
        {
            var idle = new FakeIdle();
            var task = NewTask(0, 0);
            var trigger = new IdleTrigger(task, idle);
            trigger.Fired += (t, reason) => _fired++;
            trigger.Start();

            idle.Tick(TimeSpan.FromMinutes(4));
            Assert.AreEqual(0, _fired, "未达阈值不应触发");
            idle.Tick(TimeSpan.FromMinutes(5));
            Assert.AreEqual(1, _fired);
            idle.Tick(TimeSpan.FromMinutes(30));
            Assert.AreEqual(1, _fired, "持续空闲不得重复触发");
            idle.Active();
            idle.Tick(TimeSpan.FromMinutes(5));
            Assert.AreEqual(2, _fired, "用户活跃后应重新武装");

            trigger.Dispose();
        }

        [TestMethod]
        public void Idle_Cooldown_DefersThenAutoFiresWhenElapsed()
        {
            var idle = new FakeIdle();
            var task = NewTask(0, 1800);
            using (var trigger = NewTrigger(task, idle))
            {
                idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(1, _fired, "首次空闲应触发并消耗配额");

                idle.Active();                       // 用户回座后重新武装
                _utc = _utc.AddMinutes(10);
                idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(1, _fired, "冷却未到不得触发");

                // 关键：被拒期间 _fired 保持 0，冷却结束后无需再次「回座 → 离开」
                _utc = _utc.AddMinutes(21);
                idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(2, _fired, "冷却结束且仍处于空闲应自动补触发");
            }
        }

        [TestMethod]
        public void Idle_DailyQuotaExhausted_DefersUntilNextDay()
        {
            var idle = new FakeIdle();
            var task = NewTask(1, 0);
            using (var trigger = NewTrigger(task, idle))
            {
                idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(1, _fired);

                idle.Active();
                for (int i = 0; i < 5; i++) idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(1, _fired, "当日额度用尽后持续空闲也不得再触发");

                _local = _local.AddDays(1);
                _utc = _utc.AddDays(1);
                idle.Tick(TimeSpan.FromMinutes(5));
                Assert.AreEqual(2, _fired, "跨本地自然日后额度归零，应自动补触发");
            }
        }

        [TestMethod]
        public void Idle_RejectLogIsRateLimited()
        {
            var idle = new FakeIdle();
            var task = NewTask(1, 0);
            string refusal;
            _limiter.TryAcquire(_name, PolicyOf(task), "priming", out refusal);

            using (var trigger = NewTrigger(task, idle))
            {
                for (int i = 0; i < 120; i++) idle.Tick(TimeSpan.FromMinutes(5));
            }

            Assert.AreEqual(0, _fired, "额度用尽后不应触发");

            // 限流拒绝是调度决策，只进 scheduler.log；任务名每次测试唯一，按 task 过滤即可
            int lines = TaskLogTestHelper.CountSchedulerLines(_name, "idle deferred by fire limit");
            Assert.IsTrue(lines <= 2,
                "1s 一 tick 的拒绝必须限频，实际日志 " + lines + " 条: " + TaskLogger.LogPath);
        }
    }
}
