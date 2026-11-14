using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>附录 B：条件判定与触发器共用 IIdleService，且 onlyIdle 阈值可配置。</summary>
    [TestClass]
    public class TaskConditionSourceTests
    {
        private sealed class FakeIdle : IIdleService
        {
            public TimeSpan RawIdle { get; set; }
            public bool IsSystemBusyCached { get { return false; } }
            public event Action<TimeSpan> IdleTick { add { } remove { } }
            public event Action UserActiveDetected { add { } remove { } }
            public event Action<bool> SystemBusyChanged { add { } remove { } }
        }

        private static TaskDefinition NewTask(int onlyIdleSec = 60)
        {
            return new TaskDefinition
            {
                Name = "IdleCondition",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" },
                When = new TaskCondition { OnlyIdle = true, OnlyIdleSec = onlyIdleSec }
            };
        }

        [TestMethod]
        public void ShouldRun_UsesInjectedIdleService_NotLocalDetector()
        {
            var idle = new FakeIdle { RawIdle = TimeSpan.FromSeconds(30) };
            string reason;

            Assert.IsFalse(TaskConditionEvaluator.ShouldRun(NewTask(), idle, out reason),
                "注入的 RawIdle=30s 应判定为不空闲");
            StringAssert.Contains(reason, "when.onlyIdle");

            idle.RawIdle = TimeSpan.FromSeconds(120);
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(NewTask(), idle, out reason),
                "注入的 RawIdle=120s 应放行");
        }

        [TestMethod]
        public void ShouldRun_CustomOnlyIdleSec_IsHonored()
        {
            var idle = new FakeIdle { RawIdle = TimeSpan.FromSeconds(90) };
            string reason;

            Assert.IsFalse(TaskConditionEvaluator.ShouldRun(NewTask(120), idle, out reason),
                "阈值 120s 时 90s 空闲应被拒");
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(NewTask(60), idle, out reason),
                "阈值 60s 时 90s 空闲应放行");
        }

        [TestMethod]
        public void LegacyOverload_StillWorksWithoutIdleService()
        {
            string reason;
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(null, out reason));
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(new TaskDefinition
            {
                Name = "NoCondition",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" }
            }, out reason));
        }

        [TestMethod]
        public void OnlyIdleSec_RoundTripAndValidate()
        {
            var task = NewTask(300);
            TaskConfigService.Save(new List<TaskDefinition> { task });
            var loaded = TaskConfigService.Load();
            Assert.AreEqual(0, loaded.Errors.Count, string.Join("; ", loaded.Errors));

            var back = loaded.Tasks.FirstOrDefault(t => t.Name == "IdleCondition");
            Assert.IsNotNull(back);
            Assert.AreEqual(300, back.When.OnlyIdleSec);
            Assert.IsNull(back.Validate());

            back.When.OnlyIdleSec = 90000;
            StringAssert.Contains(back.Validate(), "onlyIdleSec");
            back.When.OnlyIdleSec = -1;
            StringAssert.Contains(back.Validate(), "onlyIdleSec");
        }
    }
}
