using System;
using System.Reflection;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Modules.TaskScheduler.Views;
using CarroDesk.Services.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 频率限制的 UI 层回归：大盘触发描述摘要 + 编辑器表单往返。
    /// </summary>
    [TestClass]
    public class TaskFireLimitUiTests
    {
        private static TaskDefinition NewIdleTask(int maxRuns, int minIntervalSec, string scope = "auto")
        {
            return new TaskDefinition
            {
                Name = "UiThrottle",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Idle, AfterMinutes = 10 },
                Action = new TaskAction { File = "clean.ps1" },
                Options = new TaskOptions
                {
                    MaxRunsPerDay = maxRuns,
                    MinIntervalSec = minIntervalSec,
                    LimitScope = scope
                }
            };
        }

        private static string SummaryOf(TaskDefinition task)
        {
            return new TaskRowViewModel(task, new TaskStateSnapshot()).TriggerSummary;
        }

        [TestMethod]
        public void TriggerSummary_NoLimits_KeepsPlainDescription()
        {
            // 不硬编码语言包文案：只验证「无限制时摘要不带任何后缀」这一契约
            string plain = SummaryOf(NewIdleTask(0, 0));
            Assert.AreEqual(plain, SummaryOf(NewIdleTask(0, 0)), "同配置摘要应稳定");
            Assert.IsFalse(plain.Contains(Loc.T("Tasks.ThrottleDailyCap", "每天≤{0}次", 1)));
            Assert.IsFalse(plain.Contains("30m"), "未配置限流不得出现间隔摘要");
        }

        [TestMethod]
        public void TriggerSummary_WithLimits_AppendsDailyCapAndInterval()
        {
            string plain = SummaryOf(NewIdleTask(0, 0));
            string s = SummaryOf(NewIdleTask(3, 1800, "all"));

            StringAssert.StartsWith(s, plain, "限流摘要必须保留原触发描述作为前缀");
            StringAssert.Contains(s, Loc.T("Tasks.ThrottleDailyCap", "每天≤{0}次", 3));
            StringAssert.Contains(s, Loc.T("Tasks.ThrottleMinInterval", "间隔≥{0}", "30m"),
                "整分钟应换算成 m，避免显示 1800s");
        }

        [TestMethod]
        public void TriggerSummary_MinutesNotDivisibleBy60_ShowsSeconds()
        {
            StringAssert.Contains(SummaryOf(NewIdleTask(0, 45)),
                Loc.T("Tasks.ThrottleMinInterval", "间隔≥{0}", "45s"));
        }

        [TestMethod]
        public void TaskEditDialog_RoundTripsFireLimitForm()
        {
            TestEnvironment.RunInSta(() =>
            {
                var source = NewIdleTask(3, 1800, "all");
                var win = new TaskEditDialog(source, isNew: false, new string[0]);

                var maxBox = (TextBox)win.FindName("MaxRunsPerDayBox");
                var minBox = (TextBox)win.FindName("MinIntervalSecBox");
                var scopeBox = (ComboBox)win.FindName("LimitScopeBox");
                Assert.IsNotNull(maxBox);
                Assert.IsNotNull(minBox);
                Assert.IsNotNull(scopeBox);

                Assert.AreEqual("3", maxBox.Text, "编辑已有任务时表单应回填每天次数");
                Assert.AreEqual("1800", minBox.Text, "编辑已有任务时表单应回填最短间隔");
                Assert.AreEqual("all", (string)((ComboBoxItem)scopeBox.SelectedItem).Tag,
                    "编辑已有任务时表单应回填限制范围");

                maxBox.Text = "5";
                minBox.Text = "600";
                scopeBox.SelectedIndex = 0;   // auto

                var build = typeof(TaskEditDialog).GetMethod("BuildTaskFromForm",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(build, "BuildTaskFromForm 应存在");
                var result = (TaskDefinition)build.Invoke(win, null);

                Assert.AreEqual(5, result.Options.MaxRunsPerDay);
                Assert.AreEqual(600, result.Options.MinIntervalSec);
                Assert.AreEqual("auto", result.Options.LimitScope);
                Assert.IsNull(result.Validate(), "表单构造出的任务应通过校验");

                // 修改表单后 _isDirty=true，直接 Close 会触发 OnClosing 的模态保存确认框，
                // 在 RunInSta 的 STA 线程上无人点击会让 thread.Join() 永久挂起。先复位 dirty 再关窗。
                typeof(TaskEditDialog)
                    .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(win, false);
                win.Close();
            });
        }
    }
}
