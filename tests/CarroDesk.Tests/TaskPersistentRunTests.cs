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

        // ---------- B2 单实例互斥 ----------

        [TestMethod]
        public void SingleInstance_SecondStartSkipped_AllowedAfterStop()
        {
            string bat = WriteSleepBat("si-daemon.bat", 30);
            var task = NewDetachTask("t_si", bat);
            task.Options.SingleInstance = true;

            TaskProcessHandle first;
            Assert.AreEqual(0, TaskRunner.StartDetached(task, "unit-test", out first), "首次启动应成功");
            Assert.IsTrue(IsProcessAlive(first.Pid));
            first.Exited += (h, c) => { };
            first.BeginExitWatch();   // 与生产路径一致（AttachDetached），watcher 完成时释放句柄与互斥体

            // 冲突启动必须被跳过且不产生进程
            TaskProcessHandle second;
            Assert.AreEqual(TaskRunner.StartSkippedSingleInstance, TaskRunner.StartDetached(task, "unit-test", out second),
                "互斥体被持有时第二次启动应返回跳过码");
            Assert.IsNull(second, "被跳过的启动不得返回句柄");

            // 停止后互斥体随句柄释放，可再次启动（watcher 异步释放，允许短暂重试窗口）
            first.Stop();
            WaitUntil(() => !IsProcessAlive(first.Pid), TimeSpan.FromSeconds(5), "停止后进程应终止");

            int code = -1;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                TaskProcessHandle third;
                code = TaskRunner.StartDetached(task, "unit-test", out third);
                if (code == 0)
                {
                    third.Stop();
                    break;
                }
                Thread.Sleep(250);
            }
            Assert.AreEqual(0, code, "实例停止并释放互斥体后应能再次启动");
        }

        [TestMethod]
        public void Restart_AndSingleInstance_ValidationRules()
        {
            // restart=on-failure 仅限 detach（wait 的等价物是 retry）
            var waitRestart = new TaskDefinition
            {
                Name = "t_v", Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" },
                Options = new TaskOptions { Restart = "on-failure" }
            };
            StringAssert.Contains(waitRestart.Validate(), "requires mode=detach", "wait+restart 应被拒绝");

            var badValue = NewDetachTask("t_v", "x.bat");
            badValue.Options.Restart = "always";
            StringAssert.Contains(badValue.Validate(), "restart invalid", "未知 restart 值应被拒绝");

            var badDelay = NewDetachTask("t_v", "x.bat");
            badDelay.Options.Restart = "on-failure";
            badDelay.Options.RestartDelaySec = 0;
            StringAssert.Contains(badDelay.Validate(), "restartDelaySec", "重启间隔下界应被拒绝");

            var badLimit = NewDetachTask("t_v", "x.bat");
            badLimit.Options.Restart = "on-failure";
            badLimit.Options.RestartLimit = 101;
            StringAssert.Contains(badLimit.Validate(), "restartLimit", "最大重启上界应被拒绝");

            var badStable = NewDetachTask("t_v", "x.bat");
            badStable.Options.Restart = "on-failure";
            badStable.Options.StableUptimeSec = -1;
            StringAssert.Contains(badStable.Validate(), "stableUptimeSec", "稳定阈值负数应被拒绝");

            var siWait = new TaskDefinition
            {
                Name = "t_v", Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                Action = new TaskAction { File = "x.bat" },
                Options = new TaskOptions { SingleInstance = true }
            };
            StringAssert.Contains(siWait.Validate(), "singleInstance requires mode=detach", "wait+singleInstance 应被拒绝");

            var ok = NewDetachTask("t_v", "x.bat");
            ok.Options.Restart = "on-failure";
            ok.Options.SingleInstance = true;
            Assert.IsNull(ok.Validate(), "合法守护配置应通过校验");
        }

        // ---------- B3 守护重启 ----------

        private const string SuperviseTaskName = "t_supervise";

        private void WriteSuperviseTasksJson(string scriptPath, string restartOptions)
        {
            var options = (JObject)JToken.Parse(restartOptions);
            options["mode"] = "detach";
            options["hidden"] = true;
            var tasks = new JArray(new JObject(
                new JProperty("name", SuperviseTaskName),
                new JProperty("enabled", true),
                new JProperty("trigger", new JObject(new JProperty("type", "manual"))),
                new JProperty("action", new JObject(new JProperty("file", scriptPath))),
                new JProperty("options", options)));
            File.WriteAllText(TaskConfigService.FilePath, tasks.ToString(), Encoding.UTF8);
        }

        [TestMethod]
        public void Supervise_RestartsUntilMarked_ManualRunResets()
        {
            // 立即崩溃退出 7：失败 3 次（1 次启动 + 2 次重启）后熔断标记
            string bat = WriteExitBat("crash7.bat", 7);
            WriteSuperviseTasksJson(bat, "{ \"restart\": \"on-failure\", \"restartDelaySec\": 1, \"restartLimit\": 2, \"stableUptimeSec\": 1, \"notifyOnFailure\": false }");
            try
            {
                var scheduler = new TaskSchedulerService();
                scheduler.Start();
                try
                {
                    scheduler.RunManual(SuperviseTaskName);
                    WaitUntil(() => scheduler.GetSupervision(SuperviseTaskName).MarkedFailed, TimeSpan.FromSeconds(20),
                        "连续失败超过预算后应熔断标记失败");
                    Assert.IsFalse(scheduler.IsRunning(SuperviseTaskName), "熔断后不应有运行实例");
                    Assert.AreEqual(3, scheduler.GetSupervision(SuperviseTaskName).ConsecutiveFailures,
                        "1 次启动 + 2 次重启 = 3 次连续失败");
                    Assert.IsTrue(scheduler.GetRecent().Count > 0 && scheduler.GetRecent()[0].Contains("marked"),
                        "recent 列表应记录熔断标记");

                    // 手动运行 = 显式复位：清除标记并重新拉起（随后会再次失败、再次熔断，属预期）
                    scheduler.RunManual(SuperviseTaskName);
                    bool sawRunningOrFailing = false;
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        var sup = scheduler.GetSupervision(SuperviseTaskName);
                        if (scheduler.IsRunning(SuperviseTaskName) || (sup.ConsecutiveFailures > 0 && !sup.MarkedFailed))
                        {
                            sawRunningOrFailing = true;
                            break;
                        }
                        Thread.Sleep(100);
                    }
                    Assert.IsTrue(sawRunningOrFailing, "手动运行应清除标记并重新启动实例");
                    scheduler.TryStop(SuperviseTaskName);
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
        public void Supervise_StableUptimeResetsCounter_NeverMarks()
        {
            // 每次运行约 2 秒后以 7 退出；stableUptimeSec=1 → 每次退出都复位计数 → 永不熔断
            string bat = Path.Combine(_dir, "unstable-but-long.bat");
            File.WriteAllLines(bat, new[]
            {
                "@echo off",
                "ping -n 3 127.0.0.1 >nul",
                "exit /b 7"
            }, Encoding.ASCII);
            WriteSuperviseTasksJson(bat, "{ \"restart\": \"on-failure\", \"restartDelaySec\": 1, \"restartLimit\": 1, \"stableUptimeSec\": 1, \"notifyOnFailure\": false }");
            try
            {
                var scheduler = new TaskSchedulerService();
                scheduler.Start();
                try
                {
                    scheduler.RunManual(SuperviseTaskName);

                    // 观察至少 2 个完整周期（~6s）；若复位失效，limit=1 时第 2 次失败即熔断（~4.5s 内出现）
                    var sw = Stopwatch.StartNew();
                    int cycles = 0;
                    while (sw.Elapsed < TimeSpan.FromSeconds(12))
                    {
                        var sup = scheduler.GetSupervision(SuperviseTaskName);
                        Assert.IsFalse(sup.MarkedFailed, "稳定存活应重置失败计数，不应熔断");
                        cycles = Math.Max(cycles, sup.ConsecutiveFailures);
                        Thread.Sleep(200);
                    }
                    Assert.IsTrue(scheduler.IsRunning(SuperviseTaskName) || cycles > 0,
                        "观察期内应至少经历一次失败-重启循环");
                    scheduler.TryStop(SuperviseTaskName);
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
        public void Supervise_TryStopCancelsPendingRestart()
        {
            // 快速崩溃 + 5s 重启间隔：首个失败后处于"挂起重启"窗口，TryStop 必须取消它
            string bat = WriteExitBat("crash7b.bat", 7);
            WriteSuperviseTasksJson(bat, "{ \"restart\": \"on-failure\", \"restartDelaySec\": 5, \"restartLimit\": 5, \"stableUptimeSec\": 60, \"notifyOnFailure\": false }");
            try
            {
                var scheduler = new TaskSchedulerService();
                scheduler.Start();
                try
                {
                    scheduler.RunManual(SuperviseTaskName);
                    WaitUntil(() => scheduler.GetSupervision(SuperviseTaskName).ConsecutiveFailures >= 1,
                        TimeSpan.FromSeconds(10), "首次失败应被计数");
                    Assert.IsTrue(scheduler.IsRunning(SuperviseTaskName), "重启延迟期内槽保持占用");

                    Assert.IsTrue(scheduler.TryStop(SuperviseTaskName), "取消挂起重启应返回 true");
                    Assert.IsFalse(scheduler.IsRunning(SuperviseTaskName), "停止后应立即出册");

                    // 跨过 5s 重启间隔：不得再有第二次尝试（否则计数会涨到 2）
                    Thread.Sleep(TimeSpan.FromSeconds(7));
                    Assert.IsFalse(scheduler.IsRunning(SuperviseTaskName), "挂起重启应已被取消");
                    Assert.AreEqual(1, scheduler.GetSupervision(SuperviseTaskName).ConsecutiveFailures,
                        "取消后不得有新的启动尝试");
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
    }
}
