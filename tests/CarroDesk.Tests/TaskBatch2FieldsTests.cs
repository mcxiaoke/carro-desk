using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S3/M3（docs/CODE-REVIEW-20261025.md）回归：Batch-2 字段持久化与补跑语义。
    /// </summary>
    [TestClass]
    public class TaskBatch2FieldsTests
    {
        // ===== S3：encoding / runAtStartup / catchUpMissed / env 必须随 Save/Load 保留 =====

        [TestMethod]
        public void TaskConfigService_RoundTripsBatch2Fields()
        {
            var task = new TaskDefinition
            {
                Name = "Batch2RoundTrip",
                Enabled = true,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "02:30" },
                Action = new TaskAction
                {
                    File = "C:\\scripts\\run.bat",
                    Args = "--x",
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["FOO"] = "bar baz",
                        ["PATH2"] = "C:\\tmp"
                    }
                },
                Options = new TaskOptions
                {
                    Mode = "wait",
                    Encoding = "gbk",
                    RunAtStartup = true,
                    CatchUpMissed = true
                }
            };
            TaskConfigService.Save(new List<TaskDefinition> { task });

            var loaded = TaskConfigService.Load();
            Assert.AreEqual(0, loaded.Errors.Count, "加载不应有错误: " + string.Join("; ", loaded.Errors));
            var back = loaded.Tasks.FirstOrDefault(t => t.Name == "Batch2RoundTrip");
            Assert.IsNotNull(back, "保存的任务必须能读回");

            Assert.AreEqual("gbk", back.Options.Encoding, "encoding 必须落盘并读回（此前保存即丢）");
            Assert.IsTrue(back.Options.RunAtStartup, "runAtStartup 必须落盘并读回（此前保存即丢）");
            Assert.IsTrue(back.Options.CatchUpMissed, "catchUpMissed 必须落盘并读回（此前保存即丢）");

            Assert.IsNotNull(back.Action.Env);
            Assert.AreEqual("bar baz", back.Action.Env["FOO"], "action.env 必须落盘并读回（此前保存即丢）");
            Assert.AreEqual("C:\\tmp", back.Action.Env["PATH2"]);
        }

        [TestMethod]
        public void TaskDefinition_Validate_RejectsUnknownEncoding()
        {
            var task = new TaskDefinition
            {
                Name = "EncCheck",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Interval, EverySec = 60 },
                Action = new TaskAction { File = "x.bat" },
                Options = new TaskOptions { Encoding = "iso-8859-1" }
            };
            StringAssert.Contains(task.Validate(), "encoding", "未知 encoding 应在配置期拒绝而非静默按 UTF-8 处理");

            task.Options.Encoding = "gbk";
            Assert.IsNull(task.Validate());
            task.Options.Encoding = "utf-8";
            Assert.IsNull(task.Validate());
            task.Options.Encoding = "";
            Assert.IsNull(task.Validate());
        }

        // ===== M3：触发器重建不得自带"启动补发"，立即触发统一由服务层会话集合投递 =====

        [TestMethod]
        public void DailyTrigger_CatchUpMissed_DoesNotFireImmediatelyOnStart()
        {
            // 触发点 00:00 在测试执行时刻必然已过，修复前 Start() 会立即补发一次
            var task = new TaskDefinition
            {
                Name = "CatchupProbe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "00:00" },
                Options = new TaskOptions { CatchUpMissed = true }
            };
            int fired = 0;
            var trigger = new DailyTrigger(task);
            trigger.Fired += (t, r) => fired++;
            trigger.Start();
            trigger.Stop();
            trigger.Dispose();
            Assert.AreEqual(0, fired,
                "重载（重建触发器）时不得立即补跑当天已过触发点的 daily 任务，补跑由服务层每会话投递一次");
        }

        [TestMethod]
        public void IntervalTrigger_RunAtStartup_DoesNotFireOnStart()
        {
            var task = new TaskDefinition
            {
                Name = "RunAtStartupProbe",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Interval, EverySec = 60 },
                Options = new TaskOptions { RunAtStartup = true }
            };
            int fired = 0;
            var trigger = new IntervalTrigger(task);
            trigger.Fired += (t, r) => fired++;
            trigger.Start();
            trigger.Stop();
            trigger.Dispose();
            Assert.AreEqual(0, fired,
                "RunAtStartup 的立即触发改由服务层会话集合投递，触发器重建不得自带补发");
        }
    }
}
