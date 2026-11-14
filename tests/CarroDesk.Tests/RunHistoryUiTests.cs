using System;
using CarroDesk.Modules.TaskScheduler.Views;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>运行历史窗口的行格式化（纯逻辑，不构造 WPF 窗口）。</summary>
    [TestClass]
    public class RunHistoryUiTests
    {
        private static RunHistoryRow Row(RunHistoryEntry entry)
        {
            return new RunHistoryRow(entry);
        }

        [TestMethod]
        public void Duration_FormatsMillisecondsSecondsAndMinutes()
        {
            Assert.AreEqual("500ms", Row(new RunHistoryEntry { DurationMs = 500 }).DurationText);
            Assert.AreEqual("1.5s", Row(new RunHistoryEntry { DurationMs = 1500 }).DurationText);
            Assert.AreEqual("1m05s", Row(new RunHistoryEntry { DurationMs = 65000 }).DurationText);
            Assert.AreEqual("-", Row(new RunHistoryEntry { DurationMs = 0 }).DurationText);
        }

        [TestMethod]
        public void Status_RunningWhenNotCompleted()
        {
            Assert.AreEqual("running", Row(new RunHistoryEntry { Completed = false }).StatusText);
            Assert.AreEqual("fail:3", Row(new RunHistoryEntry { Completed = true, Outcome = "fail:3" }).StatusText);
            Assert.AreEqual("interrupted",
                Row(new RunHistoryEntry { Completed = true, Interrupted = true, Outcome = "interrupted" }).StatusText);
        }

        [TestMethod]
        public void Output_ShowsLinesBytesAndTruncation()
        {
            Assert.AreEqual("12 · 2.0 KB",
                Row(new RunHistoryEntry { Completed = true, OutputLines = 12, OutputBytes = 2048 }).OutputText);
            Assert.AreEqual("3 · 100 B · trunc",
                Row(new RunHistoryEntry { Completed = true, OutputLines = 3, OutputBytes = 100, Truncated = true }).OutputText);
            Assert.AreEqual("-", Row(new RunHistoryEntry { Completed = false }).OutputText,
                "运行中的记录没有输出统计，不应显示 0 · 0 B");
        }

        [TestMethod]
        public void Command_JoinsFileAndArgsAndFallsBack()
        {
            Assert.AreEqual("cmd.exe /c run.bat",
                Row(new RunHistoryEntry { Command = "cmd.exe", Args = "/c run.bat" }).CommandDisplay);
            Assert.AreEqual("-", Row(new RunHistoryEntry()).CommandDisplay);
        }

        [TestMethod]
        public void StartText_UsesLocalTimeAndHandlesUnknown()
        {
            var utc = new DateTime(2026, 11, 14, 12, 30, 45, DateTimeKind.Utc);
            Assert.AreEqual(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Row(new RunHistoryEntry { StartUtc = utc }).StartText);
            Assert.AreEqual("--", Row(new RunHistoryEntry { StartUtc = DateTime.MinValue }).StartText);
        }
    }
}
