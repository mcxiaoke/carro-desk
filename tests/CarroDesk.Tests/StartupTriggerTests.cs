using System;
using System.Reflection;
using System.Threading;
using CarroDesk.Models;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>附录 B P0：startup 延迟必须以宿主启动时刻为绝对基准，不能随 Apply 重建重新计时。</summary>
    [TestClass]
    public class StartupTriggerTests
    {
        private static TaskDefinition NewTask(int delaySec)
        {
            return new TaskDefinition
            {
                Name = "startup_probe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Startup, DelaySec = delaySec },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        [TestMethod]
        public void StartupTrigger_HostAnchor_DoesNotResetOnRecreate()
        {
            var anchor = DateTime.Now.AddSeconds(-100);
            var task = NewTask(300);

            var first = new StartupTrigger(task, anchor);
            first.Start();
            double d1 = first.LastScheduledDelay.TotalSeconds;
            first.Stop();

            // 模拟 ApplyCoreLocked 重建触发器（配置重载）
            var second = new StartupTrigger(task, anchor);
            second.Start();
            double d2 = second.LastScheduledDelay.TotalSeconds;
            second.Stop();

            Assert.IsTrue(d1 > 190 && d1 < 210, "首次应剩约 200s，实际 " + d1);
            Assert.IsTrue(d2 > 190 && d2 < 210,
                "重建后仍应从原锚点算约 200s，不能回到 300s，实际 " + d2);
        }

        [TestMethod]
        public void StartupTrigger_AnchorAlreadyPassed_FiresImmediately()
        {
            var anchor = DateTime.Now.AddSeconds(-10);
            var trigger = new StartupTrigger(NewTask(5), anchor);
            var signal = new ManualResetEventSlim(false);
            trigger.Fired += (t, reason) => signal.Set();

            trigger.Start();
            try
            {
                Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)),
                    "到期时刻已过时应立即触发，而不是再等一个完整 delaySec");
            }
            finally
            {
                trigger.Stop();
            }
        }

        [TestMethod]
        public void StartupTrigger_NoAnchor_KeepsLegacyDelay()
        {
            var trigger = new StartupTrigger(NewTask(300));
            trigger.Start();
            double delay = trigger.LastScheduledDelay.TotalSeconds;
            trigger.Stop();

            Assert.IsTrue(Math.Abs(delay - 300) < 1,
                "未注入锚点（旧构造/单测）应保持从 Start 起算的语义，实际 " + delay);
        }

        [TestMethod]
        public void ManualTrigger_HasNoDeadTriggerManualMethod()
        {
            Assert.IsNull(typeof(ManualTrigger).GetMethod("TriggerManual",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                "TriggerManual 无调用方且会误导「手动触发入口」的认知，应删除");
        }
    }
}
