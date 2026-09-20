using System;
using System.Collections.Generic;
using System.Linq;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 手写轻量单例容器（方案 A，见规范 §3.8）。
    /// 支持同一接口多注册、GetServices&lt;T&gt;、TryGetService 与释放。
    /// </summary>
    public class ServiceContainer : IServiceProvider, IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<Type, List<Registration>> _registrations = new Dictionary<Type, List<Registration>>();

        private sealed class Registration
        {
            public object Instance;
            public Func<IServiceProvider, object> Factory;
            public bool Created;
        }

        public void AddSingleton(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (_lock)
            {
                GetOrCreateList(serviceType).Add(new Registration { Instance = instance, Created = true });
            }
        }

        public void AddSingleton<TService>(TService instance) where TService : class
        {
            AddSingleton(typeof(TService), instance);
        }

        public void AddSingleton<TService>(Func<IServiceProvider, TService> factory) where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                GetOrCreateList(typeof(TService)).Add(new Registration
                {
                    Factory = sp => factory(sp)
                });
            }
        }

        public object GetService(Type serviceType)
        {
            if (serviceType == null) return null;

            // 定向解析：只构造第一个匹配的注册。
            // 此前实现先调用 ResolveAll() 再取 [0]，会把该类型下**所有**注册都实例化一遍，
            // 等于用一次解析的代价构造了全部实例（并可能触发不必要的副作用）。
            lock (_lock)
            {
                if (!_registrations.TryGetValue(serviceType, out var list)) return null;

                foreach (var reg in list)
                {
                    var value = Resolve(reg);
                    if (value != null) return value;
                }
                return null;
            }
        }

        public T GetService<T>() where T : class
        {
            return GetService(typeof(T)) as T;
        }

        public List<object> GetServices(Type serviceType)
        {
            if (serviceType == null) return new List<object>();
            return ResolveAll(serviceType);
        }

        public List<T> GetServices<T>() where T : class
        {
            return ResolveAll(typeof(T)).OfType<T>().ToList();
        }

        public bool TryGetService<T>(out T service) where T : class
        {
            service = GetService(typeof(T)) as T;
            return service != null;
        }

        private List<Registration> GetOrCreateList(Type serviceType)
        {
            if (!_registrations.TryGetValue(serviceType, out var list))
            {
                list = new List<Registration>();
                _registrations[serviceType] = list;
            }
            return list;
        }

        /// <summary>解析单个注册（首次解析时执行工厂并缓存）。调用方须已持有 _lock。</summary>
        private object Resolve(Registration reg)
        {
            if (reg == null) return null;
            if (reg.Created) return reg.Instance;
            if (reg.Factory == null) return null;

            var created = reg.Factory(this);
            if (created == null) return null;

            reg.Instance = created;
            reg.Created = true;
            return created;
        }

        private List<object> ResolveAll(Type serviceType)
        {
            var result = new List<object>();
            lock (_lock)
            {
                if (!_registrations.TryGetValue(serviceType, out var list)) return result;
                foreach (var reg in list)
                {
                    var value = Resolve(reg);
                    if (value != null) result.Add(value);
                }
            }
            return result;
        }

        private HashSet<object> _disposed = new HashSet<object>();

        public void Dispose()
        {
            List<object> all;
            lock (_lock)
            {
                all = _registrations.Values
                    .SelectMany(l => l.Where(r => r.Created))
                    .Select(r => r.Instance)
                    .ToList();
                _registrations.Clear();
            }

            foreach (var obj in ((IEnumerable<object>)all).Reverse())
            {
                if (obj == null || !_disposed.Add(obj)) continue;
                try
                {
                    (obj as IDisposable)?.Dispose();
                }
                catch { }
            }
        }
    }

    public static class ServiceProviderExtensions
    {
        public static T GetService<T>(this IServiceProvider provider) where T : class
        {
            if (provider == null) return null;
            return provider.GetService(typeof(T)) as T;
        }
    }
}