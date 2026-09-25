using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Common;
using CarroDesk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Services.Tasks
{
    public class TaskLoadResult
    {
        public List<TaskDefinition> Tasks { get; set; } = new List<TaskDefinition>();
        public List<string> Errors { get; set; } = new List<string>();
        public bool FileCreated { get; set; }

        /// <summary>文件损坏并已备份 + 重建，调用方应提示用户。</summary>
        public bool FileRecovered { get; set; }

        /// <summary>损坏文件的备份路径（FileRecovered 为真时有值）。</summary>
        public string RecoveredBackupPath { get; set; }

        /// <summary>识别到的 schema 版本；字段缺失视为 1（历史数组格式）。</summary>
        public int SchemaVersion { get; set; } = TaskConfigService.CurrentSchemaVersion;

        public string RawJson { get; set; }
    }

    public static class TaskConfigService
    {
        /// <summary>
        /// 当前 schema 版本。
        ///
        /// 版本约定：**字段缺失即视为 1**——历史文件是裸 JSON 数组，没有 version 字段，
        /// 因此 v1 以"无版本标记"表达。读取端同时接受：
        ///   - 裸数组（v1，历史格式）
        ///   - 对象信封 { "version": n, "tasks": [...] }
        /// 这样未来引入 v2 时无需破坏既有文件；写入端仍保持裸数组格式不变，
        /// 避免改变 tray "编辑 tasks.json" 所面向的可手改文件形态。
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        public static string FilePath
        {
            get { return ConfigService.TaskFilePath; }
        }

        public static string SampleFilePath
        {
            get { return Path.Combine(ConfigService.DirPath, "tasks.sample.json"); }
        }

        public static TaskLoadResult LoadOrCreate()
        {
            var result = new TaskLoadResult();
            try
            {
                string dir = ConfigService.DirPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string logsDir = ConfigService.LogsDirPath;
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                string scriptsDir = ConfigService.ScriptsDirPath;
                if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);
            }
            catch { }

            AtomicFile.TryRestoreLatestBackup(FilePath);

            if (!File.Exists(FilePath))
            {
                try
                {
                    // 优先从随 exe 发布的样例文件复制（构建时自动复制到输出目录），不再走代码生成
                    string sample = TryReadSampleFile() ?? BuildSampleJson();
                    File.WriteAllText(FilePath, sample, Encoding.UTF8);
                    // also write sample file for reference
                    try { File.WriteAllText(SampleFilePath, sample, Encoding.UTF8); } catch { }
                    result.FileCreated = true;
                }
                catch (Exception ex)
                {
                    result.Errors.Add("failed to create tasks.json: " + ex.Message);
                    return result;
                }
            }
            else if (!File.Exists(SampleFilePath))
            {
                // tasks.json 已存在但 sample 缺失，补一份参考文件
                try
                {
                    string sample = TryReadSampleFile() ?? BuildSampleJson();
                    File.WriteAllText(SampleFilePath, sample, Encoding.UTF8);
                }
                catch { }
            }

            return Load();
        }

        public static TaskLoadResult Load()
        {
            var result = new TaskLoadResult();
            try
            {
                if (!File.Exists(FilePath))
                {
                    result.Errors.Add("tasks.json not found: " + FilePath);
                    return result;
                }
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                result.RawJson = json;
                if (string.IsNullOrWhiteSpace(json))
                {
                    // 空文件此前被静默当作"零任务"接受，用户看到的是"任务全没了"却毫无提示
                    result.Errors.Add("tasks.json is empty: " + FilePath);
                    return result;
                }

                int detectedVersion = DetectSchemaVersion(json);
                if (detectedVersion < 0)
                {
                    // 顶层结构都无法解析（ParseTasksJson 内部会吞掉异常并只记错误），
                    // 这里必须显式按"损坏文件"处理，否则用户会永远卡在加载失败上。
                    result.Errors.Add("tasks.json is not valid JSON: " + FilePath);
                    RecoverFromCorruptFile(result);
                    return result;
                }

                result.SchemaVersion = detectedVersion;
                if (result.SchemaVersion > CurrentSchemaVersion)
                {
                    result.Errors.Add("tasks.json schema version " + result.SchemaVersion +
                                      " is newer than supported " + CurrentSchemaVersion + " (read-only; file preserved)");
                    // 未来版本可能改变根结构，不能按 v1 解析失败后当作“损坏”重建，
                    // 否则一次旧版启动就可能把新版任务文件重写为空数组。
                    return result;
                }

                var tasks = ParseTasksJson(json, result.Errors);
                if (result.Errors.Any(e => e.StartsWith("[root]", StringComparison.OrdinalIgnoreCase)))
                {
                    RecoverFromCorruptFile(result);
                    return result;
                }
                // dedup by name
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tasks)
                {
                    string err = t.Validate();
                    if (err != null)
                    {
                        result.Errors.Add("task [" + (t.Name ?? "?") + "] invalid: " + err);
                        continue;
                    }
                    if (seen.Contains(t.Name))
                    {
                        result.Errors.Add("duplicate task name: " + t.Name + " (skipped)");
                        continue;
                    }
                    seen.Add(t.Name);
                    result.Tasks.Add(t);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("load exception: " + ex.Message);
                RecoverFromCorruptFile(result);
            }
            return result;
        }

        /// <summary>
        /// 识别 schema 版本。字段缺失一律视为 <see cref="CurrentSchemaVersion"/>，
        /// 因为 v1 是历史裸数组格式，本就没有版本标记。
        /// 返回 -1 表示顶层 JSON 结构本身无法解析（损坏文件）。
        /// </summary>
        private static int DetectSchemaVersion(string json)
        {
            try
            {
                using (var sr = new StringReader(json))
                using (var reader = new JsonTextReader(sr))
                {
                    var loadSettings = new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore
                    };

                    while (reader.TokenType == JsonToken.None || reader.TokenType == JsonToken.Comment)
                    {
                        if (!reader.Read()) break;
                    }
                    if (reader.TokenType == JsonToken.None) return CurrentSchemaVersion;

                    var obj = JToken.Load(reader, loadSettings) as JObject;
                    if (obj != null && obj["version"] != null)
                    {
                        int parsed;
                        if (int.TryParse(obj["version"].ToString(), out parsed)) return parsed;
                    }
                    return CurrentSchemaVersion;
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 损坏文件的备份与自愈。
        ///
        /// 原先只把损坏文件复制成 tasks.corrupt-*.json，tasks.json 本身仍是坏文件：
        /// 下次启动继续失败、永远不会恢复，用户面对的是"任务一直加载不出来"。
        /// 现在备份后重建一个可用的空任务文件，并通过 FileRecovered 让上层提示用户
        /// （原任务内容保留在备份文件里，可手工找回）。
        /// </summary>
        private static void RecoverFromCorruptFile(TaskLoadResult result)
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                string corruptPath = Path.Combine(
                    ConfigService.DirPath,
                    string.Format("tasks.corrupt-{0:yyyyMMddHHmmss}.json", DateTime.Now));

                File.Copy(FilePath, corruptPath, true);
                result.RecoveredBackupPath = corruptPath;
                result.Errors.Add("corrupt tasks.json backed up to: " + corruptPath);

                AtomicFile.WriteAllText(FilePath, "[]", Encoding.UTF8);
                result.FileRecovered = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add("corrupt recovery failed: " + ex.Message);
            }
        }

        public static bool Reload(out TaskLoadResult result)
        {
            result = Load();
            return result.Errors.Count == 0;
        }

        public static void Save(List<TaskDefinition> tasks)
        {
            if (tasks == null) tasks = new List<TaskDefinition>();
            var dir = ConfigService.DirPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var jArray = new JArray();
            foreach (var t in tasks)
            {
                if (t == null) continue;
                var item = new JObject
                {
                    ["name"] = t.Name ?? "",
                    ["enabled"] = t.Enabled,
                    ["trigger"] = SerializeTriggerToken(t.Trigger),
                    ["action"] = SerializeActionToken(t.Action),
                    ["options"] = SerializeOptionsToken(t.Options)
                };
                if (t.When != null && t.When.HasAny())
                {
                    item["when"] = SerializeWhenToken(t.When);
                }
                jArray.Add(item);
            }

            var json = jArray.ToString(Formatting.Indented);
            AtomicFile.WriteAllText(FilePath, json, Encoding.UTF8);
        }

        private static JObject SerializeTriggerToken(TaskTrigger tr)
        {
            var o = new JObject();
            if (tr == null) return o;
            o["type"] = !string.IsNullOrEmpty(tr.RawType) ? tr.RawType : tr.Type.ToString().ToLowerInvariant();
            switch (tr.Type)
            {
                case TaskTriggerType.Startup:
                    o["delaySec"] = tr.DelaySec;
                    break;
                case TaskTriggerType.Interval:
                    if (!string.IsNullOrWhiteSpace(tr.Every)) o["every"] = tr.Every;
                    else o["everySec"] = tr.EverySec;
                    break;
                case TaskTriggerType.Daily:
                    o["at"] = tr.At ?? "";
                    break;
                case TaskTriggerType.Cron:
                    o["expr"] = tr.Expr ?? "";
                    break;
                case TaskTriggerType.Idle:
                    o["afterMinutes"] = tr.AfterMinutes;
                    break;
                case TaskTriggerType.Hotkey:
                    o["hotkey"] = tr.Hotkey ?? "";
                    break;
                case TaskTriggerType.Watch:
                    o["path"] = tr.WatchPath ?? "";
                    if (!string.IsNullOrWhiteSpace(tr.WatchFilter) && tr.WatchFilter != "*.*") o["filter"] = tr.WatchFilter;
                    if (!string.IsNullOrWhiteSpace(tr.WatchEvent) && tr.WatchEvent != "created") o["event"] = tr.WatchEvent;
                    break;
            }
            return o;
        }

        private static JObject SerializeActionToken(TaskAction ac)
        {
            var o = new JObject();
            if (ac == null) return o;
            o["file"] = ac.File ?? "";
            if (!string.IsNullOrWhiteSpace(ac.Args)) o["args"] = ac.Args;
            if (!string.IsNullOrWhiteSpace(ac.WorkDir)) o["workDir"] = ac.WorkDir;
            return o;
        }

        private static JObject SerializeOptionsToken(TaskOptions op)
        {
            var o = new JObject();
            if (op == null) return o;
            o["hidden"] = op.Hidden;
            if (op.TimeoutSec != 0) o["timeoutSec"] = op.TimeoutSec;
            if (op.AllowConcurrent) o["allowConcurrent"] = true;
            if (op.Retry != 0) o["retry"] = op.Retry;
            if (!op.NotifyOnFailure) o["notifyOnFailure"] = false;
            if (!string.IsNullOrWhiteSpace(op.WorkDir)) o["workDir"] = op.WorkDir;
            return o;
        }

        private static JObject SerializeWhenToken(TaskCondition w)
        {
            var o = new JObject();
            if (w == null) return o;
            if (w.OnlyIdle) o["onlyIdle"] = true;
            if (w.AcPower) o["acPower"] = true;
            if (w.NetworkAvailable) o["networkAvailable"] = true;
            if (!string.IsNullOrWhiteSpace(w.FileExists)) o["fileExists"] = w.FileExists;
            if (!string.IsNullOrWhiteSpace(w.FileNotExists)) o["fileNotExists"] = w.FileNotExists;
            return o;
        }

        private static List<TaskDefinition> ParseTasksJson(string json, List<string> errors)
        {
            var list = new List<TaskDefinition>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            try
            {
                // JsonTextReader 原生支持 // 与 /* */ 注释，通过 CommentHandling.Ignore 自动忽略注释
                using (var sr = new StringReader(json))
                using (var reader = new JsonTextReader(sr))
                {
                    var loadSettings = new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore
                    };

                    while (reader.TokenType == JsonToken.None || reader.TokenType == JsonToken.Comment)
                    {
                        if (!reader.Read()) break;
                    }

                    if (reader.TokenType == JsonToken.None) return list;

                    var token = JToken.Load(reader, loadSettings);
                    if (token is JArray arr)
                    {
                        foreach (var item in arr)
                        {
                            if (item is JObject obj) list.Add(ParseTaskNode(obj, errors));
                            else errors.Add("tasks array item not an object");
                        }
                    }
                    else if (token is JObject obj)
                    {
                        if (obj["tasks"] is JArray tasksArr)
                        {
                            foreach (var item in tasksArr)
                            {
                                if (item is JObject taskObj) list.Add(ParseTaskNode(taskObj, errors));
                                else errors.Add("tasks array item not an object");
                            }
                        }
                        else if (obj["name"] != null)
                        {
                            list.Add(ParseTaskNode(obj, errors));
                        }
                        else
                        {
                            errors.Add("[root] object must contain a tasks array or a task name");
                        }
                    }
                    else
                    {
                        errors.Add("[root] tasks.json must be array or object");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("parse error: " + ex.Message);
            }
            return list;
        }

        private static TaskDefinition ParseTaskNode(JObject obj, List<string> errors)
        {
            var task = new TaskDefinition();
            try
            {
                task.Name = GetStr(obj, "name");
                task.Enabled = GetBool(obj, true, "enabled");

                // trigger
                var trigToken = obj["trigger"];
                if (trigToken is JObject td)
                {
                    var trig = new TaskTrigger();
                    trig.RawType = GetStr(td, "type");
                    TaskTriggerType parsedType;
                    if (!TryParseTriggerType(trig.RawType, out parsedType))
                        errors.Add("task [" + (task.Name ?? "?") + "] has missing or unknown trigger.type: " + (trig.RawType ?? "<empty>"));
                    trig.Type = parsedType;
                    trig.DelaySec = GetInt(td, trig.DelaySec, "delaySec", "delay");
                    trig.EverySec = GetInt(td, 0, "everySec", "intervalSec");
                    trig.Every = GetStr(td, "every");
                    trig.At = GetStr(td, "at", "time");
                    trig.Expr = GetStr(td, "expr", "cron");
                    trig.AfterMinutes = GetInt(td, 0, "afterMinutes", "after");
                    trig.Hotkey = GetStr(td, "hotkey", "key");
                    trig.WatchPath = GetStr(td, "watchPath", "path", "dir");
                    trig.WatchFilter = GetStr(td, "filter", "pattern");
                    if (string.IsNullOrWhiteSpace(trig.WatchFilter)) trig.WatchFilter = "*.*";
                    trig.WatchEvent = GetStr(td, "event", "watchEvent");
                    if (string.IsNullOrWhiteSpace(trig.WatchEvent)) trig.WatchEvent = "created";
                    task.Trigger = trig;
                }
                else if (trigToken != null && trigToken.Type == JTokenType.String)
                {
                    var trig = new TaskTrigger();
                    trig.RawType = trigToken.ToString();
                    TaskTriggerType parsedType;
                    if (!TryParseTriggerType(trig.RawType, out parsedType))
                        errors.Add("task [" + (task.Name ?? "?") + "] has missing or unknown trigger.type: " + (trig.RawType ?? "<empty>"));
                    trig.Type = parsedType;
                    task.Trigger = trig;
                }

                // action
                var actToken = obj["action"];
                if (actToken is JObject ad)
                {
                    var act = new TaskAction();
                    act.File = GetStr(ad, "file", "path", "command");
                    act.Args = GetStr(ad, "args", "arguments");
                    act.WorkDir = GetStr(ad, "workDir", "workingDirectory", "cwd");
                    task.Action = act;
                }
                else if (obj["file"] != null)
                {
                    var act = new TaskAction();
                    act.File = GetStr(obj, "file", "path", "command");
                    act.Args = GetStr(obj, "args", "arguments");
                    act.WorkDir = GetStr(obj, "workDir", "workingDirectory", "cwd");
                    task.Action = act;
                }

                // options
                var opt = new TaskOptions();
                if (obj["options"] is JObject od)
                {
                    opt.Hidden = GetBool(od, true, "hidden");
                    opt.TimeoutSec = GetInt(od, 0, "timeoutSec", "timeout");
                    opt.AllowConcurrent = GetBool(od, false, "allowConcurrent", "concurrent");
                    opt.Retry = GetInt(od, 0, "retry");
                    opt.WorkDir = GetStr(od, "workDir");
                    opt.NotifyOnFailure = GetBool(od, true, "notifyOnFailure", "notify");
                }
                if (obj["hidden"] != null) opt.Hidden = GetBool(obj, opt.Hidden, "hidden");
                if (obj["timeoutSec"] != null) opt.TimeoutSec = GetInt(obj, opt.TimeoutSec, "timeoutSec", "timeout");
                task.Options = opt;

                // when / condition
                var whenToken = obj["when"] ?? obj["condition"];
                if (whenToken is JObject wd)
                {
                    var cond = new TaskCondition();
                    cond.OnlyIdle = GetBool(wd, false, "onlyIdle");
                    cond.AcPower = GetBool(wd, false, "acPower");
                    cond.FileExists = GetStr(wd, "fileExists");
                    cond.FileNotExists = GetStr(wd, "fileNotExists");
                    cond.NetworkAvailable = GetBool(wd, false, "networkAvailable", "network");
                    task.When = cond;
                }
            }
            catch (Exception ex)
            {
                errors.Add("parse task [" + task.Name + "] error: " + ex.Message);
            }

            if (task.Trigger == null) task.Trigger = new TaskTrigger();
            if (task.Action == null) task.Action = new TaskAction();
            if (task.Options == null) task.Options = new TaskOptions();
            if (task.When == null) task.When = new TaskCondition();
            return task;
        }

        private static string GetStr(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return "";
            foreach (var key in keys)
            {
                var token = obj[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.ToString().Trim();
                }
            }
            return "";
        }

        private static int GetInt(JObject obj, int def, params string[] keys)
        {
            if (obj == null || keys == null) return def;
            foreach (var key in keys)
            {
                var token = obj[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    if (token.Type == JTokenType.Integer) return token.Value<int>();
                    var s = token.ToString().Trim();
                    if (int.TryParse(s, out var n)) return n;
                    var dur = TaskDefinition.ParseDuration(s);
                    if (dur > 0) return dur;
                }
            }
            return def;
        }

        private static bool GetBool(JObject obj, bool def, params string[] keys)
        {
            if (obj == null || keys == null) return def;
            foreach (var key in keys)
            {
                var token = obj[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                    var s = token.ToString().Trim().ToLowerInvariant();
                    if (s == "true" || s == "1" || s == "yes") return true;
                    if (s == "false" || s == "0" || s == "no") return false;
                }
            }
            return def;
        }

        public static bool TryParseTriggerType(string raw, out TaskTriggerType type)
        {
            type = TaskTriggerType.Unknown;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "startup":
                case "start":
                case "boot":
                    type = TaskTriggerType.Startup; return true;
                case "interval":
                case "every":
                case "periodic":
                    type = TaskTriggerType.Interval; return true;
                case "daily":
                case "day":
                    type = TaskTriggerType.Daily; return true;
                case "cron":
                case "schedule":
                    type = TaskTriggerType.Cron; return true;
                case "sessionlock":
                case "lock":
                case "session_lock":
                    type = TaskTriggerType.SessionLock; return true;
                case "sessionunlock":
                case "unlock":
                case "session_unlock":
                    type = TaskTriggerType.SessionUnlock; return true;
                case "idle":
                    type = TaskTriggerType.Idle; return true;
                case "manual":
                case "none":
                case "click":
                    type = TaskTriggerType.Manual; return true;
                case "hotkey":
                case "key":
                case "shortcut":
                    type = TaskTriggerType.Hotkey; return true;
                case "watch":
                case "filewatch":
                case "watcher":
                case "file":
                    type = TaskTriggerType.Watch; return true;
                default:
                    return false;
            }
        }

        public static string GetCanonicalTriggerTag(TaskTriggerType type)
        {
            switch (type)
            {
                case TaskTriggerType.Startup: return "startup";
                case TaskTriggerType.Interval: return "interval";
                case TaskTriggerType.Daily: return "daily";
                case TaskTriggerType.Cron: return "cron";
                case TaskTriggerType.SessionLock: return "sessionLock";
                case TaskTriggerType.SessionUnlock: return "sessionUnlock";
                case TaskTriggerType.Idle: return "idle";
                case TaskTriggerType.Manual: return "manual";
                case TaskTriggerType.Hotkey: return "hotkey";
                case TaskTriggerType.Watch: return "watch";
                default: return "unknown";
            }
        }

        private static string TryReadSampleFile()
        {
            // 构建时自动复制到输出目录的样例文件，运行时优先读取
            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tasks.sample.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "tasks.sample.json"),
                Path.Combine(Path.GetDirectoryName(typeof(TaskConfigService).Assembly.Location) ?? "", "tasks.sample.json"),
            };
            foreach (var p in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return File.ReadAllText(p, Encoding.UTF8);
                }
                catch { }
            }
            return null;
        }

        private static string BuildSampleJson()
        {
            // 回退：若随包样例文件缺失（极端情况），仍用硬编码兜底
            string fromFile = TryReadSampleFile();
            if (fromFile != null) return fromFile;
            return @"// CarroDesk AutoRun tasks - place alongside config.json
// docs: docs/AUTORUN-DESIGN.md / docs/USAGE.md
// Trigger types: startup | interval | daily | cron | sessionLock | sessionUnlock | idle | manual | hotkey | watch
// Scripts without path are resolved from: <DirPath>/scripts/  (portable: <exe>/app_data/scripts/, roaming: %AppData%/CarroDesk/scripts/)
// Supported: .ps1/.bat/.cmd/.vbs (hidden), .js -> node, .py/.pyw -> python (auto from PATH, hidden)
// Manual tasks appear in tray -> Tasks -> Manual Run (click to execute)
// Hotkey: Ctrl+Alt+Shift+Win + A-Z/0-9/F1-24 ; Watch: file watcher with debounce 500ms
[
  // startup: run 10s after login - bare name loads from scripts/
  // {
  //   ""name"": ""startup-notify"",
  //   ""enabled"": true,
  //   ""trigger"": { ""type"": ""startup"", ""delaySec"": 10 },
  //   ""action"": { ""file"": ""hello.js"", ""args"": ""--verbose"" },
  //   ""options"": { ""hidden"": true, ""timeoutSec"": 60 }
  // },

  // manual: click in tray Tasks -> Manual Run (Quick Launcher, replaces AHK tray)
  // {
  //   ""name"": ""quick-notepad"",
  //   ""trigger"": { ""type"": ""manual"" },
  //   ""action"": { ""file"": ""notepad.exe"" }
  // },

  // hotkey: global hotkey
  // {
  //   ""name"": ""hotkey-sync"",
  //   ""trigger"": { ""type"": ""hotkey"", ""hotkey"": ""Ctrl+Alt+S"" },
  //   ""action"": { ""file"": ""sync.py"" }
  // },

  // watch: file watcher
  // {
  //   ""name"": ""watch-downloads"",
  //   ""trigger"": { ""type"": ""watch"", ""path"": ""%USERPROFILE%/Downloads"", ""filter"": ""*.zip"", ""event"": ""created"" },
  //   ""action"": { ""file"": ""unzip.js"", ""args"": ""{{task}} {{yyyyMMdd}}"" }
  // },

  // interval: every 60s (or ""1h30m"")
  // {
  //   ""name"": ""heartbeat"",
  //   ""trigger"": { ""type"": ""interval"", ""everySec"": 3600 },
  //   ""action"": { ""file"": ""sync.bat"" }
  // },

  // daily: at 03:00 every day
  // {
  //   ""name"": ""daily-clean"",
  //   ""trigger"": { ""type"": ""daily"", ""at"": ""03:00"" },
  //   ""action"": { ""file"": ""clean.ps1"" },
  //   ""options"": { ""timeoutSec"": 600 }
  // },

  // with condition and template
  // {
  //   ""name"": ""backup-logs"",
  //   ""trigger"": { ""type"": ""interval"", ""every"": ""1h"" },
  //   ""action"": { ""file"": ""backup.js"", ""args"": ""--out backup-{{yyyyMMdd}}.zip"" },
  //   ""when"": { ""onlyIdle"": true, ""acPower"": true }
  // }
]
";
        }
    }
}
