using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    /// <summary>
    /// 模块基类：状态机与生命周期的统一实现。
    ///
    /// 状态迁移（仅允许以下路径，其余一律忽略）：
    ///   Created --Initialize--> Initialized --Start--> Running --Stop--> Stopped
    ///   任意阶段失败 -> Faulted（终端态：不可再 Start）
    ///
    /// 并发约定：模块的启停可能来自宿主启动流程、托盘操作与配置重载，
    /// 因此状态读写全部加锁，Start/Stop 均做幂等处理；Initialize 重复调用不会
    /// 把已 Running 的模块打回 Initialized（此前会，属状态机漏洞）。
    /// </summary>
    public abstract class ModuleBase<TConfig> : IModule where TConfig : class, new()
    {
        private readonly object _stateLock = new object();
        private ModuleStatus _status = ModuleStatus.Created;
        private bool _isRunning;
        private bool _isStarting;

        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual string Version => "1.0.0";
        public virtual int Order => 0;
        public virtual bool DefaultEnabled => true;

        public bool IsRunning
        {
            get { lock (_stateLock) { return _isRunning; } }
        }

        public ModuleStatus Status
        {
            get { lock (_stateLock) { return _status; } }
            protected set { lock (_stateLock) { _status = value; } }
        }

        protected IModuleContext Context { get; private set; }
        public TConfig Config { get; private set; }

        public virtual void Initialize(IModuleContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            lock (_stateLock)
            {
                // 幂等：只有 Created 状态才允许初始化。
                // 此前重复调用会把 Running 打回 Initialized，并让后续 Start 前的
                // 状态判断全部失真。
                if (_status != ModuleStatus.Created) return;

                Context = context;
                _status = ModuleStatus.Initialized;
            }

            try
            {
                var configMgr = Context.GetService<IConfigManager>();
                Config = configMgr != null ? configMgr.GetModuleConfig<TConfig>(Id) : new TConfig();
            }
            catch
            {
                Status = ModuleStatus.Faulted;
                throw;
            }
        }

        public void Start()
        {
            lock (_stateLock)
            {
                // 初始化失败的模块禁止启动
                if (_status == ModuleStatus.Faulted) return;
                // 状态机校验：只有已完成初始化的模块才能启动
                if (_status != ModuleStatus.Initialized) return;
                if (_isRunning || _isStarting) return;
                _isStarting = true;
            }

            try
            {
                OnStart();
                lock (_stateLock)
                {
                    _isRunning = true;
                    _status = ModuleStatus.Running;
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _isRunning = false;
                    _status = ModuleStatus.Faulted;
                }
                Context?.GetService<ILoggerService>()?.LogError(Id, "启动模块时发生异常", ex);
            }
            finally
            {
                lock (_stateLock) { _isStarting = false; }
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isRunning) return;
                // 先置位再回调：OnStop 期间状态即为"已停止"，重复 Stop 不会二次执行清理
                _isRunning = false;
            }

            try
            {
                OnStop();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "停止模块时发生异常", ex);
            }
            finally
            {
                Status = ModuleStatus.Stopped;
            }
        }

        public virtual void OnConfigReloaded()
        {
            var configMgr = Context?.GetService<IConfigManager>();
            if (configMgr != null)
            {
                Config = configMgr.GetModuleConfig<TConfig>(Id);
            }
        }

        public virtual void OnLanguageChanged()
        {
        }

        public virtual IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            return System.Linq.Enumerable.Empty<TrayMenuItem>();
        }

        protected abstract void OnStart();
        protected abstract void OnStop();

        public virtual void Dispose()
        {
            Stop();
        }
    }
}
