using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CarroDesk.Common;
using CarroDesk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Services
{
    public class ConfigService
    {
        public static string AppDataDirPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CarroDesk"); }
        }

        public static string PortableFlagPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.ini"); }
        }

        public static string PortableDataDirPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_data"); }
        }

        public static bool IsPortableMode
        {
            get { return File.Exists(PortableFlagPath); }
        }

        public static string DirPath
        {
            get { return IsPortableMode ? PortableDataDirPath : AppDataDirPath; }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirPath, "config.json"); }
        }

        public static string TaskFilePath
        {
            get { return Path.Combine(DirPath, "tasks.json"); }
        }

        public static string LogsDirPath
        {
            get { return Path.Combine(DirPath, "logs"); }
        }

        public static string ScriptsDirPath
        {
            get { return Path.Combine(DirPath, "scripts"); }
        }

        public static string GlobalTasksEnabledPath
        {
            get { return Path.Combine(DirPath, "tasks.enabled"); }
        }

        public static string ConfigSamplePath
        {
            get { return Path.Combine(DirPath, "config.sample.json"); }
        }

        public AppSettings Current { get; private set; }

        // 模块专属配置（JSON 字符串槽），随 config.json 一起落盘，避免模块配置仅存内存
        public string AudioSwitchJson { get; set; } = "";
        public string AppAutoMuteJson { get; set; } = "";
        public string MonitorProfileJson { get; set; } = "";
        public string AwakeJson { get; set; } = "";

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public void LoadOrCreate()
        {
            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                Save();
                EnsureSampleCopied();
                return;
            }
            Current = ReadFile();
            EnsureSampleCopied();
        }

        private static void EnsureSampleCopied()
        {
            // 构建时已复制到 exe 目录的 config.sample.json，首次运行时同步一份到 DirPath 供参考
            try
            {
                if (File.Exists(ConfigSamplePath)) return;
                string[] candidates = new string[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.sample.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "config.sample.json"),
                };
                foreach (var p in candidates)
                {
                    if (File.Exists(p))
                    {
                        File.Copy(p, ConfigSamplePath, false);
                        break;
                    }
                }
            }
            catch { }
        }

        public bool Reload()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var settings = ReadFile();
                Current = settings;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private AppSettings ReadFile()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

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

                    if (reader.TokenType == JsonToken.None) return new AppSettings();

                    var token = JToken.Load(reader, loadSettings);
                    if (token is JObject obj)
                    {
                        var serializer = JsonSerializer.Create(SerializerSettings);
                        var s = obj.ToObject<AppSettings>(serializer) ?? new AppSettings();

                        _moduleConfigs.Clear();
                        foreach (var prop in obj.Properties())
                        {
                            if (IsCoreAppSetting(prop.Name)) continue;
                            var val = prop.Value;
                            if (val != null && val.Type == JTokenType.String)
                            {
                                string str = val.Value<string>();
                                if (!string.IsNullOrWhiteSpace(str) && (str.TrimStart().StartsWith("{") || str.TrimStart().StartsWith("[")))
                                {
                                    try { val = JToken.Parse(str); } catch { }
                                }
                            }
                            _moduleConfigs[prop.Name] = val;
                        }

                        // 模块专属配置槽提取（兼容向后属性访问）
                        this.AudioSwitchJson = GetModuleRawJson("AudioSwitch");
                        this.AppAutoMuteJson = GetModuleRawJson("AppAutoMute");
                        this.MonitorProfileJson = GetModuleRawJson("MonitorProfile");
                        this.AwakeJson = GetModuleRawJson("Awake");

                        return AppSettings.Merge(s);
                    }
                }
            }
            catch
            {
                // 若解析异常，安全备份损坏文件，避免直接被覆盖丢失
                try
                {
                    if (File.Exists(FilePath))
                    {
                        string corruptPath = Path.Combine(DirPath, $"config.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
                        File.Copy(FilePath, corruptPath, true);
                    }
                }
                catch { }
            }
            return new AppSettings();
        }

        private static bool IsCoreAppSetting(string name)
        {
            switch (name)
            {
                case "IdleMinutes":
                case "AutoStart":
                case "ShowClock":
                case "OverlayOpacity":
                case "PinSalt":
                case "PinHash":
                case "TasksEnabled":
                case "UnlockOnResume":
                case "Language":
                case "ExcludeProcesses":
                case "FloatingPanelHotkey":
                case "FloatingPanelPosition":
                case "FloatingPanelPinned":
                case "FloatingPanelLocked":
                case "FloatingPanelX":
                case "FloatingPanelY":
                    return true;
                default:
                    return false;
            }
        }

        private readonly Dictionary<string, JToken> _moduleConfigs = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

        public JToken GetModuleToken(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return null;
            if (_moduleConfigs.TryGetValue(moduleId, out var token)) return token;

            if (string.Equals(moduleId, "AudioSwitch", StringComparison.OrdinalIgnoreCase)) return TryParseToken(AudioSwitchJson);
            if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase)) return TryParseToken(AppAutoMuteJson);
            if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase)) return TryParseToken(MonitorProfileJson);
            if (string.Equals(moduleId, "Awake", StringComparison.OrdinalIgnoreCase)) return TryParseToken(AwakeJson);

            return null;
        }

        public void SetModuleToken(string moduleId, JToken token)
        {
            if (string.IsNullOrEmpty(moduleId)) return;
            _moduleConfigs[moduleId] = token;

            string raw = token != null ? token.ToString(Formatting.None) : "";
            if (string.Equals(moduleId, "AudioSwitch", StringComparison.OrdinalIgnoreCase)) AudioSwitchJson = raw;
            else if (string.Equals(moduleId, "AppAutoMute", StringComparison.OrdinalIgnoreCase)) AppAutoMuteJson = raw;
            else if (string.Equals(moduleId, "MonitorProfile", StringComparison.OrdinalIgnoreCase)) MonitorProfileJson = raw;
            else if (string.Equals(moduleId, "Awake", StringComparison.OrdinalIgnoreCase)) AwakeJson = raw;
        }

        private string GetModuleRawJson(string moduleId)
        {
            if (_moduleConfigs.TryGetValue(moduleId, out var token) && token != null)
            {
                if (token.Type == JTokenType.String) return token.Value<string>() ?? "";
                return token.ToString(Formatting.None);
            }
            return "";
        }

        private static JToken TryParseToken(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JToken.Parse(json); } catch { return null; }
        }

        public void Save()
        {
            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            if (Current == null) Current = new AppSettings();

            var serializer = JsonSerializer.Create(SerializerSettings);
            var obj = JObject.FromObject(Current, serializer);

            // 保持同步已有旧字符串槽
            if (!string.IsNullOrEmpty(AudioSwitchJson) && !_moduleConfigs.ContainsKey("AudioSwitch"))
                _moduleConfigs["AudioSwitch"] = TryParseToken(AudioSwitchJson) ?? (JToken)AudioSwitchJson;
            if (!string.IsNullOrEmpty(AppAutoMuteJson) && !_moduleConfigs.ContainsKey("AppAutoMute"))
                _moduleConfigs["AppAutoMute"] = TryParseToken(AppAutoMuteJson) ?? (JToken)AppAutoMuteJson;
            if (!string.IsNullOrEmpty(MonitorProfileJson) && !_moduleConfigs.ContainsKey("MonitorProfile"))
                _moduleConfigs["MonitorProfile"] = TryParseToken(MonitorProfileJson) ?? (JToken)MonitorProfileJson;
            if (!string.IsNullOrEmpty(AwakeJson) && !_moduleConfigs.ContainsKey("Awake"))
                _moduleConfigs["Awake"] = TryParseToken(AwakeJson) ?? (JToken)AwakeJson;

            // 存入标准原生 JSON 对象
            foreach (var kvp in _moduleConfigs)
            {
                if (kvp.Value != null) obj[kvp.Key] = kvp.Value;
            }

            var json = obj.ToString(Formatting.Indented);
            AtomicFile.WriteAllText(FilePath, json, Encoding.UTF8);
        }
    }
}
