using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 单次运行日志的落盘写者。
    ///
    /// 为什么不用常开追加流：Windows 下常开写句柄与 File.ReadAllText(FileShare.Read)、
    /// 记事本等外部读取方式双向共享冲突，"查看日志"会直接失败（旧 TaskLogger 注释已论证）。
    /// 为什么不用每行开关文件：stdout 回调是 Process.OutputDataReceived 的线程池线程，
    /// 每行两次开关文件 + Flush 跟不上子进程写入时会写满 4KB 匿名管道，把子进程挂起。
    ///
    /// 这里用「内存缓冲 + 批量追加 + 显式 Flush 屏障」：
    ///   输出回调只做内存追加（不碰磁盘）；每 64 行 / 250ms 批量落盘一次；
    ///   Flush() 保证「写后立即可读」（测试与 UI 打开日志前调用）；
    ///   Close() 写 footer 后强制落盘。关闭期间文件句柄不常驻，外部可正常读取。
    /// 崩溃最多丢失一个 flush 窗口内的行，由 history.jsonl 的 run.start / run.abandoned 兜底。
    /// </summary>
    internal sealed class RunLogWriter
    {
        private const int FlushLineThreshold = 64;
        private const int FlushIntervalMs = 250;

        private readonly object _sync = new object();
        private readonly List<string> _pending = new List<string>();
        private readonly string _path;
        private readonly Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastFlushMs;
        private bool _closed;
        private bool _truncated;
        private long _outputBytes;
        private int _outputLines;
        private long _totalBytes;

        public string LogFilePath { get { return _path; } }
        public long OutputBytes { get { lock (_sync) return _outputBytes; } }
        public int OutputLines { get { lock (_sync) return _outputLines; } }
        public bool Truncated { get { lock (_sync) return _truncated; } }
        public long TotalBytes { get { lock (_sync) return _totalBytes; } }

        public RunLogWriter(string path)
        {
            _path = path;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            _timer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
        }

        /// <summary>header / footer 原样写入，不加时间戳前缀。</summary>
        public void WriteRaw(string line)
        {
            AppendPending(line);
        }

        /// <summary>运行期事件（started / finished / stopped / exception）。</summary>
        public void WriteEvent(string level, string message)
        {
            AppendPending(Timestamp() + " [" + level + "] " + message);
        }

        /// <summary>子进程 stdout / stderr。空行也保留，截断只影响落盘、不影响管道读取。</summary>
        public void WriteOutput(string stream, string text)
        {
            if (text == null) return;
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string raw in lines)
            {
                string line = raw;
                if (line.Length > TaskLogPaths.MaxLineChars)
                    line = line.Substring(0, TaskLogPaths.MaxLineChars) + " [line truncated]";
                AppendPending(Timestamp() + " " + stream + " " + line, isOutput: true);
            }
        }

        private void AppendPending(string line, bool isOutput = false)
        {
            lock (_sync)
            {
                if (_closed) return;
                if (isOutput)
                {
                    if (_truncated) return;
                    long bytes = Encoding.UTF8.GetByteCount(line) + 2;
                    if (_outputBytes + bytes > TaskLogPaths.MaxRunOutputBytes)
                    {
                        _truncated = true;
                        _pending.Add(Timestamp() + " === OUTPUT TRUNCATED: exceeded " +
                            TaskLogPaths.MaxRunOutputBytes + " bytes, further output is drained but not persisted ===");
                        FlushNoLock();
                        return;
                    }
                    _outputBytes += bytes;
                    _outputLines++;
                    _totalBytes += bytes;
                }
                else
                {
                    _totalBytes += Encoding.UTF8.GetByteCount(line) + 2;
                }
                _pending.Add(line);

                if (_pending.Count >= FlushLineThreshold || _clock.ElapsedMilliseconds - _lastFlushMs >= FlushIntervalMs)
                    FlushNoLock();
            }
        }

        /// <summary>显式屏障：返回后文件已包含此前所有行，可被 File.ReadAllText / 外部程序读取。</summary>
        public void Flush()
        {
            lock (_sync) FlushNoLock();
        }

        private void FlushNoLock()
        {
            _lastFlushMs = _clock.ElapsedMilliseconds;
            if (_pending.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                foreach (string line in _pending) sb.Append(line).Append('\n');
                byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, bytes.Length))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }
                _pending.Clear();
            }
            catch
            {
                // 目录不可写 / 被外部删除：保留缓冲下次重试，日志失败绝不影响任务进程
            }
        }

        /// <summary>写 footer 并关闭。幂等；之后的写入被忽略。</summary>
        public void Close(string footer)
        {
            lock (_sync)
            {
                if (_closed) return;
                if (!string.IsNullOrEmpty(footer)) _pending.Add(footer);
                _closed = true;
                FlushNoLock();
                try { _timer.Dispose(); } catch { }
            }
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }
    }
}
