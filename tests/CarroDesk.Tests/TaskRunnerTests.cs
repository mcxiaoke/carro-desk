using System;
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

        private static string ReadTaskLog(string taskName)
        {
            string path = TaskLogger.GetTaskLogPath(taskName);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
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
    }
}
