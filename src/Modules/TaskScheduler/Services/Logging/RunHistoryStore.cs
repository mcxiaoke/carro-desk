using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Services.Tasks
{
    /// <summary>一次运行的完整历史记录（由 start/end/abandoned 事件归并而成）。</summary>
    public sealed class RunHistoryEntry
    {
        public string RunId { get; set; }
        public string TaskName { get; set; }
        public string Trigger { get; set; }
        public string Command { get; set; }
        public string Args { get; set; }
        public string WorkDir { get; set; }
        public string LogPath { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
        public int Attempt { get; set; }
        public int? ExitCode { get; set; }
        public string Outcome { get; set; }
        public string EndedBy { get; set; }
        public long DurationMs { get; set; }
        public long OutputBytes { get; set; }
        public int OutputLines { get; set; }
        public bool Truncated { get; set; }
        public bool Interrupted { get; set; }
        public bool Completed { get; set; }
    }

    /// <summary>
    /// 运行历史索引：runs/history.jsonl，append-only 事件流。
    ///
    /// 不在「运行结束时写一条完整记录」：进程被 kill / 宿主崩溃时 end 事件会缺失，
    /// 那次运行就永远查不到。改为 start 先落盘、下次启动 RecoverAbandoned() 补
    /// run.abandoned（outcome=interrupted），查询时按 runId 归并。
    ///
    /// 保留策略按 天数 / 每任务次数 / 总字节 三重约束，淘汰时同步删除 run 文件并
    /// 原子重写索引，只保留仍存在文件的事件行。活跃运行（已 start 未 end）永不淘汰。
    /// </summary>
    public sealed class RunHistoryStore
    {
        public const string TypeStart = "run.start";
        public const string TypeEnd = "run.end";
        public const string TypeAbandoned = "run.abandoned";

        private static readonly Lazy<RunHistoryStore> _instance =
            new Lazy<RunHistoryStore>(() => new RunHistoryStore());

        /// <summary>进程内共享实例（任务只在宿主进程内运行，无跨进程写索引）。</summary>
        public static RunHistoryStore Instance { get { return _instance.Value; } }

        private readonly object _sync = new object();
        private readonly string _logsDir;
        private readonly string _indexPath;
        private readonly Dictionary<string, RunContext> _active =
            new Dictionary<string, RunContext>(StringComparer.Ordinal);
        private DateTime _lastPruneDate = DateTime.MinValue;

        public string IndexPath { get { return _indexPath; } }

        public RunHistoryStore(string logsDir = null)
        {
            _logsDir = string.IsNullOrEmpty(logsDir) ? TaskLogPaths.LogsDir : logsDir;
            _indexPath = Path.Combine(_logsDir, "runs", "history.jsonl");
        }

        /// <summary>把 history.jsonl 中的相对路径还原为绝对路径（尊重构造时注入的 logsDir）。</summary>
        public string ResolveLogPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return "";
            return Path.Combine(_logsDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        internal void RegisterActive(RunContext ctx)
        {
            if (ctx == null) return;
            lock (_sync) _active[ctx.RunId] = ctx;
        }

        internal void UnregisterActive(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return;
            lock (_sync) _active.Remove(runId);
        }

        /// <summary>UI 打开日志前调用：把仍在运行的日志缓冲全部落盘。</summary>
        public void FlushActive()
        {
            List<RunContext> snapshot;
            lock (_sync) snapshot = _active.Values.ToList();
            foreach (var ctx in snapshot)
            {
                try { ctx.Flush(); } catch { }
            }
        }

        public void RecordStart(RunContext ctx)
        {
            if (ctx == null) return;
            var e = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["type"] = TypeStart,
                ["runId"] = ctx.RunId,
                ["task"] = ctx.TaskName,
                ["trigger"] = ctx.TriggerReason,
                ["command"] = ctx.Command,
                ["args"] = ctx.Args,
                ["workDir"] = ctx.WorkDir,
                ["attempt"] = ctx.Attempt,
                ["startUtc"] = ctx.StartUtc.ToString("o"),
                ["startLocal"] = ctx.StartLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                ["logPath"] = ctx.RelativeLogPath
            };
            AppendLine(e);
            RegisterActive(ctx);
            MaybePrune();
        }

        public void RecordEnd(RunContext ctx, string outcome, int? exitCode, long durationMs, string endedBy)
        {
            if (ctx == null) return;
            var e = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["type"] = TypeEnd,
                ["runId"] = ctx.RunId,
                ["exitCode"] = exitCode.HasValue ? JToken.FromObject(exitCode.Value) : JValue.CreateNull(),
                ["outcome"] = outcome,
                ["durationMs"] = durationMs,
                ["outputBytes"] = ctx.OutputBytes,
                ["outputLines"] = ctx.OutputLines,
                ["truncated"] = ctx.Truncated,
                ["endedBy"] = endedBy ?? "exited"
            };
            AppendLine(e);
            UnregisterActive(ctx.RunId);
        }

        private void AppendLine(JObject e)
        {
            string line;
            try { line = JsonConvert.SerializeObject(e, Formatting.None); }
            catch { return; }
            lock (_sync)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_indexPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(_indexPath, line + "\n", new UTF8Encoding(false));
                }
                catch
                {
                    // 索引写失败不影响任务执行；run 日志文件本身仍是完整的排障入口
                }
            }
        }

        /// <summary>宿主启动时调用：补齐崩溃遗留的未闭合运行 + 执行一次保留清理。</summary>
        public void Initialize()
        {
            RecoverAbandoned();
            Prune();
        }

        /// <summary>扫描索引，为有 start 但无 end/abandoned 的运行补一条 interrupted 事件。</summary>
        public void RecoverAbandoned()
        {
            lock (_sync)
            {
                List<JObject> events = ReadEventsNoLock();
                if (events.Count == 0) return;
                var closed = new HashSet<string>(StringComparer.Ordinal);
                var started = new Dictionary<string, JObject>(StringComparer.Ordinal);
                foreach (var e in events)
                {
                    string type = (string)e["type"];
                    string id = (string)e["runId"];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (type == TypeEnd || type == TypeAbandoned) closed.Add(id);
                    else if (type == TypeStart && !started.ContainsKey(id)) started[id] = e;
                }
                foreach (var kv in started)
                {
                    // 本进程仍在运行的实例不能被误判为 interrupted（单测里可能并行启动多个 service）
                    if (_active.ContainsKey(kv.Key)) continue;
                    if (closed.Contains(kv.Key)) continue;
                    var e = new JObject
                    {
                        ["ts"] = DateTime.UtcNow.ToString("o"),
                        ["type"] = TypeAbandoned,
                        ["runId"] = kv.Key,
                        ["outcome"] = "interrupted",
                        ["endedBy"] = "host-restart"
                    };
                    AppendLineNoLock(e);
                }
            }
        }

        /// <summary>按任务 / 时间倒序查询运行历史；已归并 start/end/abandoned。</summary>
        public IReadOnlyList<RunHistoryEntry> Query(string taskName = null, int max = 200)
        {
            List<JObject> events;
            lock (_sync) events = ReadEventsNoLock();
            var map = Merge(events);
            IEnumerable<RunHistoryEntry> q = map.Values;
            if (!string.IsNullOrEmpty(taskName))
                q = q.Where(x => string.Equals(x.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
            return q.OrderByDescending(x => x.StartUtc).Take(Math.Max(1, max)).ToArray();
        }

        /// <summary>某任务最近一次运行的日志文件绝对路径；无记录或文件已清理时返回 null。</summary>
        public string GetLatestRunLogPath(string taskName)
        {
            var latest = Query(taskName, 1).FirstOrDefault();
            if (latest == null || string.IsNullOrEmpty(latest.LogPath)) return null;
            string full = ResolveLogPath(latest.LogPath);
            return File.Exists(full) ? full : null;
        }

        /// <summary>按 30 天 / 每任务 200 次 / 总 200MB 淘汰旧运行，并原子重写索引。</summary>
        public void Prune()
        {
            lock (_sync)
            {
                _lastPruneDate = DateTime.Today;
                List<string> lines = ReadLinesNoLock();
                if (lines.Count == 0) return;
                var events = ParseLines(lines);
                var entries = QueryFromEvents(events);
                var activeIds = new HashSet<string>(_active.Keys, StringComparer.Ordinal);

                var keep = new HashSet<string>(StringComparer.Ordinal);
                var perTask = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ordered = entries.OrderByDescending(x => x.StartUtc).ToList();
                DateTime cutoff = DateTime.UtcNow.AddDays(-TaskLogPaths.RetainDays);

                foreach (var entry in ordered)
                {
                    if (activeIds.Contains(entry.RunId)) { keep.Add(entry.RunId); continue; }
                    if (entry.StartUtc < cutoff) continue;
                    int count;
                    perTask.TryGetValue(entry.TaskName ?? "", out count);
                    if (count >= TaskLogPaths.MaxRunsPerTask) continue;
                    perTask[entry.TaskName ?? ""] = count + 1;
                    keep.Add(entry.RunId);
                }

                // 总字节约束：从最旧的开始淘汰，直到 runs 目录估算大小回到上限内
                long total = 0;
                foreach (var entry in ordered.Where(x => keep.Contains(x.RunId)))
                    total += FileSizeOf(entry);
                if (total > TaskLogPaths.MaxRunStorageBytes)
                {
                    foreach (var entry in ordered.Where(x => keep.Contains(x.RunId)).Reverse())
                    {
                        if (total <= TaskLogPaths.MaxRunStorageBytes) break;
                        if (activeIds.Contains(entry.RunId)) continue;
                        total -= FileSizeOf(entry);
                        keep.Remove(entry.RunId);
                    }
                }

                var removed = new HashSet<string>(entries.Select(x => x.RunId), StringComparer.Ordinal);
                removed.ExceptWith(keep);
                if (removed.Count == 0) return;

                foreach (var entry in entries.Where(x => removed.Contains(x.RunId)))
                {
                    try
                    {
                        string full = ResolveLogPath(entry.LogPath);
                        if (!string.IsNullOrEmpty(full) && File.Exists(full)) File.Delete(full);
                    }
                    catch { }
                }

                var keptLines = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    JObject e = events[i];
                    if (e == null) continue;
                    string id = (string)e["runId"];
                    if (!string.IsNullOrEmpty(id) && keep.Contains(id)) keptLines.Add(lines[i]);
                }
                try
                {
                    AtomicFile.WriteAllText(_indexPath,
                        string.Join("\n", keptLines) + (keptLines.Count > 0 ? "\n" : ""),
                        new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private void MaybePrune()
        {
            try
            {
                if (DateTime.Today == _lastPruneDate) return;
                Prune();
            }
            catch { }
        }

        private long FileSizeOf(RunHistoryEntry entry)
        {
            try
            {
                string full = ResolveLogPath(entry.LogPath);
                if (string.IsNullOrEmpty(full) || !File.Exists(full)) return 0;
                return new FileInfo(full).Length;
            }
            catch { return 0; }
        }

        private List<string> ReadLinesNoLock()
        {
            try
            {
                if (!File.Exists(_indexPath)) return new List<string>();
                return File.ReadAllLines(_indexPath, Encoding.UTF8).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            catch { return new List<string>(); }
        }

        private List<JObject> ReadEventsNoLock()
        {
            return ParseLines(ReadLinesNoLock());
        }

        private static List<JObject> ParseLines(List<string> lines)
        {
            var result = new List<JObject>(lines.Count);
            foreach (string line in lines) result.Add(TryParse(line));
            return result;
        }

        private static JObject TryParse(string line)
        {
            try
            {
                using (var reader = new JsonTextReader(new StringReader(line)))
                {
                    // 默认 DateParseHandling 会把 ISO 字符串转成 DateTime，之后取回字符串会丢失
                    // 时区信息，运行历史时间会按 UTC 原样显示（比本地时间少一个时区偏移）。
                    // 索引里的时间必须以原始字符串保留，交给 ParseUtc 显式解析。
                    reader.DateParseHandling = DateParseHandling.None;
                    return JObject.Load(reader);
                }
            }
            catch { return null; }
        }

        private static List<RunHistoryEntry> QueryFromEvents(List<JObject> events)
        {
            return Merge(events).Values.ToList();
        }

        private static Dictionary<string, RunHistoryEntry> Merge(List<JObject> events)
        {
            var map = new Dictionary<string, RunHistoryEntry>(StringComparer.Ordinal);
            foreach (var e in events)
            {
                if (e == null) continue;
                string id = (string)e["runId"];
                if (string.IsNullOrEmpty(id)) continue;
                string type = (string)e["type"];
                RunHistoryEntry entry;
                if (!map.TryGetValue(id, out entry))
                {
                    entry = new RunHistoryEntry
                    {
                        RunId = id,
                        TaskName = (string)e["task"] ?? "",
                        Trigger = (string)e["trigger"] ?? "",
                        Command = (string)e["command"] ?? "",
                        Args = (string)e["args"] ?? "",
                        WorkDir = (string)e["workDir"] ?? "",
                        LogPath = (string)e["logPath"] ?? "",
                        Attempt = (int?)e["attempt"] ?? 1,
                        StartUtc = ParseUtc((string)e["startUtc"]),
                        Outcome = "running"
                    };
                    map[id] = entry;
                }
                if (type == TypeEnd || type == TypeAbandoned)
                {
                    entry.Completed = true;
                    entry.Interrupted = type == TypeAbandoned;
                    entry.Outcome = (string)e["outcome"] ?? "unknown";
                    entry.EndedBy = (string)e["endedBy"] ?? "";
                    entry.EndUtc = ParseUtc((string)e["ts"]);
                    entry.ExitCode = (int?)e["exitCode"];
                    entry.DurationMs = (long?)e["durationMs"] ?? 0;
                    entry.OutputBytes = (long?)e["outputBytes"] ?? 0;
                    entry.OutputLines = (int?)e["outputLines"] ?? 0;
                    entry.Truncated = (bool?)e["truncated"] ?? false;
                }
            }
            return map;
        }

        private void AppendLineNoLock(JObject e)
        {
            try
            {
                string line = JsonConvert.SerializeObject(e, Formatting.None);
                string dir = Path.GetDirectoryName(_indexPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_indexPath, line + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime dt;
            if (!string.IsNullOrEmpty(value) && DateTime.TryParse(value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                return dt.ToUniversalTime();
            return DateTime.MinValue;
        }
    }
}
