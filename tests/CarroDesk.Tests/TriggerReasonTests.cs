using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>结构化 reason 解析（不改 ITrigger.Fired 事件签名的等价实现）。</summary>
    [TestClass]
    public class TriggerReasonTests
    {
        [TestMethod]
        public void Parse_ClassifiesAllTriggerReasons()
        {
            Assert.AreEqual(TaskTriggerType.Startup, TriggerReason.Parse("startup").Type);
            Assert.AreEqual(TaskTriggerType.Startup, TriggerReason.Parse("startup:catchup").Type);
            Assert.AreEqual(TaskTriggerType.Interval, TriggerReason.Parse("interval:60s").Type);
            Assert.AreEqual(TaskTriggerType.Daily, TriggerReason.Parse("daily:02:30").Type);
            Assert.AreEqual(TaskTriggerType.Cron, TriggerReason.Parse("cron:0 9 * * 1").Type);
            Assert.AreEqual(TaskTriggerType.Cron, TriggerReason.Parse("cron-catchup:0 9 * * 1").Type);
            Assert.AreEqual(TaskTriggerType.Idle, TriggerReason.Parse("idle:5m").Type);
            Assert.AreEqual(TaskTriggerType.Watch, TriggerReason.Parse("watch:created|C:\\a.txt").Type);
            Assert.AreEqual(TaskTriggerType.Hotkey, TriggerReason.Parse("hotkey:Ctrl+Alt+Q").Type);
            Assert.AreEqual(TaskTriggerType.Manual, TriggerReason.Parse("manual").Type);
            Assert.AreEqual(TaskTriggerType.SessionLock, TriggerReason.Parse("sessionLock").Type);
            Assert.AreEqual(TaskTriggerType.SessionUnlock, TriggerReason.Parse("sessionUnlock").Type);
        }

        [TestMethod]
        public void Parse_FlagsStartupDailyAndCatchUp()
        {
            Assert.IsTrue(TriggerReason.Parse("startup").IsStartup);
            Assert.IsTrue(TriggerReason.Parse("startup:runAtStartup").IsCatchUp);
            Assert.IsTrue(TriggerReason.Parse("startup:catchup").IsCatchUp);
            Assert.IsTrue(TriggerReason.Parse("cron-catchup:0 9 * * 1").IsCatchUp);
            Assert.IsTrue(TriggerReason.Parse("daily:02:30").IsDaily);
            Assert.IsFalse(TriggerReason.Parse("daily:02:30").IsStartup);
            Assert.IsFalse(TriggerReason.Parse("idle:5m").IsCatchUp);
        }

        [TestMethod]
        public void IsUserDriven_UsesPrefixesAndFeedsLimiter()
        {
            Assert.IsTrue(TriggerReason.IsUserDriven("manual"));
            Assert.IsTrue(TriggerReason.IsUserDriven("restart"));
            Assert.IsTrue(TriggerReason.IsUserDriven("hotkey:Ctrl+Alt+Q"));
            Assert.IsFalse(TriggerReason.IsUserDriven("idle:5m"));
            Assert.IsFalse(TriggerReason.IsUserDriven("startup:catchup"));
            Assert.IsFalse(TriggerReason.IsUserDriven(null));

            // 限流门与 ExecuteAsync 必须共用同一套规则
            Assert.AreEqual(TriggerReason.IsUserDriven("hotkey:Ctrl+Alt+Q"),
                TaskFireLimiter.IsUserDrivenReason("hotkey:Ctrl+Alt+Q"));
        }

        [TestMethod]
        public void Parse_NullAndEmpty_AreSafe()
        {
            var info = TriggerReason.Parse(null);
            Assert.AreEqual("", info.Raw);
            Assert.AreEqual(TaskTriggerType.Unknown, info.Type);
            Assert.IsFalse(info.IsUserDriven || info.IsCatchUp || info.IsStartup || info.IsDaily);
            Assert.AreEqual(TaskTriggerType.Unknown, TriggerReason.Parse("").Type);
        }
    }
}
