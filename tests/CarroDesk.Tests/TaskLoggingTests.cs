using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 重构后的日志契约：一次运行一个 runId / 一个文件 / 一条可查询历史；
    /// 管理日志与进程输出严格分离；崩溃遗留记录可恢复；保留策略生效。
    /// </summary>
    [TestClass]
    public class TaskLoggingTests
    {
        private static string NewTaskName()
        {
            return "t_log_" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        private static string NewTempLogsDir()
        {
            string dir = Path.Combine(TestEnvironment.TempRoot, "logs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(dir, "runs"));
            return dir;
        }

        private static RunContext Begin(string taskName, string file = "cmd.exe", string args = "--flag", string workDir = "D:\\work")
        {
            return RunContext.Begin(taskName, "unit-test", file, args, workDir, 1, RunHistoryStore.Instance);
        }

        [TestMethod]
        public void RunContext_EachRunHasOwnIdAndFile()
        {
            string task = NewTaskName();
            var first = Begin(task, "cmd.exe", "first");
            first.Complete(0, "ok", "exited");
            var second = Begin(task, "cmd.exe", "second");
            second.Complete(1, "fail:1", "exited");

            Assert.AreNotEqual(first.RunId, second.RunId, "每次运行必须有唯一 runId");
            Assert.AreNotEqual(first.FullLogPath, second.FullLogPath, "每次运行必须写独立文件");
            Assert.IsTrue(File.Exists(first.FullLogPath));
            Assert.IsTrue(File.Exists(second.FullLogPath));

            string c1 = File.ReadAllText(first.FullLogPath, Encoding.UTF8);
            string c2 = File.ReadAllText(second.FullLogPath, Encoding.UTF8);
            StringAssert.Contains(c1, "first");
            StringAssert.Contains(c2, "second");
            StringAssert.Contains(c1, "=== RUN START runId=" + first.RunId);
            StringAssert.Contains(c1, "=== RUN END runId=" + first.RunId);
            StringAssert.Contains(c2, "exitCode=1");
        }

        [TestMethod]
        public void RunLog_HeaderAndFooterContainRequiredFields()
        {
            string task = NewTaskName();
            var run = Begin(task, "powershell.exe", "-File backup.ps1 --target D:\\bak", "D:\\scripts");
            run.Output("OUT", "hello");
            run.Output("ERR", "warning");
            run.Complete(0, "ok", "exited");

            string content = File.ReadAllText(run.FullLogPath, Encoding.UTF8);
            StringAssert.Contains(content, "CMD  file=powershell.exe");
            StringAssert.Contains(content, "args=-File backup.ps1 --target D:\\bak");
            StringAssert.Contains(content, "workDir=D:\\scripts");
            StringAssert.Contains(content, " OUT hello");
            StringAssert.Contains(content, " ERR warning");
            StringAssert.Contains(content, "startUtc=");
            StringAssert.Contains(content, "durationMs=");
            StringAssert.Contains(content, "exitCode=0");
            StringAssert.Contains(content, "outcome=ok");
            StringAssert.Contains(content, "outputLines=2");
        }

        [TestMethod]
        public void RunHistory_StartEndMergeIntoQueryableEntry()
        {
            string task = NewTaskName();
            var run = Begin(task, "cmd.exe", "history-args", "D:\\work");
            run.Output("OUT", "line");
            run.Complete(3, "fail:3", "exited");

            var entry = RunHistoryStore.Instance.Query(task, 10).FirstOrDefault();
            Assert.IsNotNull(entry, "run.start + run.end 应归并成一条历史记录");
            Assert.AreEqual(run.RunId, entry.RunId);
            Assert.AreEqual(task, entry.TaskName);
            Assert.AreEqual("cmd.exe", entry.Command);
            Assert.AreEqual("history-args", entry.Args);
            Assert.AreEqual("D:\\work", entry.WorkDir);
            Assert.AreEqual(3, entry.ExitCode);
            Assert.AreEqual("fail:3", entry.Outcome);
            Assert.AreEqual(1, entry.OutputLines);
            Assert.IsTrue(entry.Completed);
            Assert.IsTrue(entry.DurationMs >= 0);
            Assert.AreEqual(TaskLogPaths.ToRelative(run.FullLogPath), entry.LogPath);
            Assert.IsNotNull(RunHistoryStore.Instance.GetLatestRunLogPath(task));
        }

        [TestMethod]
        public void RunHistory_RecoverAbandonedMarksInterrupted()
        {
            string dir = NewTempLogsDir();
            var store = new RunHistoryStore(dir);
            string line = "{\"ts\":\"" + DateTime.UtcNow.AddMinutes(-5).ToString("o") +
                "\",\"type\":\"run.start\",\"runId\":\"rid-abandoned\",\"task\":\"t\"," +
                "\"startUtc\":\"" + DateTime.UtcNow.AddMinutes(-5).ToString("o") +
                "\",\"logPath\":\"runs/t/202611/rid-abandoned.log\"}";
            File.WriteAllText(store.IndexPath, line + "\n", new UTF8Encoding(false));

            store.RecoverAbandoned();

            var entry = store.Query("t", 10).Single();
            Assert.IsTrue(entry.Interrupted, "崩溃遗留的 start 必须补 interrupted");
            Assert.AreEqual("interrupted", entry.Outcome);
            Assert.IsTrue(entry.Completed);
        }

        [TestMethod]
        public void RunHistory_PruneDropsRunsOlderThanRetainDays()
        {
            string dir = NewTempLogsDir();
            var store = new RunHistoryStore(dir);
            DateTime old = DateTime.UtcNow.AddDays(-(TaskLogPaths.RetainDays + 1));
            string rel = "runs/t/202610/rid-old.log";
            string line = "{\"ts\":\"" + old.ToString("o") +
                "\",\"type\":\"run.start\",\"runId\":\"rid-old\",\"task\":\"t\"," +
                "\"startUtc\":\"" + old.ToString("o") + "\",\"logPath\":\"" + rel + "\"}";
            string full = store.ResolveLogPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(store.IndexPath, line + "\n", new UTF8Encoding(false));
            File.WriteAllText(full, "old run log", new UTF8Encoding(false));

            store.Prune();

            Assert.IsFalse(File.Exists(full), "超过保留天数的 run 文件应被删除");
            Assert.AreEqual(0, store.Query("t", 100).Count, "索引应同步移除已删除的记录");
        }

        [TestMethod]
        public void RunHistory_PruneKeepsAtMostMaxRunsPerTask()
        {
            string dir = NewTempLogsDir();
            var store = new RunHistoryStore(dir);
            var lines = new List<string>();
            for (int i = 0; i < TaskLogPaths.MaxRunsPerTask + 5; i++)
            {
                DateTime when = DateTime.UtcNow.AddMinutes(-i);
                lines.Add("{\"ts\":\"" + when.ToString("o") +
                    "\",\"type\":\"run.start\",\"runId\":\"rid-" + i + "\",\"task\":\"t\"," +
                    "\"startUtc\":\"" + when.ToString("o") +
                    "\",\"logPath\":\"runs/t/202611/rid-" + i + ".log\"}");
            }
            File.WriteAllLines(store.IndexPath, lines, new UTF8Encoding(false));

            store.Prune();

            Assert.AreEqual(TaskLogPaths.MaxRunsPerTask, store.Query("t", 1000).Count,
                "每任务保留次数应有硬上限");
        }

        [TestMethod]
        public void RunLog_OutputIsTruncatedButDrainContinues()
        {
            string task = NewTaskName();
            var run = Begin(task);

            // 单行超长：截断并留标记
            run.Output("OUT", new string('x', TaskLogPaths.MaxLineChars + 100));
            // 累计超过单次运行上限：停止落盘，但回调仍继续（不抛、不阻塞）
            string chunk = new string('y', 64 * 1024);
            for (int i = 0; i < 180; i++) run.Output("OUT", chunk);
            run.Output("OUT", "still-drained");
            run.Complete(0, "ok", "exited");

            string content = File.ReadAllText(run.FullLogPath, Encoding.UTF8);
            StringAssert.Contains(content, "[line truncated]");
            StringAssert.Contains(content, "=== OUTPUT TRUNCATED");
            Assert.IsFalse(content.Contains("still-drained"), "超限后的输出不得继续落盘");
            Assert.IsTrue(run.Truncated);
        }

        [TestMethod]
        public void SchedulerLog_SystemIsAFieldAndOutputNeverLeaks()
        {
            string marker = "system-marker-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            TaskLogger.Warn("system", marker);

            string scheduler = TaskLogTestHelper.ReadSchedulerLog();
            StringAssert.Contains(scheduler, "[task=system] " + marker);

            // "system" 不再对应任何任务文件；新写入的 marker 不得出现在任何 task-*.log
            string logsDir = TaskLogPaths.LogsDir;
            foreach (string file in Directory.Exists(logsDir)
                ? Directory.GetFiles(logsDir, "task-*.log", SearchOption.TopDirectoryOnly)
                : new string[0])
            {
                string content = File.ReadAllText(file, Encoding.UTF8);
                Assert.IsFalse(content.Contains(marker),
                    "管理事件不得写入任务日志: " + file);
            }

            // 若升级前遗留了旧伪任务文件，也不得再被写入新事件
            string legacy = Path.Combine(logsDir, "task-system.log");
            if (File.Exists(legacy))
                Assert.IsFalse(File.ReadAllText(legacy, Encoding.UTF8).Contains(marker),
                    "管理事件不得再写入伪任务文件 task-system.log");
        }

        [TestMethod]
        public void TaskLogPaths_DirNameUsesHashToAvoidCaseCollision()
        {
            Assert.AreNotEqual(TaskLogPaths.GetTaskDirName("Foo"), TaskLogPaths.GetTaskDirName("foo"),
                "Windows 文件系统大小写不敏感，目录名必须区分大小写不同的任务名");
            Assert.AreNotEqual(TaskLogPaths.GetTaskDirName("a".PadRight(70, 'b')),
                TaskLogPaths.GetTaskDirName("a".PadRight(70, 'c')),
                "超长名截断后不得碰撞");
        }
    }
}
