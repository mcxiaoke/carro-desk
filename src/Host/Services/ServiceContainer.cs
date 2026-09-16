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
            var resolved = ResolveAll(serviceType);
            return resolved.Count > 0 ? resolved[0] : null;
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
            var resolved = ResolveAll(typeof(T));
            service = resolved.OfType<T>().FirstOrDefault();
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

        private List<object> ResolveAll(Type serviceType)
        {
            var result = new List<object>();
            lock (_lock)
            {
                if (!_registrations.TryGetValue(serviceType, out var list)) return result;
                foreach (var reg in list)
                {
                    object value;
                    if (reg.Created)
                    {
                        value = reg.Instance;
                    }
                    else
                    {
                        if (reg.Factory == null) continue;
                        var created = reg.Factory(this);
                        if (created == null) continue;
                        reg.Instance = created;
                        reg.Created = true;
                        value = created;
                    }
                    result.Add(value);
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