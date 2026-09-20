using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 闲时服务并发订阅与释放语义测试（P2-7）。
    /// </summary>
    [TestClass]
    public class SystemIdleServiceTests2
    {
        [TestMethod]
        public void ConcurrentSubscriptions_AreNotLost()
        {
            var service = new SystemIdleService();
            try
            {
                const int count = 16;
                var hits = new int[count];
                var threads = new Thread[count];

                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    threads[i] = new Thread(() => service.IdleTick += _ => Interlocked.Increment(ref hits[index]));
                    threads[i].Start();
                }
                foreach (var t in threads) t.Join();

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(8) && hits.Any(h => h == 0))
                {
                    Thread.Sleep(100);
                }

                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(hits[i] > 0,
                        string.Format("第 {0} 个订阅者从未收到回调：并发订阅被非原子的 += 覆盖了", i));
                }
            }
            finally
            {
                service.Dispose();
            }
        }

        [TestMethod]
        public void UnsubscribedHandlers_StopReceivingTicks()
        {
            var service = new SystemIdleService();
            try
            {
                int hits = 0;
                Action<TimeSpan> handler = _ => Interlocked.Increment(ref hits);
                service.IdleTick += handler;

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5) && Volatile.Read(ref hits) == 0) Thread.Sleep(50);
                Assert.IsTrue(hits > 0, "订阅后应至少收到一次 Tick");

                service.IdleTick -= handler;
                int afterUnsubscribe = Volatile.Read(ref hits);
                Thread.Sleep(1600); // 跨过至少一个 1s tick 周期
                Assert.AreEqual(afterUnsubscribe, Volatile.Read(ref hits), "取消订阅后不应再收到 Tick");
            }
            finally
            {
                service.Dispose();
            }
        }

        [TestMethod]
        public void Dispose_IsIdempotent_AndStartAfterDisposeIsSafe()
        {
            var service = new SystemIdleService();
            service.Start();
            service.Dispose();
            service.Dispose();
            service.Start();   // 释放后不得抛异常
            service.Stop();
        }
    }

    /// <summary>
    /// IdleTrigger 的"仅触发一次"语义（P2-8）。
    /// </summary>
    [TestClass]
    public class IdleTriggerTests
    {
        private sealed class FakeIdleService : IIdleService
        {
            public TimeSpan RawIdle { get; set; }
            public bool IsSystemBusyCached { get { return false; } }

            public event Action<TimeSpan> IdleTick;
            public event Action UserActiveDetected;

            // 显式空访问器：本测试不涉及忙碌状态，避免生成未使用的委托字段（CS0067 警告）
            public event Action<bool> SystemBusyChanged { add { } remove { } }

            public void RaiseTick(TimeSpan idle)
            {
                RawIdle = idle;
                IdleTick?.Invoke(idle);
            }

            public void RaiseUserActive()
            {
                UserActiveDetected?.Invoke();
            }
        }

        private static TaskDefinition NewTask(int afterMinutes)
        {
            return new TaskDefinition
            {
                Name = "t_idle",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Idle, AfterMinutes = afterMinutes }
            };
        }

        [TestMethod]
        public void IdleTrigger_FiresOnlyOnce_UntilUserBecomesActive()
        {
            var idle = new FakeIdleService();
            var trigger = new IdleTrigger(NewTask(5), idle);

            int fired = 0;
            trigger.Fired += (t, reason) => Interlocked.Increment(ref fired);
            trigger.Start();

            idle.RaiseTick(TimeSpan.FromMinutes(4));
            Assert.AreEqual(0, fired, "未达阈值不应触发");

            idle.RaiseTick(TimeSpan.FromMinutes(5));
            Assert.AreEqual(1, fired);

            // 持续空闲不应重复触发
            idle.RaiseTick(TimeSpan.FromMinutes(6));
            idle.RaiseTick(TimeSpan.FromMinutes(30));
            Assert.AreEqual(1, fired, "持续空闲期间不得重复触发");

            // 用户回座后重新武装
            idle.RaiseUserActive();
            idle.RaiseTick(TimeSpan.FromMinutes(5));
            Assert.AreEqual(2, fired, "用户活跃后应重新武装并可再次触发");

            trigger.Dispose();
        }

        [TestMethod]
        public void IdleTrigger_FiresOnceUnderConcurrentTicks()
        {
            var idle = new FakeIdleService();
            var trigger = new IdleTrigger(NewTask(1), idle);

            int fired = 0;
            trigger.Fired += (t, reason) => Interlocked.Increment(ref fired);
            trigger.Start();

            // 多线程同时越过阈值：判断并置位必须是单次原子操作
            var threads = new Thread[8];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() => idle.RaiseTick(TimeSpan.FromMinutes(2)));
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();

            Assert.AreEqual(1, fired, "并发越过阈值时应只触发一次");
            trigger.Dispose();
        }
    }

    /// <summary>
    /// 模块状态机迁移合法性（P2-11）。
    /// </summary>
    [TestClass]
    public class ModuleStateMachineTests
    {
        private sealed class DummyModule : ModuleBase<DummyConfig>
        {
            public override string Id => "dummy";
            public override string Name => "Dummy";
            public int StartCount;
            public int StopCount;
            public bool ThrowOnInit;

            public override void Initialize(IModuleContext context)
            {
                if (ThrowOnInit)
                {
                    Status = ModuleStatus.Faulted;
                    throw new InvalidOperationException("init crash");
                }
                base.Initialize(context);
            }

            protected override void OnStart() { StartCount++; }
            protected override void OnStop() { StopCount++; }
        }

        private sealed class DummyConfig
        {
        }

        private sealed class DummyContext : IModuleContext
        {
            public string ModuleId { get { return "dummy"; } }
            public System.Windows.Threading.Dispatcher Dispatcher { get { return null; } }
            public T GetService<T>() where T : class { return null; }
            public void RequestTrayRefresh() { }
            public void ShowNotification(string message, string title = "CarroDesk") { }
        }

        [TestMethod]
        public void Start_BeforeInitialize_IsIgnored()
        {
            var module = new DummyModule();
            module.Start();

            Assert.AreEqual(ModuleStatus.Created, module.Status, "未初始化的模块不得启动");
            Assert.IsFalse(module.IsRunning);
            Assert.AreEqual(0, module.StartCount);
        }

        [TestMethod]
        public void RepeatedInitialize_DoesNotResetRunningState()
        {
            var module = new DummyModule();
            module.Initialize(new DummyContext());
            module.Start();
            Assert.AreEqual(ModuleStatus.Running, module.Status);

            // 重复初始化此前会把 Running 打回 Initialized，使后续状态判断全部失真
            module.Initialize(new DummyContext());

            Assert.AreEqual(ModuleStatus.Running, module.Status, "重复初始化不得重置状态");
            Assert.IsTrue(module.IsRunning);
        }

        [TestMethod]
        public void RepeatedStartAndStop_AreIdempotent()
        {
            var module = new DummyModule();
            module.Initialize(new DummyContext());

            module.Start();
            module.Start();
            Assert.AreEqual(1, module.StartCount, "重复 Start 不得二次执行 OnStart");
            Assert.AreEqual(ModuleStatus.Running, module.Status);

            module.Stop();
            module.Stop();
            Assert.AreEqual(1, module.StopCount, "重复 Stop 不得二次执行 OnStop");

            // 停止后不允许再次启动（需重新 Initialize 才能回到 Initialized）
            module.Start();
            Assert.AreEqual(1, module.StartCount, "已停止的模块不得再启动");
        }

        [TestMethod]
        public void FaultedModule_CannotStart_AndKeepsFaultedAfterStopAll()
        {
            var module = new DummyModule { ThrowOnInit = true };

            try
            {
                module.Initialize(new DummyContext());
                Assert.Fail("初始化异常应当向上抛出，由 ModuleManager 记录");
            }
            catch (InvalidOperationException)
            {
                // 预期
            }

            Assert.AreEqual(ModuleStatus.Faulted, module.Status);

            module.Start();
            Assert.AreEqual(0, module.StartCount, "Faulted 模块不得启动");

            module.Stop();
            Assert.AreEqual(ModuleStatus.Faulted, module.Status, "Faulted 是终端态，不应被 Stop 改写");
        }
    }
}
