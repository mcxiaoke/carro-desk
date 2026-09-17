using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

                        // 模块专属配置槽提取（兼容内嵌 JSON 字符串或直接内嵌对象）
                        this.AudioSwitchJson = ExtractSubJson(obj["AudioSwitch"]);
                        this.AppAutoMuteJson = ExtractSubJson(obj["AppAutoMute"]);
                        this.MonitorProfileJson = ExtractSubJson(obj["MonitorProfile"]);

                        return AppSettings.Merge(s);
                    }
                }
            }
            catch
            {
                // 若解析异常，安全回退到默认设置
            }
            return new AppSettings();
        }

        private static string ExtractSubJson(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return token.Value<string>() ?? "";
            return token.ToString(Formatting.None);
        }

        public void Save()
        {
            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            if (Current == null) Current = new AppSettings();

            var serializer = JsonSerializer.Create(SerializerSettings);
            var obj = JObject.FromObject(Current, serializer);

            // 存入模块专属配置字符串槽
            obj["AudioSwitch"] = this.AudioSwitchJson ?? "";
            obj["AppAutoMute"] = this.AppAutoMuteJson ?? "";
            obj["MonitorProfile"] = this.MonitorProfileJson ?? "";

            var json = obj.ToString(Formatting.Indented);
            File.WriteAllText(FilePath, json, Encoding.UTF8);
        }
    }
}
