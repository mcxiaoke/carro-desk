using System;
using System.Threading;
using CarroDesk.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class MemoryOptimizerTests
    {
        [TestMethod]
        public void MemoryOptimizer_TrimWorkingSet_ExecutesSafely()
        {
            // 验证强制修剪始终能够安全执行且不抛异常
            bool result = MemoryOptimizer.TrimWorkingSet(force: true);
            Assert.IsTrue(result, "强制修剪应当成功执行");
        }

        [TestMethod]
        public void MemoryOptimizer_Throttling_RespectsMinInterval()
        {
            // 首次强制修剪
            MemoryOptimizer.TrimWorkingSet(force: true);

            // 紧接着非强制修剪，应当被节流拦截返回 false
            bool throttled = MemoryOptimizer.TrimWorkingSet(force: false);
            Assert.IsFalse(throttled, "短时间内的非强制修剪应被节流拦截");
        }

        [TestMethod]
        public void MemoryOptimizer_TrimIfWorkingSetExceeds_ThresholdLogic()
        {
            // 当阈值极大（如 1TB）时，当前进程工作集不可能超过该阈值，应返回 false
            long unreachableThreshold = 1024L * 1024 * 1024 * 1024;
            bool resultHigh = MemoryOptimizer.TrimIfWorkingSetExceeds(unreachableThreshold, force: true);
            Assert.IsFalse(resultHigh, "未超过阈值时不应触发修剪");

            // 当阈值为 0 时，必然超过，应触发修剪
            bool resultLow = MemoryOptimizer.TrimIfWorkingSetExceeds(0, force: true);
            Assert.IsTrue(resultLow, "超过阈值时应触发修剪");
        }

        [TestMethod]
        public void MemoryOptimizer_ScheduleTrim_DoesNotThrowAndRunsAsync()
        {
            // 验证异步调度防抖合并与平稳运行
            MemoryOptimizer.ScheduleTrim(delayMs: 100, force: true);
            MemoryOptimizer.ScheduleTrim(delayMs: 150, force: true);

            // 稍作等待以确保异步任务不抛任何未捕获异常
            Thread.Sleep(300);
        }
    }
}
