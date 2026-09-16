using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using SimpleJSON;

namespace ScreenLock.Services.Localization
{
    public class I18nService : INotifyPropertyChanged
    {
        public static I18nService Instance { get; } = new I18nService();

        private readonly Dictionary<string, Dictionary<string, string>> _locales
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _languageNames
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string _currentLanguage = "zh-CN";
        private CultureInfo _currentCulture = new CultureInfo("zh-CN");

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action LanguageChanged;

        public string CurrentLanguage => _currentLanguage;
        public CultureInfo CurrentCulture => _currentCulture;
        public IReadOnlyDictionary<string, string> AvailableLanguages => _languageNames;

        public string this[string key] => Get(key);

        private I18nService()
        {
            LoadEmbeddedLocales();
            LoadExternalLocales();
        }

        public void Init(string configuredLanguage)
        {
            SetLanguage(configuredLanguage, notifyConfig: false);
        }

        public void SetLanguage(string langCode, bool notifyConfig = true)
        {
            string target = ResolveLanguageCode(langCode);
            if (string.Equals(_currentLanguage, target, StringComparison.OrdinalIgnoreCase) && _currentCulture != null)
            {
                return;
            }

            _currentLanguage = target;
            try
            {
                _currentCulture = new CultureInfo(target);
            }
            catch
            {
                _currentCulture = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? new CultureInfo("zh-CN")
                    : CultureInfo.InvariantCulture;
            }

            // 触发 WPF 索引器属性变更通知，刷新所有 {loc:Loc Key} 绑定
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

            LanguageChanged?.Invoke();
        }

        public string ResolveLanguageCode(string code)
        {
            if (string.IsNullOrEmpty(code) || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var sys = CultureInfo.CurrentUICulture.Name;
                if (sys.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-CN";
                }
                return "en-US";
            }

            if (_locales.ContainsKey(code)) return code;
            if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";

            return "en-US";
        }

        public string Get(string key, string defaultValue = null)
        {
            if (string.IsNullOrEmpty(key)) return defaultValue ?? string.Empty;

            // 1. 当前激活语言查找
            if (_locales.TryGetValue(_currentLanguage, out var currentDict) && currentDict.TryGetValue(key, out var val))
            {
                return val;
            }

            // 2. Fallback 回退机制：若当前非中文则退往 zh-CN，若为中文则退往 en-US
            string fallbackLang = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
            if (_locales.TryGetValue(fallbackLang, out var fallbackDict) && fallbackDict.TryGetValue(key, out val))
            {
                return val;
            }

            // 3. 兜底返回 defaultValue 或 key 本身
            return defaultValue ?? key;
        }

        public string Format(string key, params object[] args)
        {
            var pattern = Get(key);
            if (args == null || args.Length == 0) return pattern;
            try
            {
                return string.Format(_currentCulture, pattern, args);
            }
            catch
            {
                return pattern;
            }
        }

        private void LoadEmbeddedLocales()
        {
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            foreach (var name in names)
            {
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Contains("Locales"))
                {
                    try
                    {
                        using (var stream = asm.GetManifestResourceStream(name))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var content = reader.ReadToEnd();
                            ParseAndRegisterLocale(content);
                        }
                    }
                    catch { }
                }
            }
        }

        public void LoadExternalLocales()
        {
            try
            {
                string langDir = Path.Combine(ConfigService.DirPath, "lang");
                if (Directory.Exists(langDir))
                {
                    var files = Directory.GetFiles(langDir, "*.json");
                    foreach (var f in files)
                    {
                        try
                        {
                            var content = File.ReadAllText(f, Encoding.UTF8);
                            ParseAndRegisterLocale(content);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void ParseAndRegisterLocale(string json)
        {
            var node = JSONNode.Parse(json);
            if (node == null || !node.IsObject) return;

            var obj = node.AsObject;
            string code = "";
            string dispName = "";

            if (obj.HasKey("_meta") && obj["_meta"].IsObject)
            {
                var meta = obj["_meta"].AsObject;
                code = meta.HasKey("code") ? meta["code"].Value : "";
                dispName = meta.HasKey("name") ? meta["name"].Value : "";
            }

            if (string.IsNullOrEmpty(code)) return;
            if (string.IsNullOrEmpty(dispName)) dispName = code;

            if (!_locales.TryGetValue(code, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _locales[code] = dict;
            }

            _languageNames[code] = dispName;

            FlattenJson(obj, "", dict);
        }

        private void FlattenJson(JSONObject obj, string prefix, Dictionary<string, string> target)
        {
            foreach (var kvp in obj)
            {
                if (string.Equals(kvp.Key, "_meta", StringComparison.OrdinalIgnoreCase)) continue;

                string fullKey = string.IsNullOrEmpty(prefix) ? kvp.Key : prefix + "." + kvp.Key;
                if (kvp.Value is JSONObject childObj)
                {
                    FlattenJson(childObj, fullKey, target);
                }
                else
                {
                    target[fullKey] = kvp.Value.Value;
                }
            }
        }
    }
}
