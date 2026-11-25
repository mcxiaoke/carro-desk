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
        private volatile bool _isDisposed;

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
            if (serviceType == null || _isDisposed) return null;

            Registration[] snapshot;
            lock (_lock)
            {
                if (_isDisposed || !_registrations.TryGetValue(serviceType, out var list) || list.Count == 0)
                    return null;
                snapshot = list.ToArray();
            }

            // 定向解析：在容器锁外执行工厂构造，避免工厂内部解析其他服务时长期占用全局锁。
            foreach (var reg in snapshot)
            {
                var value = Resolve(reg);
                if (value != null) return value;
            }
            return null;
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

        private object Resolve(Registration reg)
        {
            if (reg == null || _isDisposed) return null;
            if (reg.Created) return reg.Instance;
            if (reg.Factory == null) return null;

            object lateDisposed = null;
            object result = null;
            lock (reg)
            {
                if (reg.Created) return reg.Instance;
                if (_isDisposed) return null;

                var created = reg.Factory(this);
                if (created == null) return null;

                if (_isDisposed)
                {
                    lateDisposed = created;
                }
                else
                {
                    reg.Instance = created;
                    reg.Created = true;
                    result = created;
                }
            }

            if (lateDisposed is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
            return result;
        }

        private List<object> ResolveAll(Type serviceType)
        {
            var result = new List<object>();
            if (_isDisposed) return result;

            Registration[] snapshot;
            lock (_lock)
            {
                if (_isDisposed || !_registrations.TryGetValue(serviceType, out var list) || list.Count == 0)
                    return result;
                snapshot = list.ToArray();
            }

            foreach (var reg in snapshot)
            {
                var value = Resolve(reg);
                if (value != null) result.Add(value);
            }
            return result;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private readonly HashSet<object> _disposed = new HashSet<object>(ReferenceComparer.Instance);

        public void Dispose()
        {
            List<object> all;
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                all = _registrations.Values
                    .SelectMany(l => l.Where(r => r.Created))
                    .Select(r => r.Instance)
                    .ToList();
                _registrations.Clear();
            }

            foreach (var obj in ((IEnumerable<object>)all).Reverse())
            {
                if (obj == null) continue;
                bool added;
                lock (_disposed) { added = _disposed.Add(obj); }
                if (!added) continue;
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