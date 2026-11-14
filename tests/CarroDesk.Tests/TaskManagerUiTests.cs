using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Modules.TaskScheduler.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>任务管理窗口：触发方式精简、最近运行列、双击空白修复、列/排序键契约。</summary>
    [TestClass]
    public class TaskManagerUiTests
    {
        private static TaskDefinition NewTask(TaskTrigger trigger)
        {
            return new TaskDefinition
            {
                Name = "UiProbe",
                Trigger = trigger,
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static TaskRowViewModel Row(TaskDefinition task, DateTime? lastRun = null)
        {
            return new TaskRowViewModel(task, new TaskStateSnapshot(), lastRun);
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "未能定位仓库根目录");
            return Path.Combine(dir.FullName, "src");
        }

        [TestMethod]
        public void TriggerShortText_IsCompactForAllTypes()
        {
            StringAssert.Contains(Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Startup, DelaySec = 10 })).TriggerShortText, "10s");

            var interval = Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Interval, EverySec = 3600 })).TriggerShortText;
            StringAssert.Contains(interval, "1h");
            Assert.IsTrue(interval.Length <= 12, "短标签不应过长（中英文语言包都要成立）: " + interval);

            Assert.AreEqual("02:30", Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Daily, At = "02:30" })).TriggerShortText);
            Assert.AreEqual("Cron", Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Cron, Expr = "0 9 * * 1" })).TriggerShortText);
            StringAssert.Contains(Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Idle, AfterMinutes = 10 })).TriggerShortText, "10m");

            foreach (var type in new[] { TaskTriggerType.Watch, TaskTriggerType.Manual, TaskTriggerType.Hotkey,
                                         TaskTriggerType.SessionLock, TaskTriggerType.SessionUnlock })
            {
                var text = Row(NewTask(new TaskTrigger { Type = type })).TriggerShortText;
                Assert.IsTrue(text.Length <= 8, type + " 的短标签过长: " + text);
            }
        }

        [TestMethod]
        public void TriggerSummary_StillCarriesFullInfoForTooltip()
        {
            var row = Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Cron, Expr = "0 9 * * 1" }));
            StringAssert.Contains(row.TriggerSummary, "0 9 * * 1",
                "精简列文本后，完整信息必须仍可由 ToolTip 取得");
        }

        [TestMethod]
        public void LastRunDisplayText_FormatsShortAndHandlesEmpty()
        {
            var when = new DateTime(2026, 11, 14, 20, 30, 45);
            var row = Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Manual }), when);

            Assert.AreEqual("11-14 20:30", row.LastRunDisplayText);
            Assert.AreEqual("2026-11-14 20:30:45", row.LastRunToolTip);
            Assert.AreEqual(when, row.LastRunAt);

            var empty = Row(NewTask(new TaskTrigger { Type = TaskTriggerType.Manual }));
            Assert.AreEqual("--", empty.LastRunDisplayText);
            Assert.IsNull(empty.LastRunAt);
        }

        [TestMethod]
        public void ShouldOpenEditorOnDoubleClick_RequiresRowHitAndSelection()
        {
            Assert.IsTrue(TaskManagerWindow.ShouldOpenEditorOnDoubleClick(true, true));
            Assert.IsFalse(TaskManagerWindow.ShouldOpenEditorOnDoubleClick(false, true),
                "双击空白处不得打开上次选中的任务");
            Assert.IsFalse(TaskManagerWindow.ShouldOpenEditorOnDoubleClick(true, false));
            Assert.IsFalse(TaskManagerWindow.ShouldOpenEditorOnDoubleClick(false, false));
        }

        [TestMethod]
        public void ColumnSortKeys_MatchXamlColumnCount()
        {
            var field = typeof(TaskManagerWindow).GetField("ColumnSortKeys",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var keys = (string[])field.GetValue(null);

            string xaml = Path.Combine(FindSourceRoot(), "Modules", "TaskScheduler", "Views", "TaskManagerWindow.xaml");
            Assert.IsTrue(File.Exists(xaml), "未找到 TaskManagerWindow.xaml");
            int columns = XDocument.Load(xaml).Descendants()
                .Count(e => e.Name.LocalName == "GridViewColumn");

            Assert.AreEqual(columns, keys.Length,
                "新增/移动列时必须同步维护 ColumnSortKeys（GridViewColumn 无 Tag）");
            CollectionAssert.Contains(keys, "lastrun", "最近运行列必须可排序");
        }
    }
}
