using System;
using System.Collections.Concurrent;

namespace CarroDesk.Host.Services
{
    public class ServiceContainer : IServiceProvider
    {
        private readonly ConcurrentDictionary<Type, object> _singletons = new ConcurrentDictionary<Type, object>();
        private readonly ConcurrentDictionary<Type, Func<IServiceProvider, object>> _factories = new ConcurrentDictionary<Type, Func<IServiceProvider, object>>();

        public void AddSingleton(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _singletons[serviceType] = instance;
        }

        public void AddSingleton<TService>(TService instance) where TService : class
        {
            AddSingleton(typeof(TService), instance);
        }

        public void AddSingleton<TService>(Func<IServiceProvider, TService> factory) where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _factories[typeof(TService)] = factory;
        }

        public object GetService(Type serviceType)
        {
            if (serviceType == null) return null;

            if (_singletons.TryGetValue(serviceType, out var instance))
            {
                return instance;
            }

            if (_factories.TryGetValue(serviceType, out var factory))
            {
                var created = factory(this);
                if (created != null)
                {
                    _singletons.TryAdd(serviceType, created);
                    return created;
                }
            }

            return null;
        }

        public T GetService<T>() where T : class
        {
            return GetService(typeof(T)) as T;
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
