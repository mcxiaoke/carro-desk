using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        public string RawJson { get; set; }
    }

    public static class TaskConfigService
    {
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
                    return result;
                }
                var tasks = ParseTasksJson(json, result.Errors);
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
            }
            return result;
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
            File.WriteAllText(FilePath, json, Encoding.UTF8);
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
                            // empty object or unknown
                        }
                    }
                    else
                    {
                        errors.Add("tasks.json must be array or object");
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
                    trig.Type = ParseTriggerType(trig.RawType);
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
                    trig.Type = ParseTriggerType(trig.RawType);
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

        private static TaskTriggerType ParseTriggerType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return TaskTriggerType.Startup;
            raw = raw.Trim().ToLowerInvariant();
            switch (raw)
            {
                case "startup": return TaskTriggerType.Startup;
                case "start": return TaskTriggerType.Startup;
                case "boot": return TaskTriggerType.Startup;
                case "interval": return TaskTriggerType.Interval;
                case "every": return TaskTriggerType.Interval;
                case "periodic": return TaskTriggerType.Interval;
                case "daily": return TaskTriggerType.Daily;
                case "day": return TaskTriggerType.Daily;
                case "cron": return TaskTriggerType.Cron;
                case "schedule": return TaskTriggerType.Cron;
                case "sessionlock": return TaskTriggerType.SessionLock;
                case "lock": return TaskTriggerType.SessionLock;
                case "session_lock": return TaskTriggerType.SessionLock;
                case "sessionunlock": return TaskTriggerType.SessionUnlock;
                case "unlock": return TaskTriggerType.SessionUnlock;
                case "session_unlock": return TaskTriggerType.SessionUnlock;
                case "idle": return TaskTriggerType.Idle;
                case "manual": return TaskTriggerType.Manual;
                case "none": return TaskTriggerType.Manual;
                case "click": return TaskTriggerType.Manual;
                case "hotkey": return TaskTriggerType.Hotkey;
                case "key": return TaskTriggerType.Hotkey;
                case "shortcut": return TaskTriggerType.Hotkey;
                case "watch": return TaskTriggerType.Watch;
                case "filewatch": return TaskTriggerType.Watch;
                case "watcher": return TaskTriggerType.Watch;
                case "file": return TaskTriggerType.Watch;
                default: return TaskTriggerType.Startup;
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
