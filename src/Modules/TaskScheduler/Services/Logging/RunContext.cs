using System;
using System.Globalization;
using System.Threading;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 一次任务运行的上下文：唯一 runId + 一个独立日志文件 + 一条历史索引记录。
    ///
    /// 生命周期：TryStart 创建（写 header + run.start）→ 输出回调写 OUT/ERR →
    /// WaitAsync 退出时 Complete（写 footer + run.end）；启动失败则 Abandon。
    /// 宿主崩溃来不及写 footer 时，history.jsonl 的 run.start 由下次启动补
    /// run.abandoned（见 RunHistoryStore.RecoverAbandoned）。
    ///
    /// 所有写入方法都吞掉自身异常：日志绝不能成为任务执行的故障点。
    /// </summary>
    public sealed class RunContext
    {
        private static int _seq;

        private readonly RunLogWriter _log;
        private readonly RunHistoryStore _history;
        private readonly DateTime _startUtc;
        private int _completed;
        private int _pid = -1;

        public string RunId { get; private set; }
        public string TaskName { get; private set; }
        public string TriggerReason { get; private set; }
        public string Command { get; private set; }
        public string Args { get; private set; }
        public string WorkDir { get; private set; }
        public int Attempt { get; private set; }
        public DateTime StartUtc { get { return _startUtc; } }
        public DateTime StartLocal { get; private set; }
        public string FullLogPath { get; private set; }
        public string RelativeLogPath { get; private set; }

        public int Pid { get { return _pid; } }
        public long OutputBytes { get { return _log == null ? 0 : _log.OutputBytes; } }
        public int OutputLines { get { return _log == null ? 0 : _log.OutputLines; } }
        public bool Truncated { get { return _log == null ? false : _log.Truncated; } }

        private RunContext(string taskName, string triggerReason, string command, string args,
            string workDir, int attempt, RunHistoryStore history)
        {
            _startUtc = DateTime.UtcNow;
            StartLocal = DateTime.Now;
            TaskName = taskName ?? "";
            TriggerReason = triggerReason ?? "";
            Command = command ?? "";
            Args = args ?? "";
            WorkDir = workDir ?? "";
            Attempt = attempt <= 0 ? 1 : attempt;
            _history = history ?? RunHistoryStore.Instance;

            int seq = Interlocked.Increment(ref _seq);
            RunId = StartLocal.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                    "-" + Environment.ProcessId + "-" + seq.ToString("D4", CultureInfo.InvariantCulture);

            FullLogPath = TaskLogPaths.GetRunLogPath(TaskName, StartLocal, RunId);
            RelativeLogPath = TaskLogPaths.ToRelative(FullLogPath);

            try { _log = new RunLogWriter(FullLogPath); }
            catch { _log = null; }

            try
            {
                _log?.WriteRaw("=== RUN START runId=" + RunId + " task=" + Escape(TaskName) +
                    " trigger=" + Escape(TriggerReason) + " attempt=" + Attempt +
                    " startUtc=" + _startUtc.ToString("o", CultureInfo.InvariantCulture) +
                    " startLocal=" + StartLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + " ===");
                _log?.WriteRaw("CMD  file=" + Escape(Command) + " args=" + Escape(Args) +
                    " workDir=" + Escape(WorkDir));
                _log?.Flush();
                _history.RecordStart(this);
            }
            catch { }
        }

        public static RunContext Begin(string taskName, string triggerReason, string command,
            string args, string workDir, int attempt = 1, RunHistoryStore history = null)
        {
            return new RunContext(taskName, triggerReason, command, args, workDir, attempt, history);
        }

        public void SetPid(int pid)
        {
            _pid = pid;
        }

        public void Event(string level, string message)
        {
            try { _log?.WriteEvent(level ?? "INFO", message ?? ""); } catch { }
        }

        public void Output(string stream, string text)
        {
            try { _log?.WriteOutput(stream ?? "OUT", text); } catch { }
        }

        /// <summary>显式屏障：调用后 run 文件已包含此前全部行，可被外部程序打开。</summary>
        public void Flush()
        {
            try { _log?.Flush(); } catch { }
        }

        /// <summary>启动失败 / 运行被放弃：补 footer 并结束索引记录。</summary>
        public void Abandon(string outcome, string reason)
        {
            Complete(null, string.IsNullOrEmpty(outcome) ? "start-failed" : outcome, reason ?? "start-failed");
        }

        /// <summary>写 footer + run.end。幂等；重复调用（Stop 与 WaitAsync 竞态）只有第一次生效。</summary>
        public void Complete(int? exitCode, string outcome, string endedBy)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            try
            {
                long durationMs = Math.Max(0, (long)(DateTime.UtcNow - _startUtc).TotalMilliseconds);
                string footer = "=== RUN END runId=" + RunId +
                    " endUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    " durationMs=" + durationMs +
                    " exitCode=" + (exitCode.HasValue ? exitCode.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                    " outcome=" + Escape(outcome ?? "unknown") +
                    " outputBytes=" + OutputBytes + " outputLines=" + OutputLines +
                    " truncated=" + Truncated + " endedBy=" + Escape(endedBy ?? "exited") + " ===";
                _log?.Close(footer);
                _history.RecordEnd(this, outcome ?? "unknown", exitCode, durationMs, endedBy ?? "exited");
            }
            catch { }
        }

        /// <summary>把可能含换行的命令/参数压成单行，避免破坏 header 的行结构。</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
