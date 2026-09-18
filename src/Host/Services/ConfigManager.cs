using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Host.Services
{
    public class ConfigManager : IConfigManager, IConfigRegistry
    {
        private interface IConfigAdapter
        {
            object Get();
            void Save(object config);
        }

        private class ConfigAdapter<T> : IConfigAdapter where T : class, new()
        {
            private readonly Func<T> _getter;
            private readonly Action<T> _setter;

            public ConfigAdapter(Func<T> getter, Action<T> setter)
            {
                _getter = getter;
                _setter = setter;
            }

            public object Get() => _getter();
            public void Save(object config) => _setter(config as T);
        }

        private readonly ConfigService _underlying;
        private readonly Dictionary<string, IConfigAdapter> _adapters =
            new Dictionary<string, IConfigAdapter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<object>> _defaultFactories =
            new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase);

        public ConfigManager(ConfigService underlying)
        {
            _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        }

        public ConfigService Underlying => _underlying;
        public AppSettings Current => _underlying.Current;

        public event Action ConfigReloaded;

        public void Register<TConfig>(string moduleId, Func<TConfig> getter, Action<TConfig> setter) where TConfig : class, new()
        {
            if (string.IsNullOrEmpty(moduleId) || getter == null || setter == null) return;
            _adapters[moduleId] = new ConfigAdapter<TConfig>(getter, setter);
        }

        public void RegisterDefault<TConfig>(string moduleId, Func<TConfig> defaultFactory) where TConfig : class, new()
        {
            if (string.IsNullOrEmpty(moduleId) || defaultFactory == null) return;
            _defaultFactories[moduleId] = () => defaultFactory();
        }

        public void LoadOrCreate()
        {
            _underlying.LoadOrCreate();
        }

        public void Save()
        {
            _underlying.Save();
        }

        public void Reload()
        {
            _underlying.Reload();
            ConfigReloaded?.Invoke();
        }

        public T GetModuleConfig<T>(string moduleId) where T : class, new()
        {
            if (string.IsNullOrEmpty(moduleId)) return CreateInstance<T>(moduleId);

            // 1. 优先走模块注册的强类型适配器
            if (_adapters.TryGetValue(moduleId, out var adapter))
            {
                var val = adapter.Get() as T;
                if (val != null) return val;
            }

            // 2. 通用原生 JToken/JSON 配置解析
            var token = _underlying.GetModuleToken(moduleId);
            if (token != null && token.Type != JTokenType.Null)
            {
                try
                {
                    if (token is JObject jObj)
                    {
                        return jObj.ToObject<T>() ?? CreateInstance<T>(moduleId);
                    }
                    if (token.Type == JTokenType.String)
                    {
                        string str = token.Value<string>();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            return JsonConvert.DeserializeObject<T>(str) ?? CreateInstance<T>(moduleId);
                        }
                    }
                }
                catch { }
            }

            return CreateInstance<T>(moduleId);
        }

        private T CreateInstance<T>(string moduleId) where T : class, new()
        {
            if (!string.IsNullOrEmpty(moduleId) && _defaultFactories.TryGetValue(moduleId, out var factory))
            {
                try
                {
                    var res = factory() as T;
                    if (res != null) return res;
                }
                catch { }
            }

            var method = typeof(T).GetMethod("CreateDefault", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(T))
            {
                try
                {
                    var res = method.Invoke(null, null) as T;
                    if (res != null) return res;
                }
                catch { }
            }

            return new T();
        }

        public void SaveModuleConfig<T>(string moduleId, T config) where T : class
        {
            if (string.IsNullOrEmpty(moduleId) || config == null) return;

            // 1. 优先走模块注册的强类型适配器
            if (_adapters.TryGetValue(moduleId, out var adapter))
            {
                adapter.Save(config);
                return;
            }

            // 2. 通用原生 JToken 保存
            try
            {
                var token = JToken.FromObject(config);
                _underlying.SetModuleToken(moduleId, token);
                _underlying.Save();
            }
            catch { }
        }
    }
}
