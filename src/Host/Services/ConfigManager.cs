using System;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Host.Services
{
    public class ConfigManager : IConfigManager
    {
        private readonly ConfigService _underlying;

        public ConfigManager(ConfigService underlying)
        {
            _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        }

        public ConfigService Underlying => _underlying;
        public AppSettings Current => _underlying.Current;

        public event Action ConfigReloaded;

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
            if (string.IsNullOrEmpty(moduleId)) return CreateInstance<T>();

            var token = _underlying.GetModuleToken(moduleId);
            if (token != null && token.Type != JTokenType.Null)
            {
                try
                {
                    if (token is JObject jObj)
                    {
                        return jObj.ToObject<T>() ?? CreateInstance<T>();
                    }
                    if (token.Type == JTokenType.String)
                    {
                        string str = token.Value<string>();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            return JsonConvert.DeserializeObject<T>(str) ?? CreateInstance<T>();
                        }
                    }
                }
                catch { }
            }

            return CreateInstance<T>();
        }

        private static T CreateInstance<T>() where T : class, new()
        {
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
