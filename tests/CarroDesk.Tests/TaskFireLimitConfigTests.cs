using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 频率限制三字段的配置层回归（设计文档 §7 用例 10）。
    /// 覆盖 Save/Load 往返、手写别名与时长字符串、Validate 范围、Clone 保留。
    /// </summary>
    [TestClass]
    public class TaskFireLimitConfigTests
    {
        private static TaskDefinition NewTask(string name)
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Idle, AfterMinutes = 5 },
                Action = new TaskAction { File = "x.bat" }
            };
        }

        [TestMethod]
        public void TaskConfigService_RoundTripsFireLimitOptions()
        {
            var task = NewTask("ThrottleRoundTrip");
            task.Options.MaxRunsPerDay = 3;
            task.Options.MinIntervalSec = 1800;
            task.Options.LimitScope = "all";

            TaskConfigService.Save(new List<TaskDefinition> { task });
            var loaded = TaskConfigService.Load();

            Assert.AreEqual(0, loaded.Errors.Count, "加载不应有错误: " + string.Join("; ", loaded.Errors));
            var back = loaded.Tasks.FirstOrDefault(t => t.Name == "ThrottleRoundTrip");
            Assert.IsNotNull(back, "保存的任务必须能读回");
            Assert.AreEqual(3, back.Options.MaxRunsPerDay);
            Assert.AreEqual(1800, back.Options.MinIntervalSec);
            Assert.AreEqual("all", back.Options.LimitScope);
            Assert.IsTrue(back.Options.LimitAppliesToUserDriven);
        }

        [TestMethod]
        public void TaskConfigService_ParsesAliasesAndDurationStrings()
        {
            const string json = @"[
  {
    ""name"": ""ThrottleAlias"",
    ""trigger"": { ""type"": ""idle"", ""afterMinutes"": 5 },
    ""action"": { ""file"": ""x.bat"" },
    ""options"": { ""maxRuns"": 2, ""minInterval"": ""30m"", ""limitScope"": ""ALL"" }
  }
]";
            File.WriteAllText(TaskConfigService.FilePath, json, Encoding.UTF8);
            var loaded = TaskConfigService.Load();

            Assert.AreEqual(0, loaded.Errors.Count, "加载不应有错误: " + string.Join("; ", loaded.Errors));
            var back = loaded.Tasks.FirstOrDefault(t => t.Name == "ThrottleAlias");
            Assert.IsNotNull(back);
            Assert.AreEqual(2, back.Options.MaxRunsPerDay, "maxRuns 别名必须生效");
            Assert.AreEqual(1800, back.Options.MinIntervalSec, "minInterval 的 30m 时长字符串必须解析为 1800 秒");
            Assert.IsNull(back.Validate(), "ALL 大小写不敏感，应通过校验");
            Assert.IsTrue(back.Options.LimitAppliesToUserDriven);
        }

        [TestMethod]
        public void TaskConfigService_DefaultOptionsAreUnlimited()
        {
            var task = NewTask("ThrottleDefault");

            Assert.IsNull(task.Validate());
            Assert.AreEqual(0, task.Options.MaxRunsPerDay);
            Assert.AreEqual(0, task.Options.MinIntervalSec);
            Assert.AreEqual("auto", task.Options.LimitScope);
            Assert.IsFalse(task.Options.LimitAppliesToUserDriven);
        }

        [TestMethod]
        public void TaskDefinition_Validate_RejectsOutOfRangeFireLimitOptions()
        {
            var task = NewTask("ThrottleInvalid");

            task.Options.MaxRunsPerDay = -1;
            StringAssert.Contains(task.Validate(), "maxRunsPerDay");
            task.Options.MaxRunsPerDay = 1001;
            StringAssert.Contains(task.Validate(), "maxRunsPerDay");

            task.Options.MaxRunsPerDay = 2;
            task.Options.MinIntervalSec = -1;
            StringAssert.Contains(task.Validate(), "minIntervalSec");
            task.Options.MinIntervalSec = 86401;
            StringAssert.Contains(task.Validate(), "minIntervalSec");

            task.Options.MinIntervalSec = 60;
            task.Options.LimitScope = "sometimes";
            StringAssert.Contains(task.Validate(), "limitScope");

            task.Options.LimitScope = "auto";
            Assert.IsNull(task.Validate(), "合法值必须通过校验");
        }

        [TestMethod]
        public void TaskDefinition_Clone_PreservesFireLimitOptions()
        {
            var task = NewTask("ThrottleClone");
            task.Options.MaxRunsPerDay = 7;
            task.Options.MinIntervalSec = 900;
            task.Options.LimitScope = "all";
            task.Options.ExtraOptions = new Newtonsoft.Json.Linq.JObject { ["customFlag"] = true };

            var clone = task.Clone();

            Assert.AreEqual(7, clone.Options.MaxRunsPerDay);
            Assert.AreEqual(900, clone.Options.MinIntervalSec);
            Assert.AreEqual("all", clone.Options.LimitScope);
            Assert.IsNotNull(clone.Options.ExtraOptions, "ExtraOptions 仍需单独搬运（既有 Clone 语义）");
            Assert.IsTrue((bool)clone.Options.ExtraOptions["customFlag"]);
        }
    }
}
