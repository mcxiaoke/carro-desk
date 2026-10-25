using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// H4/AW-3（docs/CODE-REVIEW-20261025.md）回归：Awake 状态跨线程写序与时间解析。
    /// </summary>
    [TestClass]
    public class AwakeMarshallingTests
    {
        private static string ReadSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CarroDesk.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "未能定位仓库根目录（向上找不到 CarroDesk.slnx）");
            string path = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "未找到源文件: " + path);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static int AssertContains(string source, string needle, int from, string message)
        {
            int at = source.IndexOf(needle, from, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, message);
            return at;
        }

        /// <summary>
        /// IPC 命令 handler 在线程池线程写 AwakeService 状态，tick 在 UI 线程读。
        /// 两步写必须"先写时刻/截止时间，后写闸门字段"（_pendingAction / _mode）：
        /// 读方以闸门字段进入判定，x86-TSO 保证看到闸门翻转时必已看到先写的值；
        /// 反序则可能读到"新动作 + 旧 MinValue 时刻"并立即执行电源动作/误判过期。
        /// </summary>
        [TestMethod]
        public void AwakeService_TwoStepWrites_OrderTimeBeforeGateField()
        {
            string source = ReadSource("src/Modules/Awake/Services/AwakeService.cs");

            // SchedulePowerOff：_pendingFireAt 必须先于 _pendingAction 写入
            int scheduleAt = AssertContains(source, "public void SchedulePowerOff", 0, "找不到 SchedulePowerOff");
            int fireAtWrite = AssertContains(source, "_pendingFireAt = ", scheduleAt, "SchedulePowerOff 应写 _pendingFireAt");
            int actionWrite = AssertContains(source, "_pendingAction = action", scheduleAt, "SchedulePowerOff 应写 _pendingAction");
            Assert.IsTrue(fireAtWrite < actionWrite,
                "SchedulePowerOff 必须先写 _pendingFireAt 再写 _pendingAction（H4 写序）");

            // SetTimed：_expireTime 必须先于 _mode 写入
            int timedAt = AssertContains(source, "public void SetTimed", 0, "找不到 SetTimed");
            int expireWrite = AssertContains(source, "_expireTime = ", timedAt, "SetTimed 应写 _expireTime");
            int modeWrite = AssertContains(source, "_mode = AwakeMode.Timed", timedAt, "SetTimed 应写 _mode");
            Assert.IsTrue(expireWrite < modeWrite,
                "SetTimed 必须先写 _expireTime 再写 _mode = Timed（H4 写序）");

            // SetUntilTime：同上
            int untilAt = AssertContains(source, "public void SetUntilTime", 0, "找不到 SetUntilTime");
            int untilExpireWrite = AssertContains(source, "_expireTime = ", untilAt, "SetUntilTime 应写 _expireTime");
            int untilModeWrite = AssertContains(source, "_mode = AwakeMode.UntilTime", untilAt, "SetUntilTime 应写 _mode");
            Assert.IsTrue(untilExpireWrite < untilModeWrite,
                "SetUntilTime 必须先写 _expireTime 再写 _mode = UntilTime（H4 写序）");
        }

        /// <summary>_mode / _pendingAction 跨线程读写，必须声明 volatile 防止重排与读旧值。</summary>
        [TestMethod]
        public void AwakeService_GateFields_AreVolatile()
        {
            string source = ReadSource("src/Modules/Awake/Services/AwakeService.cs");
            StringAssert.Contains(source, "volatile AwakeMode _mode", "_mode 必须声明 volatile（H4）");
            StringAssert.Contains(source, "volatile PendingPowerAction _pendingAction", "_pendingAction 必须声明 volatile（H4）");
        }

        /// <summary>
        /// TimeSpan.TryParse 会把纯数字 "18" 解析为 18 天，机器可能被保持唤醒数周。
        /// "保持至指定时刻"输入必须用严格 HH:mm[:ss] 解析（AW-3）。
        /// </summary>
        [TestMethod]
        public void AwakeSettingsWindow_UsesStrictTimeParse()
        {
            string source = ReadSource("src/Modules/Awake/Views/AwakeSettingsWindow.xaml.cs");
            Assert.IsTrue(source.Contains("TryParseExact"),
                "UntilTime 输入必须用 TryParseExact 严格解析，不得使用 TimeSpan.TryParse（纯数字会被解析为天数）");
        }
    }
}
