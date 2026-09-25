using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// 数据目录覆盖（自动化测试 / CI 隔离用，优先级最高）。
        /// 设为非空后，<see cref="DirPath"/> 及其派生的全部路径都指向该目录，
        /// 避免测试运行污染用户真实的 %AppData%\CarroDesk 配置。
        /// 生产代码不得设置此属性。
        /// </summary>
        public static string DataDirOverride { get; set; }

        /// <summary>环境变量名：设置后覆盖数据目录（便携部署 / 自动化测试用）。</summary>
        public const string DataDirEnvVarName = "CARRODESK_DATA_DIR";

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
            get
            {
                var overridden = ResolveDataDirOverride();
                if (overridden != null) return overridden;
                return IsPortableMode ? PortableDataDirPath : AppDataDirPath;
            }
        }

        private static string ResolveDataDirOverride()
        {
            var explicitOverride = DataDirOverride;
            if (!string.IsNullOrWhiteSpace(explicitOverride)) return explicitOverride;
            try
            {
                var fromEnv = Environment.GetEnvironmentVariable(DataDirEnvVarName);
                if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
            }
            catch
            {
                // 环境变量读取失败时退回默认目录，不影响正常启动
            }
            return null;
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
        public bool LastLoadSucceeded { get { return _lastReadSucceeded; } }
        public bool LastLoadIoFailure { get; private set; }
        public string LastLoadError { get; private set; }

        /// <summary>
        /// 串行化配置的内存态（Current / _moduleConfigs）与磁盘读写。
        /// 本类是被宿主与多个模块跨线程共享的可变单例：
        /// UI 线程（设置界面保存）、Dispatcher 线程（模块 OnConfigReloaded）、
        /// 线程池线程（任务执行、剪贴板落盘）都会写入。
        /// 缺少此锁时，Save() 遍历 _moduleConfigs 的过程中被 SetModuleToken 修改，
        /// 会抛 "Collection was modified" 并被上层静默吞掉，表现为"保存成功但磁盘无变化"。
        /// 注意：Monitor 可重入，因此 SaveCore/ReadFileCore 内部再次获取同一把锁是安全的。
        /// </summary>
        private readonly object _ioLock = new object();
        private bool _lastReadSucceeded;

        private static readonly HashSet<string> HostSettingNames = new HashSet<string>(
            typeof(AppSettings).GetProperties().Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase
        );

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public void LoadOrCreate()
        {
            lock (_ioLock)
            {
                if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);

                // 清理进程上次被强杀时遗留的孤儿临时文件（正常路径下 finally 会删除）
                AtomicFile.CleanupStaleTempFiles(DirPath, TimeSpan.FromHours(1));
                AtomicFile.TryRestoreLatestBackup(FilePath);

                if (!File.Exists(FilePath))
                {
                    Current = new AppSettings();
                    SaveCore();
                    _lastReadSucceeded = true;
                    LastLoadIoFailure = false;
                    LastLoadError = null;
                    EnsureSampleCopied();
                    return;
                }
                Current = ReadFile();
                EnsureSampleCopied();
            }
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
            lock (_ioLock)
            {
                if (!File.Exists(FilePath)) return false;
                var settings = ReadFile();
                if (!_lastReadSucceeded) return false;
                Current = settings;
                return true;
            }
        }

        /// <summary>读取并解析配置文件。调用方必须已持有 <see cref="_ioLock"/>。</summary>
        private AppSettings ReadFile()
        {
            LastLoadIoFailure = false;
            LastLoadError = null;
            // 解析采用“候选快照 → 完整成功后提交”的事务模型。失败时必须保留
            // 上一次有效 Current/_moduleConfigs，绝不能形成宿主默认值 + 旧模块配置的混合态。
            var previousCurrent = Current;
            var previousModules = _moduleConfigs.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(FilePath)) throw new FileNotFoundException("config.json not found", FilePath);
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("config.json is empty");

                JObject obj;
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

                    if (reader.TokenType == JsonToken.None) throw new InvalidDataException("config.json has no JSON value");
                    var token = JToken.Load(reader, loadSettings);
                    obj = token as JObject;
                    if (obj == null) throw new InvalidDataException("config.json root must be a JSON object");
                }

                var serializer = JsonSerializer.Create(SerializerSettings);
                var loaded = obj.ToObject<AppSettings>(serializer) ?? new AppSettings();
                var merged = AppSettings.Merge(loaded);
                var moduleConfigs = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

                foreach (var prop in obj.Properties())
                {
                    if (HostSettingNames.Contains(prop.Name)) continue;
                    var val = prop.Value;
                    if (val != null && val.Type == JTokenType.String)
                    {
                        string str = val.Value<string>();
                        if (!string.IsNullOrWhiteSpace(str) &&
                            (str.TrimStart().StartsWith("{") || str.TrimStart().StartsWith("[")))
                        {
                            try { val = JToken.Parse(str); } catch { }
                        }
                    }
                    moduleConfigs[prop.Name] = val;
                }

                // 到此才提交完整候选快照。
                Current = merged;
                _moduleConfigs.Clear();
                foreach (var kvp in moduleConfigs) _moduleConfigs[kvp.Key] = kvp.Value;

                bool migrated = MigrateLegacyConfigs(obj);
                if (migrated)
                {
                    try { SaveCore(); } catch { }
                }

                _lastReadSucceeded = true;
                return merged;
            }
            catch (Exception ex)
            {
                Current = previousCurrent ?? new AppSettings();
                _moduleConfigs.Clear();
                foreach (var kvp in previousModules) _moduleConfigs[kvp.Key] = kvp.Value;
                _lastReadSucceeded = false;
                LastLoadError = ex.Message;
                LastLoadIoFailure = ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is System.Security.SecurityException;

                // 只对确定的内容损坏做备份；短暂 I/O/共享冲突不应制造“损坏”副本。
                if (ex is JsonException || ex is InvalidDataException || ex is FormatException)
                {
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

                return Current;
            }
        }

        private readonly Dictionary<string, JToken> _moduleConfigs = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

        public JToken GetModuleToken(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return null;
            lock (_ioLock)
            {
                return _moduleConfigs.TryGetValue(moduleId, out var token) ? token : null;
            }
        }

        /// <summary>在同一锁内完成模块 token 替换与全量落盘；失败恢复原 token 后继续抛出。</summary>
        public void SaveModuleTokenAndSave(string moduleId, JToken token)
        {
            if (string.IsNullOrEmpty(moduleId)) throw new ArgumentNullException(nameof(moduleId));
            lock (_ioLock)
            {
                JToken previous = null;
                bool hadPrevious = _moduleConfigs.TryGetValue(moduleId, out previous) && previous != null;
                _moduleConfigs[moduleId] = token;
                try
                {
                    SaveCore();
                }
                catch
                {
                    if (hadPrevious) _moduleConfigs[moduleId] = previous;
                    else _moduleConfigs[moduleId] = null;
                    throw;
                }
            }
        }

        public void SetModuleToken(string moduleId, JToken token)
        {
            if (string.IsNullOrEmpty(moduleId)) return;
            lock (_ioLock)
            {
                _moduleConfigs[moduleId] = token;
            }
        }

        public void Save()
        {
            lock (_ioLock)
            {
                SaveCore();
            }
        }

        /// <summary>落盘实现。调用方必须已持有 <see cref="_ioLock"/>，以保证写入顺序与内存态一致。</summary>
        private void SaveCore()
        {
            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            if (Current == null) Current = new AppSettings();

            var serializer = JsonSerializer.Create(SerializerSettings);
            var obj = JObject.FromObject(Current, serializer);

            // 存入标准原生 JSON 对象（快照遍历，避免并发修改导致集合异常）
            foreach (var kvp in _moduleConfigs.ToList())
            {
                if (kvp.Value != null) obj[kvp.Key] = kvp.Value;
            }

            var json = obj.ToString(Formatting.Indented);
            AtomicFile.WriteAllText(FilePath, json, Encoding.UTF8);
        }

        private static readonly string[] LegacyScreenLockProps = new[]
        {
            "IdleMinutes", "ShowClock", "OverlayOpacity", "ExcludeProcesses", "UnlockOnResume"
        };

        private bool MigrateLegacyConfigs(JObject rootObj)
        {
            if (rootObj == null) return false;
            bool changed = false;

            // 1. 迁移 ScreenLock 历史扁平配置
            bool hasLegacyScreenLock = LegacyScreenLockProps.Any(p => rootObj.Property(p) != null || _moduleConfigs.ContainsKey(p));
            if (hasLegacyScreenLock)
            {
                JObject slObj = null;
                if (_moduleConfigs.TryGetValue("ScreenLock", out var slToken) && slToken is JObject existingSl)
                {
                    slObj = existingSl;
                }
                else
                {
                    slObj = new JObject
                    {
                        ["Enabled"] = true,
                        ["IdleMinutes"] = 5,
                        ["ShowClock"] = true,
                        ["OverlayOpacity"] = 0.88,
                        ["ExcludeProcesses"] = new JArray(),
                        ["UnlockOnResume"] = true
                    };
                    _moduleConfigs["ScreenLock"] = slObj;
                    changed = true;
                }

                if (TryGetLegacyValue(rootObj, "IdleMinutes", out var idleVal))
                {
                    slObj["IdleMinutes"] = idleVal;
                    changed = true;
                }

                if (TryGetLegacyValue(rootObj, "ShowClock", out var clockVal))
                {
                    slObj["ShowClock"] = clockVal;
                    changed = true;
                }

                if (TryGetLegacyValue(rootObj, "OverlayOpacity", out var opacityVal))
                {
                    slObj["OverlayOpacity"] = opacityVal;
                    changed = true;
                }

                if (TryGetLegacyValue(rootObj, "UnlockOnResume", out var resumeVal))
                {
                    slObj["UnlockOnResume"] = resumeVal;
                    changed = true;
                }

                if (TryGetLegacyValue(rootObj, "ExcludeProcesses", out var exclVal))
                {
                    if (exclVal is JArray jarr)
                    {
                        if (jarr.Count > 0 || slObj["ExcludeProcesses"] == null)
                        {
                            slObj["ExcludeProcesses"] = jarr;
                            changed = true;
                        }
                    }
                    else if (exclVal.Type == JTokenType.String && !string.IsNullOrWhiteSpace(exclVal.Value<string>()))
                    {
                        slObj["ExcludeProcesses"] = exclVal;
                        changed = true;
                    }
                }

                foreach (var p in LegacyScreenLockProps)
                {
                    if (_moduleConfigs.Remove(p)) changed = true;
                }
            }

            // 2. 迁移 TaskScheduler 历史 TasksEnabled 配置
            if (TryGetLegacyValue(rootObj, "TasksEnabled", out var tasksEnabledVal))
            {
                JObject tsObj = null;
                if (_moduleConfigs.TryGetValue("TaskScheduler", out var tsToken) && tsToken is JObject existingTs)
                {
                    tsObj = existingTs;
                }
                else
                {
                    tsObj = new JObject
                    {
                        ["GlobalEnabled"] = true,
                        ["TasksFile"] = "tasks.json"
                    };
                    _moduleConfigs["TaskScheduler"] = tsObj;
                }
                tsObj["GlobalEnabled"] = tasksEnabledVal;
                _moduleConfigs.Remove("TasksEnabled");
                changed = true;
            }

            // 3. 清理无效的空字符串模块节点（如 "AudioSwitch": "", "AppAutoMute": ""）
            var emptyModuleKeys = _moduleConfigs
                .Where(kvp => kvp.Value != null && kvp.Value.Type == JTokenType.String && string.IsNullOrWhiteSpace(kvp.Value.Value<string>()))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var k in emptyModuleKeys)
            {
                _moduleConfigs.Remove(k);
                changed = true;
            }

            return changed;
        }

        private bool TryGetLegacyValue(JObject rootObj, string propName, out JToken val)
        {
            val = null;
            if (_moduleConfigs.TryGetValue(propName, out var token) && token != null && token.Type != JTokenType.Null)
            {
                val = token;
                return true;
            }
            var p = rootObj.Property(propName);
            if (p != null && p.Value != null && p.Value.Type != JTokenType.Null)
            {
                val = p.Value;
                return true;
            }
            return false;
        }
    }
}
