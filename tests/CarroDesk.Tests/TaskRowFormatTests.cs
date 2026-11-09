using CarroDesk.Modules.TaskScheduler.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 任务大盘列格式化：已运行时长的“人类可读”文案与内存占用单位换算。
    /// </summary>
    [TestClass]
    public class TaskRowFormatTests
    {
        [TestMethod]
        public void FormatDuration_UnderOneMinute_ShowsSeconds()
        {
            Assert.AreEqual("0 秒", TaskRowViewModel.FormatDuration(0));
            Assert.AreEqual("45 秒", TaskRowViewModel.FormatDuration(45.9));
        }

        [TestMethod]
        public void FormatDuration_UnderOneHour_ShowsMinutesAndSeconds()
        {
            Assert.AreEqual("1 分", TaskRowViewModel.FormatDuration(60));
            Assert.AreEqual("1 分 30 秒", TaskRowViewModel.FormatDuration(90));
            Assert.AreEqual("59 分 59 秒", TaskRowViewModel.FormatDuration(3599));
        }

        [TestMethod]
        public void FormatDuration_UnderOneDay_ShowsHoursAndMinutes()
        {
            Assert.AreEqual("1 小时", TaskRowViewModel.FormatDuration(3600));
            Assert.AreEqual("1 小时 1 分", TaskRowViewModel.FormatDuration(3660));
            Assert.AreEqual("23 小时 59 分", TaskRowViewModel.FormatDuration(86399));
        }

        [TestMethod]
        public void FormatDuration_OverOneDay_ShowsDaysAndHours()
        {
            Assert.AreEqual("1 天", TaskRowViewModel.FormatDuration(86400));
            // 201612s = 2 天 8 小时 0 分 12 秒（原始行内“秒数”列的最长样例）
            Assert.AreEqual("2 天 8 小时", TaskRowViewModel.FormatDuration(201612));
        }

        [TestMethod]
        public void FormatDuration_NegativeOrInvalid_TreatedAsZero()
        {
            Assert.AreEqual("0 秒", TaskRowViewModel.FormatDuration(-5));
            Assert.AreEqual("0 秒", TaskRowViewModel.FormatDuration(double.NaN));
        }

        [TestMethod]
        public void FormatBytes_Units()
        {
            Assert.AreEqual("--", TaskRowViewModel.FormatBytes(0));
            Assert.AreEqual("--", TaskRowViewModel.FormatBytes(-1));
            Assert.AreEqual("1.0 MB", TaskRowViewModel.FormatBytes(1024L * 1024));
            Assert.AreEqual("72.8 MB", TaskRowViewModel.FormatBytes(76312576));
            Assert.AreEqual("2.50 GB", TaskRowViewModel.FormatBytes((long)(2.5 * 1024 * 1024 * 1024)));
        }
    }
}
