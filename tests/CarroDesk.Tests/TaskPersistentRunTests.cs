using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 常驻（detach）任务支持测试（TASKS-PERSISTENT-RUN B1）。
    ///
    /// 覆盖：模型层组合校验、配置序列化 round-trip、TaskProcessHandle 生命周期
    /// （退出监视 / 停止杀树 / killWithHost=false 存活语义）、调度器运行注册表
    /// （防重 / 状态查询 / TryStop）。真实进程级，沿用 TaskRunnerTests 探针模式。
    /// </summary>
    [TestClass]
    public class TaskPersistentRunTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "persistent-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
        }

        private string WriteSleepBat(string fileName, int seconds)
        {
            string path = Path.Combine(_dir, fileName);
            File.WriteAllLines(path, new[]
            {
                "@echo off",
                "ping -n " + (seconds + 1) + " 127.0.0.1 >nul",
                "exit /b 0"
            }, Encoding.ASCII);
            return path;
        }

        private string WriteExitBat(string fileName, int exitCode)
        {
            string path = Path.Combine(_dir, fileName);
            File.WriteAllLines(path, new[] { "@echo off", "exit /b " + exitCode }, Encoding.ASCII);
            return path;
        }

        private static TaskDefinition NewDetachTask(string name, string file, bool killWithHost = true)
        {
            return new TaskDefinition
            {
                Name = name,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = file },
                Options = new TaskOptions { Hidden = true, Mode = "detach", KillWithHost = killWithHost }
            };
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !condition()) Thread.Sleep(100);
            Assert.IsTrue(condition(), "等待超时: " + what);
        }

        // ---------- 模型层组合校验 ----------

        [TestMethod]
        public void Detach_ConflictingOptions_AreRejected()
        {
            var timeout = NewDetachTask("t_conflict", "x.bat");
            timeout.Options.TimeoutSec = 60;
            StringAssert.Contains(timeout.Validate(), "timeoutSec", "detach+timeoutSec 应被校验拒绝");

            var retry = NewDetachTask("t_conflict", "x.bat");
            retry.Options.Retry = 2;
            StringAssert.Contains(retry.Validate(), "retry", "detach+retry 应被校验拒绝");

            var concurrent = NewDetachTask("t_conflict", "x.bat");
            concurrent.Options.AllowConcurrent = true;
            StringAssert.Contains(concurrent.Validate(), "allowConcurrent", "detach+allowConcurrent 应被校验拒绝");

            var badMode = NewDetachTask("t_conflict", "x.bat");
            badMode.Options.Mode = "daemon";
            StringAssert.Contains(badMode.Validate(), "mode", "未知 mode 值应被校验拒绝");
        }

        [TestMethod]
        public void Detach_ValidConfig_Passes_AndWaitDefaultsUnchanged()
        {
            var detach = NewDetachTask("t_ok", "x.bat");
            Assert.IsNull(detach.Validate(), "合法 detach 配置应通过校验");

            // 缺省（wait）行为与旧版本完全一致
            var defaults = new TaskOptions();
            Assert.AreEqual("wait", defaults.Mode);
            Assert.IsTrue(defaults.KillWithHost);
            Assert.IsFalse(defaults.IsDetach);
            Assert.IsFalse(defaults.SingleInstance);

            var waitTask = new TaskDefinition
            {
                Name = "t_ok",
                Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" }
            };
            Assert.IsNull(waitTask.Validate(), "未设置 options 的旧格式任务应保持可校验通过");
        }

        [TestMethod]
        public void DetachOptions_RoundTripThroughConfigFile()
        {
            try
            {
                var task = NewDetachTask("t_roundtrip", "C:\\some dir\\daemon.js", killWithHost: false);
                TaskConfigService.Save(new System.Collections.Generic.List<TaskDefinition> { task });

                var result = TaskConfigService.Load();
                Assert.AreEqual(0, result.Errors.Count, "round-trip 不应产生加载错误: " + string.Join("; ", result.Errors));
                Assert.AreEqual(1, result.Tasks.Count);
                var loaded = result.Tasks[0];
                Assert.AreEqual("detach", loaded.Options.Mode);
                Assert.IsTrue(loaded.Options.IsDetach);
                Assert.IsFalse(loaded.Options.KillWithHost, "killWithHost=false 应正确往返");
            }
            finally
            {
                try { File.Delete(TaskConfigService.FilePath); } catch { }
            }
        }

        // ---------- TaskProcessHandle 生命周期 ----------

        [TestMethod]
        public void Detached_QuickExit_FiresExitedWithCode()
        {
            string bat = WriteExitBat("exit0.bat", 0);
            var task = NewDetachTask("t_detach_exit", bat);

            TaskProcessHandle handle;
            Assert.AreEqual(0, TaskRunner.StartDetached(task, "unit-test", out handle), "启动应成功");
            Assert.IsTrue(handle.Pid > 0);
            Assert.IsTrue(handle.KillWithHost, "killWithHost 默认 true，应挂作业对象");

            int? exitCode = null;
            var fired = new ManualResetEvent(false);
            handle.Exited += (h, c) => { exitCode = c; fired.Set(); };
            handle.BeginExitWatch();

            Assert.IsTrue(fired.WaitOne(TimeSpan.FromSeconds(10)), "快速退出的 detach 实例应触发 Exited");
            Assert.AreEqual(0, exitCode.Value);
            Assert.IsFalse(handle.WasStopped, "自然退出不算被停止");
            handle.Dispose();
        }

        [TestMethod]
        public void Detached_Stop_KillsProcessTree_AndReportsStopped()
        {
            string bat = WriteSleepBat("daemon30s.bat", 30);
            var task = NewDetachTask("t_detach_stop", bat);

            TaskProcessHandle handle;
            Assert.AreEqual(0, TaskRunner.StartDetached(task, "unit-test", out handle));

            var exited = new ManualResetEvent(false);
            handle.Exited += (h, c) => exited.Set();
            handle.BeginExitWatch();

            Assert.IsTrue(IsProcessAlive(handle.Pid), "sleep 期间进程应存活");

            handle.Stop();
            Assert.IsTrue(exited.WaitOne(TimeSpan.FromSeconds(10)), "Stop 后应触发 Exited");
            Assert.IsTrue(handle.WasStopped, "Stop 应标记 WasStopped");

            WaitUntil(() => !IsProcessAlive(handle.Pid), TimeSpan.FromSeconds(5),
                "Stop 后子进程 " + handle.Pid + " 仍存活");
            handle.Dispose();
        }

        [TestMethod]
        public void Detached_KillWithHostFalse_SurvivesDispose_ButStopStillWorks()
        {
            string bat = WriteSleepBat("daemon-free.bat", 30);
            var task = NewDetachTask("t_detach_free", bat, killWithHost: false);

            TaskProcessHandle handle;
            Assert.AreEqual(0, TaskRunner.StartDetached(task, "unit-test", out handle));
            Assert.IsFalse(handle.KillWithHost, "killWithHost=false 不应挂作业对象");
            Assert.IsTrue(IsProcessAlive(handle.Pid));

            // 宿主退出清理路径：只 Dispose，不杀——进程必须继续存活
            handle.Dispose();
            Assert.IsTrue(IsProcessAlive(handle.Pid), "killWithHost=false 的实例应在句柄 Dispose 后继续存活");

            // 手动停止仍然可用（taskkill 降级链）
            handle.Stop();
            WaitUntil(() => !IsProcessAlive(handle.Pid), TimeSpan.FromSeconds(5),
                "Stop 后子进程 " + handle.Pid + " 仍存活（killWithHost=false 路径）");
        }

        // ---------- 调度器运行注册表 ----------

        [TestMethod]
        public void Scheduler_DetachTask_TracksRunning_Dedupes_AndStops()
        {
            string bat = WriteSleepBat("sched-daemon.bat", 30);
            var tasks = new JArray(new JObject(
                new JProperty("name", "t_sched_detach"),
                new JProperty("enabled", true),
                new JProperty("trigger", new JObject(new JProperty("type", "manual"))),
                new JProperty("action", new JObject(new JProperty("file", bat))),
                new JProperty("options", new JObject(
                    new JProperty("mode", "detach"),
                    new JProperty("hidden", true)))));

            File.WriteAllText(TaskConfigService.FilePath, tasks.ToString(), Encoding.UTF8);
            try
            {
                var scheduler = new TaskSchedulerService();
                scheduler.Start();
                try
                {
                    Assert.IsTrue(scheduler.RunManual("t_sched_detach"), "手动触发应成功");
                    WaitUntil(() => scheduler.IsRunning("t_sched_detach"), TimeSpan.FromSeconds(10),
                        "启动后应进入运行注册表");
                    Assert.AreEqual(1, scheduler.GetRunning().Count);
                    Assert.IsTrue(scheduler.GetRecent().Count > 0 && scheduler.GetRecent()[0].Contains("started"),
                        "recent 列表应记录 started");

                    // 防重：实例运行期间再次触发不得叠加
                    scheduler.RunManual("t_sched_detach");
                    Thread.Sleep(1500);
                    Assert.AreEqual(1, scheduler.GetRunning().Count, "运行期间重复触发应被跳过，不得叠加实例");

                    // 停止
                    Assert.IsTrue(scheduler.TryStop("t_sched_detach"), "停止运行中实例应成功");
                    WaitUntil(() => !scheduler.IsRunning("t_sched_detach"), TimeSpan.FromSeconds(10),
                        "退出 watcher 应清理运行槽");
                }
                finally
                {
                    scheduler.Stop();
                }
            }
            finally
            {
                try { File.Delete(TaskConfigService.FilePath); } catch { }
            }
        }

        [TestMethod]
        public void Scheduler_TryStop_NotRunning_ReturnsFalse()
        {
            var scheduler = new TaskSchedulerService();
            Assert.IsFalse(scheduler.TryStop("no-such-task"), "无运行实例时 TryStop 应返回 false");
            Assert.AreEqual(0, scheduler.GetRunning().Count);
        }
    }
}
