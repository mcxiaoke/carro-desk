using System;
using System.Reflection;
using System.Windows.Controls;
using CarroDesk.Models;
using CarroDesk.Modules.TaskScheduler.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>编辑器新增高级配置项的表单往返与数值校验。</summary>
    [TestClass]
    public class TaskAdvancedOptionsUiTests
    {
        private static TaskDefinition NewTask(TaskTriggerType type)
        {
            return new TaskDefinition
            {
                Name = "AdvOpts",
                Trigger = new TaskTrigger
                {
                    Type = type,
                    WatchPath = "C:\\tmp",
                    WatchDebounceMs = 1200,
                    WatchReadyTimeoutMs = 500,
                    WatchIncludeSubdirs = true
                },
                Action = new TaskAction { File = "x.bat" },
                When = new TaskCondition { OnlyIdle = true, OnlyIdleSec = 300 }
            };
        }

        private static TextBox Box(TaskEditDialog win, string name)
        {
            return (TextBox)win.FindName(name);
        }

        private static void CloseWithoutPrompt(TaskEditDialog win)
        {
            // 改过表单后 _isDirty=true，直接 Close 会弹模态确认框并在 STA 线程上死锁
            typeof(TaskEditDialog)
                .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(win, false);
            win.Close();
        }

        private static object[] InvokeValidate(TaskEditDialog win)
        {
            var method = typeof(TaskEditDialog).GetMethod("TryValidateNumericInputs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            var args = new object[] { null };
            bool ok = (bool)method.Invoke(win, args);
            return new object[] { ok, args[0] };
        }

        private static TaskDefinition Build(TaskEditDialog win)
        {
            var method = typeof(TaskEditDialog).GetMethod("BuildTaskFromForm",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (TaskDefinition)method.Invoke(win, null);
        }

        [TestMethod]
        public void TaskEditDialog_RoundTripsWatchAndIdleConditionOptions()
        {
            TestEnvironment.RunInSta(() =>
            {
                var win = new TaskEditDialog(NewTask(TaskTriggerType.Watch), false, new string[0]);

                Assert.AreEqual("1200", Box(win, "WatchDebounceMsBox").Text);
                Assert.AreEqual("500", Box(win, "WatchReadyTimeoutMsBox").Text);
                Assert.IsTrue(((CheckBox)win.FindName("WatchIncludeSubdirsBox")).IsChecked == true);
                Assert.AreEqual("300", Box(win, "OnlyIdleSecBox").Text);

                Box(win, "WatchDebounceMsBox").Text = "800";
                Box(win, "WatchReadyTimeoutMsBox").Text = "0";
                ((CheckBox)win.FindName("WatchIncludeSubdirsBox")).IsChecked = false;
                Box(win, "OnlyIdleSecBox").Text = "120";

                var result = Build(win);
                Assert.AreEqual(800, result.Trigger.WatchDebounceMs);
                Assert.AreEqual(0, result.Trigger.WatchReadyTimeoutMs);
                Assert.IsFalse(result.Trigger.WatchIncludeSubdirs);
                Assert.AreEqual(120, result.When.OnlyIdleSec);
                Assert.IsNull(result.Validate(), "表单构造出的任务应通过校验");

                CloseWithoutPrompt(win);
            });
        }

        [TestMethod]
        public void TaskEditDialog_RejectsOutOfRangeAdvancedOptions()
        {
            TestEnvironment.RunInSta(() =>
            {
                var win = new TaskEditDialog(NewTask(TaskTriggerType.Watch), false, new string[0]);

                Box(win, "WatchDebounceMsBox").Text = "70000";
                var r = InvokeValidate(win);
                Assert.IsFalse((bool)r[0], "防抖超过 60000 应被拒绝");
                Assert.IsFalse(string.IsNullOrEmpty((string)r[1]));

                Box(win, "WatchDebounceMsBox").Text = "500";
                Box(win, "OnlyIdleSecBox").Text = "90000";
                r = InvokeValidate(win);
                Assert.IsFalse((bool)r[0], "空闲阈值超过 86400 应被拒绝");

                Box(win, "OnlyIdleSecBox").Text = "60";
                r = InvokeValidate(win);
                Assert.IsTrue((bool)r[0], "合法值应通过校验: " + r[1]);

                CloseWithoutPrompt(win);
            });
        }

        [TestMethod]
        public void TaskEditDialog_HiddenWatchInputsDoNotBlockOtherTriggers()
        {
            TestEnvironment.RunInSta(() =>
            {
                var win = new TaskEditDialog(NewTask(TaskTriggerType.Manual), false, new string[0]);

                // watch 面板隐藏，但表单仍持有旧值；非 watch 触发器不应被隐藏字段挡住保存
                Box(win, "WatchDebounceMsBox").Text = "not-a-number";
                var r = InvokeValidate(win);
                Assert.IsTrue((bool)r[0], "隐藏面板里的非法值不应阻塞其它触发器: " + r[1]);

                CloseWithoutPrompt(win);
            });
        }
    }
}
