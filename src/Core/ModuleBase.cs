using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    /// <summary>
    /// 模块基类：状态机与生命周期的统一实现，兼具快捷键、配置存取与托盘刷新的通用基础设施托管。
    ///
    /// 状态迁移（仅允许以下路径，其余一律忽略）：
    ///   Created --Initialize--> Initialized --Start--> Running --Stop--> Stopped
    ///   任意阶段失败 -> Faulted（终端态：不可再 Start）
    ///
    /// 并发约定：模块的启停可能来自宿主启动流程、托盘操作与配置重载，
    /// 因此状态读写全部加锁，Start/Stop 均做幂等处理；Initialize 重复调用不会
    /// 把已 Running 的模块打回 Initialized。
    /// </summary>
    public abstract class ModuleBase<TConfig> : IModule where TConfig : class, new()
    {
        private readonly object _stateLock = new object();
        private ModuleStatus _status = ModuleStatus.Created;
        private bool _isRunning;
        private bool _isStarting;
        private bool _stopRequested;

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

        /// <summary>
        /// 模块配置实例。默认以空配置保底，防止初始化前或单元测试注入时产生空引用。
        /// </summary>
        public TConfig Config { get; protected set; } = new TConfig();

        public virtual void Initialize(IModuleContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            lock (_stateLock)
            {
                // 幂等：只有 Created 状态才允许初始化。
                if (_status != ModuleStatus.Created) return;

                Context = context;
                _status = ModuleStatus.Initialized;
            }

            try
            {
                var configMgr = Context.GetService<IConfigManager>();
                Config = (configMgr != null ? configMgr.GetModuleConfig<TConfig>(Id) : null) ?? new TConfig();
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
                _stopRequested = false;
            }

            try
            {
                lock (_managedHotkeys) { _managedHotkeys.Clear(); }
                OnStart();
                bool stopRequested;
                lock (_stateLock)
                {
                    stopRequested = _stopRequested;
                    if (!stopRequested)
                    {
                        _isRunning = true;
                        _status = ModuleStatus.Running;
                    }
                }
                if (stopRequested)
                {
                    try { UnregisterAllManagedHotkeys(); } catch { }
                    try { OnStop(); } catch { }
                    Status = ModuleStatus.Stopped;
                    return;
                }
                SyncManagedHotkeys();
            }
            catch (Exception ex)
            {
                // OnStart 可能已经注册热键、启动 Timer 或订阅事件；失败时必须做逆序清理。
                try { UnregisterAllManagedHotkeys(); } catch { }
                try { OnStop(); } catch { }
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
                if (_isStarting)
                {
                    // Start 尚未完成时记录停止请求；Start 尾部会执行完整清理，不能直接返回后放任启动继续。
                    _stopRequested = true;
                    return;
                }
                if (!_isRunning) return;
                // 先置位再回调：OnStop 期间状态即为"已停止"，重复 Stop 不会二次执行清理
                _isRunning = false;
            }

            try
            {
                UnregisterAllManagedHotkeys();
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

        public void MarkFaulted()
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _isStarting = false;
                _status = ModuleStatus.Faulted;
            }
        }

        public virtual void OnConfigReloaded()
        {
            var configMgr = Context?.GetService<IConfigManager>();
            if (configMgr != null)
            {
                Config = configMgr.GetModuleConfig<TConfig>(Id) ?? new TConfig();
            }
            SyncManagedHotkeys();
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

        #region 基础设施通用封装（托管快捷键、配置存取、托盘与通知）

        private sealed class ManagedHotkeyBinding
        {
            public Func<string> HotkeyGetter { get; }
            public Action Action { get; }
            public int CurrentHotkeyId { get; set; }
            public string LastRegisteredKey { get; set; }

            public ManagedHotkeyBinding(Func<string> hotkeyGetter, Action action)
            {
                HotkeyGetter = hotkeyGetter ?? throw new ArgumentNullException(nameof(hotkeyGetter));
                Action = action ?? throw new ArgumentNullException(nameof(action));
            }
        }

        private readonly List<ManagedHotkeyBinding> _managedHotkeys = new List<ManagedHotkeyBinding>();

        /// <summary>
        /// 注册受生命周期托管的快捷键。
        /// 基类将在 OnStart 时自动注册、OnConfigReloaded 时自动同步热键变更、OnStop/Dispose 时自动注销。
        /// </summary>
        protected void RegisterManagedHotkey(Func<string> hotkeyGetter, Action action)
        {
            if (hotkeyGetter == null || action == null) return;
            lock (_managedHotkeys)
            {
                var binding = new ManagedHotkeyBinding(hotkeyGetter, action);
                _managedHotkeys.Add(binding);
                if (IsRunning)
                {
                    ApplyHotkeyBinding(binding);
                }
            }
        }

        private void ApplyHotkeyBinding(ManagedHotkeyBinding binding)
        {
            var hotkeys = Context?.GetService<IHotkeyService>();
            if (hotkeys == null) return;

            string newKey = binding.HotkeyGetter()?.Trim();

            if (binding.CurrentHotkeyId > 0 &&
                string.Equals(binding.LastRegisteredKey, newKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(newKey))
            {
                if (binding.CurrentHotkeyId > 0)
                {
                    try { hotkeys.Unregister(Id, binding.CurrentHotkeyId); } catch { }
                }
                binding.CurrentHotkeyId = 0;
                binding.LastRegisteredKey = null;
                return;
            }

            // 先注册新键，成功后再注销旧键。新键被外部程序占用时保留原工作热键，
            // 避免一次配置重载让模块彻底失去快捷操作。
            int newId = 0;
            try
            {
                newId = hotkeys.Register(Id, newKey, binding.Action, out _);
            }
            catch { }
            if (newId <= 0) return;

            int oldId = binding.CurrentHotkeyId;
            binding.CurrentHotkeyId = newId;
            binding.LastRegisteredKey = newKey;
            if (oldId > 0)
            {
                try { hotkeys.Unregister(Id, oldId); } catch { }
            }
        }

        private void SyncManagedHotkeys()
        {
            lock (_managedHotkeys)
            {
                foreach (var binding in _managedHotkeys)
                {
                    ApplyHotkeyBinding(binding);
                }
            }
        }

        private void UnregisterAllManagedHotkeys()
        {
            lock (_managedHotkeys)
            {
                var hotkeys = Context?.GetService<IHotkeyService>();
                foreach (var binding in _managedHotkeys)
                {
                    if (binding.CurrentHotkeyId > 0 && hotkeys != null)
                    {
                        try
                        {
                            hotkeys.Unregister(Id, binding.CurrentHotkeyId);
                        }
                        catch { }
                        binding.CurrentHotkeyId = 0;
                        binding.LastRegisteredKey = null;
                    }
                }
                try
                {
                    hotkeys?.UnregisterAll(Id);
                }
                catch { }
                _managedHotkeys.Clear();
            }
        }

        /// <summary>持久化当前模块配置（经 IConfigManager）</summary>
        public virtual bool SaveConfig()
        {
            try
            {
                var configMgr = Context?.GetService<IConfigManager>();
                return configMgr != null && configMgr.SaveModuleConfig(Id, Config);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>向宿主请求托盘菜单刷新</summary>
        public void RequestTrayRefresh()
        {
            try
            {
                Context?.RequestTrayRefresh();
            }
            catch { }
        }

        /// <summary>向宿主请求托盘菜单刷新（与历史命名保持兼容）</summary>
        public void RequestRefreshTray() => RequestTrayRefresh();

        /// <summary>派发通知气泡</summary>
        public void ShowNotify(string message, string title = "CarroDesk")
        {
            try
            {
                Context?.ShowNotification(message, title);
            }
            catch { }
        }

        /// <summary>当前模块托盘根项引用（供线程安全 Header/ToolTip 刷新）</summary>
        protected TrayMenuItem TrayRoot { get; set; }

        /// <summary>跨线程安全刷新当前模块托盘根项的 Header 与 ToolTip</summary>
        protected void SetTrayItemSelf(string header, string toolTip = null)
        {
            if (TrayRoot == null) return;
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(() => SetTrayItemSelf(header, toolTip)));
                return;
            }
            TrayRoot.Header = header;
            if (toolTip != null)
            {
                TrayRoot.ToolTip = toolTip;
            }
        }

        #endregion
    }
}
