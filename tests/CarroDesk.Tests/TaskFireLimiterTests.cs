using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// TaskFireLimiter 核心语义回归（设计文档 §7 用例 1-9）。
    /// 时钟全部注入，保证跨天/间隔/回拨等边界可确定性复现；
    /// 数据目录由 TestEnvironment.AssemblyInitialize 隔离，不污染真实用户目录。
    /// </summary>
    [TestClass]
    public class TaskFireLimiterTests
    {
        private string _dir;
        private string _statePath;
        private DateTime _nowUtc;
        private DateTime _nowLocal;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "fire-limiter", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _statePath = Path.Combine(_dir, "task-fire-state.json");
            _nowUtc = new DateTime(2026, 11, 14, 2, 0, 0, DateTimeKind.Utc);
            _nowLocal = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Local);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private TaskFireLimiter NewLimiter(string statePath = null)
        {
            return new TaskFireLimiter(statePath, () => _nowUtc, () => _nowLocal);
        }

        [TestMethod]
        public void UnlimitedPolicy_DoesNotCreateStateOrFile()
        {
            var limiter = NewLimiter(_statePath);
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", FireLimitPolicy.Unlimited, "idle:5m", out refusal));
            Assert.IsNull(refusal);
            Assert.IsFalse(File.Exists(_statePath), "默认不限流时不得引入任何落盘开销");
            Assert.AreEqual(0, limiter.GetSnapshot("t").Count, "默认不限流时不得创建计数条目");
        }

        [TestMethod]
        public void MaxRunsPerDay_StopsAtLimit()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 3 };
            var limiter = NewLimiter();
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsFalse(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "第 4 次必须被拒");
            StringAssert.Contains(refusal, "daily quota 3/3");
            Assert.AreEqual(3, limiter.GetSnapshot("t").Count, "被拒的请求不得消耗配额");
        }

        [TestMethod]
        public void CountResetsOnLocalDateRollOver()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 2 };
            var limiter = NewLimiter();
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsFalse(limiter.TryAcquire("t", policy, "idle:5m", out refusal));

            _nowLocal = _nowLocal.AddDays(1);
            _nowUtc = _nowUtc.AddDays(1);

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "跨本地自然日后额度应归零");
            Assert.AreEqual(1, limiter.GetSnapshot("t").Count);
        }

        [TestMethod]
        public void MinInterval_EnforcedAtBoundary()
        {
            var policy = new FireLimitPolicy { MinIntervalSec = 1800 };
            var limiter = NewLimiter();
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));

            _nowUtc = _nowUtc.AddSeconds(1799);
            Assert.IsFalse(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "差 1 秒也不得放行");
            StringAssert.Contains(refusal, "min interval 1800s");

            _nowUtc = _nowUtc.AddSeconds(1);
            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "到点即放行");
        }

        [TestMethod]
        public void ClockRollback_ResetsAndAllows()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 2, MinIntervalSec = 3600 };
            var limiter = NewLimiter();
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.AreEqual(1, limiter.GetSnapshot("t").Count);

            _nowUtc = _nowUtc.AddDays(-2);

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal),
                "UTC 回拨不得让最短间隔永久不满足");
            Assert.AreEqual(1, limiter.GetSnapshot("t").Count, "回拨应重置该任务的计数");
        }

        [TestMethod]
        public void ConcurrentTryAcquire_IsAtomic()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 5 };
            var limiter = NewLimiter();
            int passed = 0;

            Parallel.For(0, 8, i =>
            {
                string refusal;
                if (limiter.TryAcquire("t", policy, "idle:5m", out refusal))
                    Interlocked.Increment(ref passed);
            });

            Assert.AreEqual(5, passed, "并发触发不得同时读到同一个 Count 而互相覆盖");
            Assert.AreEqual(5, limiter.GetSnapshot("t").Count);
        }

        [TestMethod]
        public void StateSurvivesRestart()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 3 };
            string refusal;

            var first = NewLimiter(_statePath);
            Assert.IsTrue(first.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsTrue(first.TryAcquire("t", policy, "idle:5m", out refusal));

            var second = NewLimiter(_statePath);
            Assert.AreEqual(2, second.GetSnapshot("t").Count, "重启后必须读回已消耗的额度");
            Assert.IsTrue(second.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsFalse(second.TryAcquire("t", policy, "idle:5m", out refusal), "宿主重启不得绕过每日上限");
        }

        [TestMethod]
        public void CorruptStateFile_FallsBackToMemoryWithoutBlocking()
        {
            File.WriteAllText(_statePath, "{ not valid json");
            var policy = new FireLimitPolicy { MaxRunsPerDay = 2 };
            var limiter = NewLimiter(_statePath);
            string refusal;

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "状态损坏不得阻断任务");
            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsFalse(limiter.TryAcquire("t", policy, "idle:5m", out refusal), "降级内存后限流仍须生效");
        }

        [TestMethod]
        public void ForgetRemovedTasks_RemovesOrphans()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 10 };
            var limiter = NewLimiter();
            string refusal;

            limiter.TryAcquire("keep", policy, "idle:5m", out refusal);
            limiter.TryAcquire("gone", policy, "idle:5m", out refusal);

            limiter.ForgetRemovedTasks(new[] { "keep" });

            Assert.AreEqual(1, limiter.GetSnapshot("keep").Count);
            Assert.AreEqual(0, limiter.GetSnapshot("gone").Count);
        }

        [TestMethod]
        public void CanFire_DoesNotConsumeQuota()
        {
            var policy = new FireLimitPolicy { MaxRunsPerDay = 1 };
            var limiter = NewLimiter();
            string refusal;

            Assert.IsTrue(limiter.CanFire("t", policy, out refusal), "只读判定应可重复调用");
            Assert.IsTrue(limiter.CanFire("t", policy, out refusal), "CanFire 不得消耗配额");
            Assert.AreEqual(0, limiter.GetSnapshot("t").Count);

            Assert.IsTrue(limiter.TryAcquire("t", policy, "idle:5m", out refusal));
            Assert.IsFalse(limiter.CanFire("t", policy, out refusal), "消耗后只读判定也应拒绝");
            StringAssert.Contains(refusal, "daily quota");
        }

        [TestMethod]
        public void IsUserDrivenReason_MatchesPrefixes()
        {
            Assert.IsTrue(TaskFireLimiter.IsUserDrivenReason("manual"));
            Assert.IsTrue(TaskFireLimiter.IsUserDrivenReason("restart"));
            Assert.IsTrue(TaskFireLimiter.IsUserDrivenReason("hotkey:Ctrl+Alt+Q"),
                "HotkeyTrigger 发出的 reason 带后缀，必须用前缀匹配");
            Assert.IsFalse(TaskFireLimiter.IsUserDrivenReason("idle:5m"));
            Assert.IsFalse(TaskFireLimiter.IsUserDrivenReason("interval:60s"));
            Assert.IsFalse(TaskFireLimiter.IsUserDrivenReason("startup:catchup"));
            Assert.IsFalse(TaskFireLimiter.IsUserDrivenReason(null));
        }
    }
}
