using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// TaskRunner 行为测试（P1-2）。
    ///
    /// 这些是真实的进程级端到端测试：真的写出 .bat 脚本、真的用 cmd.exe 启动、
    /// 再从任务日志中读取脚本实际收到的参数，用来验证命令行转义与超时终止行为，
    /// 而不是只断言拼接出来的字符串长什么样。
    /// </summary>
    [TestClass]
    public class TaskRunnerTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(TestEnvironment.TempRoot, "taskrunner-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
        }

        /// <summary>
        /// 探针批处理：用 delayed expansion 安全回显 %*。
        /// 直接 `echo %*` 会让参数里的元字符在批处理内部被再解析一次，测不出真实值。
        /// </summary>
        private string WriteProbeBat(string fileName, int exitCode = 0, bool sleep = false)
        {
            string path = Path.Combine(_dir, fileName);
            var lines = new System.Collections.Generic.List<string>
            {
                "@echo off",
                "setlocal enabledelayedexpansion"
            };
            if (sleep)
            {
                // 用于超时测试：阻塞约 29 秒
                lines.Add("ping -n 30 127.0.0.1 >nul");
            }
            lines.Add("set \"A=%*\"");
            lines.Add("echo ARGS[!A!]");
            lines.Add("exit /b " + exitCode);
            File.WriteAllLines(path, lines, Encoding.ASCII);
            return path;
        }

        private static TaskDefinition NewTask(string name, string file, string args, int timeoutSec = 0)
        {
            return new TaskDefinition
            {
                Name = name,
                Action = new TaskAction { File = file, Args = args },
                Options = new TaskOptions { Hidden = true, TimeoutSec = timeoutSec }
            };
        }

        private static int Run(TaskDefinition task)
        {
            return TaskRunner.RunAsync(task, "unit-test").GetAwaiter().GetResult();
        }

        private static int Run(TaskDefinition task, CancellationToken ct, out TaskRunOutcome outcome)
        {
            outcome = new TaskRunOutcome();
            return TaskRunner.RunAsync(task, "unit-test", ct, outcome).GetAwaiter().GetResult();
        }

        private static string ReadTaskLog(string taskName)
        {
            string path = TaskLogger.GetTaskLogPath(taskName);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }

        /// <summary>
        /// 任务日志写入：写后必须可被普通读取方式读到，且并发写入不丢行、不串行。
        ///
        /// 三条约束都来自真实调用方：
        ///   1. 读后写一致——"查看日志"与单测都在写入后立刻用 File.ReadAllText 读；
        ///   2. 外部可读——常开写句柄会让 File.ReadAllText(FileShare.Read) 直接失败；
        ///   3. 并发安全——WriteOutput 是子进程 stdout/stderr 的回调线程，多任务同时输出。
        /// </summary>
        [TestMethod]
        public void TaskLogger_AppendIsReadableAndConcurrencySafe()
        {
            string taskName = "t_logger_probe";
            string path = TaskLogger.GetTaskLogPath(taskName);
            try { File.Delete(path); } catch { }

            TaskLogger.Info(taskName, "first-line");
            StringAssert.Contains(File.ReadAllText(path, Encoding.UTF8), "first-line",
                "日志写入后必须立即可被 File.ReadAllText 读到（读后写一致）");

            const int threads = 8;
            const int perThread = 40;
            var barrier = new ManualResetEventSlim(false);
            var failures = new List<Exception>();
            var workers = new List<Thread>();
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                var worker = new Thread(() =>
                {
                    try
                    {
                        barrier.Wait();
                        for (int i = 0; i < perThread; i++)
                        {
                            TaskLogger.WriteOutput(taskName, "OUT", "w" + id + "-l" + i);
                        }
                    }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                });
                worker.IsBackground = true;
                workers.Add(worker);
                worker.Start();
            }
            barrier.Set();
            foreach (var w in workers) Assert.IsTrue(w.Join(TimeSpan.FromSeconds(30)), "并发写入线程未在预期时间内结束");

            if (failures.Count > 0) Assert.Fail("并发写入抛异常: " + failures[0]);

            string content = File.ReadAllText(path, Encoding.UTF8);
            for (int t = 0; t < threads; t++)
            {
                for (int i = 0; i < perThread; i++)
                {
                    StringAssert.Contains(content, "w" + t + "-l" + i,
                        "并发写入丢行（线程 " + t + " 第 " + i + " 行）");
                }
            }

            // 聚合日志同样必须落盘且含同一条记录
            StringAssert.Contains(File.ReadAllText(TaskLogger.GetAggregateLogPath(), Encoding.UTF8), "first-line",
                "聚合日志必须同步落盘");
        }

        private static void AssertProbeReceived(string taskName, string expectedArgs)
        {
            string log = ReadTaskLog(taskName);
            StringAssert.Contains(log, "ARGS[" + expectedArgs + "]",
                "脚本实际收到的参数与预期不符。任务日志:\n" + log);
        }

        private static int? ExtractPid(string taskName)
        {
            var m = Regex.Match(ReadTaskLog(taskName), @"started pid=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
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

        [TestMethod]
        public void Bat_AmpersandInArgs_IsPassedVerbatim_NotTreatedAsCommandSeparator()
        {
            string bat = WriteProbeBat("amp.bat", exitCode: 7);
            var task = NewTask("t_amp", bat, "a & b");

            int code = Run(task);

            Assert.AreEqual(7, code, "退出码未正确回传");
            AssertProbeReceived("t_amp", "a & b");
        }

        [TestMethod]
        public void Bat_PipeAndRedirectInArgs_ArePassedVerbatim()
        {
            string bat = WriteProbeBat("pipe.bat");
            var task = NewTask("t_pipe", bat, "a | b > out.txt");

            Run(task);

            AssertProbeReceived("t_pipe", "a | b > out.txt");
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "out.txt")),
                "参数中的 > 被当成重定向执行了，说明转义失效");
        }

        [TestMethod]
        public void Bat_CaretAndParensInArgs_ArePassedVerbatim()
        {
            string bat = WriteProbeBat("caret.bat");
            var task = NewTask("t_caret", bat, "(x) ^ y");

            Run(task);

            AssertProbeReceived("t_caret", "(x) ^ y");
        }

        [TestMethod]
        public void Bat_QuotedArgWithSpace_IsPassedVerbatim()
        {
            string bat = WriteProbeBat("quoted.bat");
            var task = NewTask("t_quoted", bat, "--msg \"hello world\"");

            Run(task);

            AssertProbeReceived("t_quoted", "--msg \"hello world\"");
        }

        [TestMethod]
        public void Bat_ScriptPathWithSpaces_StillRuns()
        {
            string sub = Path.Combine(_dir, "sub dir with space");
            Directory.CreateDirectory(sub);
            string bat = Path.Combine(sub, "probe.bat");
            File.WriteAllLines(bat, new[]
            {
                "@echo off",
                "setlocal enabledelayedexpansion",
                "set \"A=%*\"",
                "echo ARGS[!A!]",
                "exit /b 3"
            }, Encoding.ASCII);

            var task = NewTask("t_spacepath", bat, "\"a b\" c");
            int code = Run(task);

            Assert.AreEqual(3, code, "含空格路径的脚本未能正常执行");
            AssertProbeReceived("t_spacepath", "\"a b\" c");
        }

        [TestMethod]
        public void Bat_NonZeroExitCode_IsPropagated()
        {
            string bat = WriteProbeBat("exitcode.bat", exitCode: 42);
            int code = Run(NewTask("t_exitcode", bat, ""));

            Assert.AreEqual(42, code);
        }

        [TestMethod]
        public void Bat_EmptyArgs_RunsCleanly()
        {
            string bat = WriteProbeBat("noargs.bat");
            int code = Run(NewTask("t_noargs", bat, ""));

            Assert.AreEqual(0, code);
            AssertProbeReceived("t_noargs", "");
        }

        [TestMethod]
        public void Timeout_LongRunningScript_IsKilled_AndReturnsMinusOne()
        {
            string bat = WriteProbeBat("slow.bat", sleep: true);
            var task = NewTask("t_timeout", bat, "", timeoutSec: 1);

            var sw = Stopwatch.StartNew();
            int code = Run(task);
            sw.Stop();

            Assert.AreEqual(-1, code, "超时任务应返回 -1");

            int? pid = ExtractPid("t_timeout");
            Assert.IsNotNull(pid, "未从日志中解析到 pid，任务可能根本没启动");

            // 超时终止必须真的杀掉落进程，而不只是让 RunAsync 提前返回
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5) && IsProcessAlive(pid.Value))
            {
                Thread.Sleep(100);
            }
            Assert.IsFalse(IsProcessAlive(pid.Value),
                "超时后子进程 " + pid.Value + " 仍然存活，终止逻辑失效（可能残留孤儿进程）");
        }

        [TestMethod]
        public void Timeout_WithOutcome_ReportsTimedOut()
        {
            string bat = WriteProbeBat("slow2.bat", sleep: true);
            var task = NewTask("t_outcome_timeout", bat, "", timeoutSec: 1);

            TaskRunOutcome outcome;
            int code = Run(task, CancellationToken.None, out outcome);

            Assert.AreEqual(-1, code, "超时任务应返回 -1");
            Assert.IsTrue(outcome.TimedOut, "应报告 TimedOut=true");
            Assert.IsFalse(outcome.Cancelled, "未请求取消，不应报告 Cancelled");
        }

        [TestMethod]
        public void Cancel_UserStop_KillsProcess_AndReportsCancelled()
        {
            string bat = WriteProbeBat("stoppable.bat", sleep: true);
            var task = NewTask("t_cancel", bat, "");

            // 2 秒后触发取消（脚本本身要跑约 29 秒）
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                var sw = Stopwatch.StartNew();
                TaskRunOutcome outcome;
                int code = Run(task, cts.Token, out outcome);
                sw.Stop();

                Assert.AreEqual(-1, code, "取消后应返回 -1");
                Assert.IsTrue(outcome.Cancelled, "应报告 Cancelled=true");
                Assert.IsFalse(outcome.TimedOut, "用户停止不应被报告为超时");
                Assert.IsTrue(sw.Elapsed.TotalSeconds < 8,
                    "取消后应尽快返回（实际 " + sw.Elapsed.TotalSeconds + "s），而不是等脚本自然结束");

                int? pid = ExtractPid("t_cancel");
                Assert.IsNotNull(pid, "未从日志中解析到 pid，任务可能根本没启动");

                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(5) && IsProcessAlive(pid.Value))
                {
                    Thread.Sleep(100);
                }
                Assert.IsFalse(IsProcessAlive(pid.Value),
                    "取消后子进程 " + pid.Value + " 仍然存活，停止逻辑失效");
            }
        }

        [TestMethod]
        public void NormalExit_WithOutcome_ReportsExitCode()
        {
            string bat = WriteProbeBat("outcome.bat", exitCode: 5);

            TaskRunOutcome outcome;
            int code = Run(NewTask("t_outcome", bat, ""), CancellationToken.None, out outcome);

            Assert.AreEqual(5, code);
            Assert.AreEqual(5, outcome.ExitCode, "outcome.ExitCode 应回传真实退出码");
            Assert.IsFalse(outcome.TimedOut);
            Assert.IsFalse(outcome.Cancelled);
        }

        [TestMethod]
        public void LegacyOverload_StillWorks_AfterCancellationOverloadAdded()
        {
            string bat = WriteProbeBat("legacy.bat", exitCode: 9);
            int code = Run(NewTask("t_legacy", bat, ""));

            Assert.AreEqual(9, code, "两参旧重载行为不应改变");
        }
    }
}
