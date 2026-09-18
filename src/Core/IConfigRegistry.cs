using System;

namespace CarroDesk.Core
{
    /// <summary>
    /// 模块配置注册表，允许模块向宿主提供自定义配置适配器，解耦 Host 对模块具体类型的依赖。
    /// </summary>
    public interface IConfigRegistry
    {
        void Register<TConfig>(string moduleId, Func<TConfig> getter, Action<TConfig> setter) where TConfig : class, new();
        void RegisterDefault<TConfig>(string moduleId, Func<TConfig> defaultFactory) where TConfig : class, new();
    }
}
