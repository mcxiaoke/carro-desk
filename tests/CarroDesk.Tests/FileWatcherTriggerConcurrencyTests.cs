using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 附录 B P0：FileWatcherTrigger 的并发缺陷与配置化回归。
    /// </summary>
    [TestClass]
    public class FileWatcherTriggerConcurrencyTests
    {
        private string _dir;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "watch-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private TaskDefinition NewTask(Action<TaskTrigger> tune = null)
        {
            var tr = new TaskTrigger { Type = TaskTriggerType.Watch, WatchPath = _dir };
            tune?.Invoke(tr);
            return new TaskDefinition
            {
                Name = "watch_probe",
                Trigger = tr,
                Action = new TaskAction { File = "x.bat" }
            };
        }

        private static object ReadField(object instance, string name)
        {
            return typeof(FileWatcherTrigger)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);
        }

        private static void InvokePrivate(object instance, string method, params object[] args)
        {
            typeof(FileWatcherTrigger)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(instance, args);
        }

        [TestMethod]
        public void ShouldDebounce_ConcurrentSameKey_OnlyOnePasses()
        {
            var trigger = new FileWatcherTrigger(NewTask());
            var now = DateTime.Now;
            int allowed = 0;

            Parallel.For(0, 32, i =>
            {
                if (!trigger.ShouldDebounce("C:\\test\\same.txt", now))
                    Interlocked.Increment(ref allowed);
            });

            Assert.AreEqual(1, allowed,
                "同一文件的并发事件必须恰好放行一次；AddOrUpdate 回调里写外部变量会漏判");
        }

        [TestMethod]
        public void ShouldDebounce_CustomDebounceMs_IsHonored()
        {
            var trigger = new FileWatcherTrigger(NewTask(t => t.WatchDebounceMs = 2000));
            var now = DateTime.Now;

            Assert.IsFalse(trigger.ShouldDebounce("f.txt", now));
            Assert.IsTrue(trigger.ShouldDebounce("f.txt", now.AddMilliseconds(600)),
                "默认 500ms 会放行，配置 2000ms 时必须仍被拦截");
            Assert.IsFalse(trigger.ShouldDebounce("f.txt", now.AddMilliseconds(2100)));
        }

        [TestMethod]
        public void ShouldDebounce_ZeroDisablesDebounce()
        {
            var trigger = new FileWatcherTrigger(NewTask(t => t.WatchDebounceMs = 0));
            var now = DateTime.Now;

            Assert.IsFalse(trigger.ShouldDebounce("f.txt", now));
            Assert.IsFalse(trigger.ShouldDebounce("f.txt", now), "0 应完全关闭防抖");
        }

        [TestMethod]
        public void Stop_InvalidatesGeneration_AndErrorRecoveryCannotResurrectWatcher()
        {
            var trigger = new FileWatcherTrigger(NewTask());
            trigger.Start();

            int gen = trigger.CurrentGeneration;
            Assert.IsTrue(trigger.IsGenerationCurrent(gen));
            Assert.IsNotNull(ReadField(trigger, "_watcher"), "Start 后应持有 watcher");

            trigger.Stop();
            Assert.IsFalse(trigger.IsGenerationCurrent(gen), "Stop 必须让旧代失效");
            Assert.IsNull(ReadField(trigger, "_watcher"));

            // 模拟「Error 回调已排队、稍后才执行」的旧代自愈任务
            InvokePrivate(trigger, "OnWatcherError", null, new ErrorEventArgs(new IOException("simulated overflow")));
            Task.Delay(250).Wait();

            Assert.IsNull(ReadField(trigger, "_watcher"),
                "Stop 之后旧代自愈任务不得复活 watcher（幽灵触发 + 句柄泄漏）");

            trigger.Start();
            Assert.AreNotEqual(gen, trigger.CurrentGeneration, "再次 Start 必须是新的一代");
            Assert.IsTrue(trigger.IsGenerationCurrent(trigger.CurrentGeneration));
            trigger.Dispose();
        }

        [TestMethod]
        public void Stop_PreventsLateEventDispatch()
        {
            var trigger = new FileWatcherTrigger(NewTask());
            int fired = 0;
            trigger.Fired += (t, reason) => Interlocked.Increment(ref fired);
            trigger.Start();

            InvokePrivate(trigger, "OnEvent", null,
                new FileSystemEventArgs(WatcherChangeTypes.Created, _dir, "a.txt"));
            Task.Delay(250).Wait();
            Assert.AreEqual(1, fired, "正常运行时事件应派发一次");

            trigger.Stop();
            InvokePrivate(trigger, "OnEvent", null,
                new FileSystemEventArgs(WatcherChangeTypes.Created, _dir, "b.txt"));
            Task.Delay(250).Wait();
            Assert.AreEqual(1, fired, "Stop 之后不得再向服务层派发");
        }

        [TestMethod]
        public void WatchOptions_RoundTripAndValidate()
        {
            var task = NewTask(t =>
            {
                t.WatchDebounceMs = 1200;
                t.WatchIncludeSubdirs = true;
                t.WatchReadyTimeoutMs = 500;
            });
            task.Name = "WatchOpts";

            TaskConfigService.Save(new List<TaskDefinition> { task });
            var loaded = TaskConfigService.Load();
            Assert.AreEqual(0, loaded.Errors.Count, string.Join("; ", loaded.Errors));

            var back = loaded.Tasks.FirstOrDefault(t => t.Name == "WatchOpts");
            Assert.IsNotNull(back);
            Assert.AreEqual(1200, back.Trigger.WatchDebounceMs);
            Assert.IsTrue(back.Trigger.WatchIncludeSubdirs);
            Assert.AreEqual(500, back.Trigger.WatchReadyTimeoutMs);
            Assert.IsNull(back.Validate());

            back.Trigger.WatchDebounceMs = 70000;
            StringAssert.Contains(back.Validate(), "debounceMs");
            back.Trigger.WatchDebounceMs = 1200;
            back.Trigger.WatchReadyTimeoutMs = -1;
            StringAssert.Contains(back.Validate(), "readyTimeoutMs");
        }
    }
}
