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
        private const string LogModuleId = "Config";

        private readonly ConfigService _underlying;
        private readonly ILoggerService _logger;

        public ConfigManager(ConfigService underlying, ILoggerService logger = null)
        {
            _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
            _logger = logger;
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
                catch (Exception ex)
                {
                    // 反序列化失败必须留痕：此前静默回退默认值，会让用户在无任何提示的情况下
                    // 丢失整段模块配置（且回退值可能在下次 Save 时覆盖原配置）。
                    LogError($"模块 '{moduleId}' 配置反序列化为 {typeof(T).Name} 失败，已回退默认配置", ex);
                }
            }

            return CreateInstance<T>();
        }

        /// <summary>按类型缓存 CreateDefault 的反射查找结果，避免每次解析配置都走 GetMethod。</summary>
        private static class CreateDefaultMethodCache<T> where T : class, new()
        {
            public static readonly System.Reflection.MethodInfo Method =
                typeof(T).GetMethod(
                    "CreateDefault",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
        }

        private T CreateInstance<T>() where T : class, new()
        {
            var method = CreateDefaultMethodCache<T>.Method;
            if (method != null && method.ReturnType == typeof(T))
            {
                try
                {
                    var res = method.Invoke(null, null) as T;
                    if (res != null) return res;
                }
                catch (Exception ex)
                {
                    LogError($"{typeof(T).Name}.CreateDefault() 调用失败，已回退 new()", ex);
                }
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
            catch (Exception ex)
            {
                // 不向上抛：调用方遍布 UI 事件与定时器回调，抛出会形成新的崩溃面。
                // 但绝不允许无声失败——必须落盘日志，否则表现为"保存成功但配置未变"。
                LogError($"保存模块 '{moduleId}' 配置失败（改动未持久化）", ex);
            }
        }

        private void LogError(string message, Exception ex)
        {
            _logger?.LogError(LogModuleId, message, ex);
        }
    }
}
