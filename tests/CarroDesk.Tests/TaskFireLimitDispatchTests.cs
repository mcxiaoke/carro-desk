using System.Collections.Generic;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 服务层统一门回归（设计文档 §7 用例 11-12）。
    /// 直接调用 internal TryPassFireLimit，避免启动真实进程；limiter 由构造注入纯内存态。
    /// </summary>
    [TestClass]
    public class TaskFireLimitDispatchTests
    {
        private static TaskDefinition NewTask(string name, int maxRuns, string scope = "auto")
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" },
                Options = new TaskOptions { MaxRunsPerDay = maxRuns, LimitScope = scope }
            };
        }

        private static TaskSchedulerService NewService(TaskFireLimiter limiter)
        {
            return new TaskSchedulerService(fireLimiter: limiter);
        }

        [TestMethod]
        public void Dispatch_AutoTriggerConsumesQuota()
        {
            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            var task = NewTask("DispatchAuto", 2);
            string refusal;

            Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.IsFalse(svc.TryPassFireLimit(task, "idle:5m", out refusal), "第 3 次应被限流门拦下");
            StringAssert.Contains(refusal, "daily quota");
        }

        [TestMethod]
        public void Dispatch_AutoScope_SkipsUserDrivenWithoutConsuming()
        {
            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            var task = NewTask("DispatchAutoScope", 1);
            string refusal;

            Assert.IsTrue(svc.TryPassFireLimit(task, "manual", out refusal));
            Assert.IsTrue(svc.TryPassFireLimit(task, "restart", out refusal));
            Assert.IsTrue(svc.TryPassFireLimit(task, "hotkey:Ctrl+Alt+Q", out refusal));
            Assert.AreEqual(0, limiter.GetSnapshot("DispatchAutoScope").Count,
                "auto 作用域下人工触发既不受限也不消耗自动触发的额度");

            Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.IsFalse(svc.TryPassFireLimit(task, "idle:5m", out refusal));
        }

        [TestMethod]
        public void Dispatch_AllScope_LimitsUserDriven()
        {
            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            var task = NewTask("DispatchAllScope", 1, "all");
            string refusal;

            Assert.IsTrue(svc.TryPassFireLimit(task, "manual", out refusal));
            Assert.IsFalse(svc.TryPassFireLimit(task, "restart", out refusal), "all 作用域下人工触发同样受限");
            Assert.IsFalse(svc.TryPassFireLimit(task, "idle:5m", out refusal));
        }

        [TestMethod]
        public void Dispatch_ReloadDoesNotResetQuota()
        {
            var task = NewTask("DispatchReload", 2);
            TaskConfigService.Save(new List<TaskDefinition> { task });

            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            string refusal;

            Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.AreEqual(1, limiter.GetSnapshot("DispatchReload").Count);

            svc.Reload();   // 模拟托盘「重载任务」/ 编辑器「保存并重载」/ 配置热重载

            Assert.AreEqual(1, limiter.GetSnapshot("DispatchReload").Count,
                "Apply 重建触发器不得清空限流额度");
            Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.IsFalse(svc.TryPassFireLimit(task, "idle:5m", out refusal),
                "重载后仍需能触发到上限，不能多给一轮配额");
        }

        [TestMethod]
        public void Dispatch_CatchUpReason_IsLimited()
        {
            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            var task = NewTask("DispatchCatchup", 1);
            string refusal;

            Assert.IsTrue(svc.TryPassFireLimit(task, "startup:catchup", out refusal),
                "补跑本身是一次运行，首次应放行");
            Assert.IsFalse(svc.TryPassFireLimit(task, "startup:catchup", out refusal),
                "补跑不得绕过每日上限");
        }

        [TestMethod]
        public void Dispatch_UnlimitedPolicy_AlwaysPasses()
        {
            var limiter = new TaskFireLimiter();
            var svc = NewService(limiter);
            var task = NewTask("DispatchUnlimited", 0, "all");
            string refusal;

            for (int i = 0; i < 5; i++)
                Assert.IsTrue(svc.TryPassFireLimit(task, "idle:5m", out refusal));
            Assert.AreEqual(0, limiter.GetSnapshot("DispatchUnlimited").Count);
        }
    }
}
