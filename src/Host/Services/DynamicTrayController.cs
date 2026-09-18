using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Views;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 托盘动态注入引擎（规范 §4）。
    /// 将各模块 <see cref="IModule.GetTrayMenuItems"/> 的项按序插入 TrayContextMenu 双锚点（SlotAnchorTop/Bottom）之间；
    /// 维护 <see cref="_inserted"/> 登记表精确移除；属性级走绑定，结构级整区间重建；
    /// RequestRefresh 走 Dispatcher + 150ms 防抖，菜单 IsOpen 时延迟到 Closed 后执行。
    /// 本类属 Host 层，禁止出现业务模块类名与业务概念词。
    /// </summary>
    public class DynamicTrayController
    {
        private const int DebounceMs = 150;

        private readonly ModuleManager _modules;
        private readonly Dispatcher _dispatcher;
        private readonly ILoggerService _logger;
        private readonly Action<string, string> _notify;

        /// <summary>登记每个模块插入的动态元素，卸载/重建按登记表精确移除（禁止 IndexOf(anchor)+1 脆弱定位）。</summary>
        private readonly Dictionary<IModule, List<object>> _inserted = new Dictionary<IModule, List<object>>();
        private readonly Dictionary<TrayMenuItem, IModule> _nodeOwner = new Dictionary<TrayMenuItem, IModule>();
        private readonly List<object> _slotItems = new List<object>();

        private TrayContextMenu _menu;
        private DispatcherTimer _debounceTimer;
        private bool _refreshScheduled;
        private bool _pendingAfterClose;

        public DynamicTrayController(ModuleManager modules, Dispatcher dispatcher, ILoggerService logger,
            Action<string, string> notify)
        {
            _modules = modules ?? throw new ArgumentNullException(nameof(modules));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;
            _notify = notify;
        }

        /// <summary>绑定到托盘 ContextMenu（事件接线由菜单侧统一负责）。</summary>
        public void Attach(TrayContextMenu menu)
        {
            _menu = menu ?? throw new ArgumentNullException(nameof(menu));
            _menu.DynamicController = this;
        }

        /// <summary>任意线程可调；150ms 防抖；菜单 IsOpen 时延迟到 Closed 后执行。</summary>
        public void RequestRefresh()
        {
            if (_dispatcher.CheckAccess())
            {
                ScheduleAfterDebounce();
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(ScheduleAfterDebounce));
            }
        }

        /// <summary>UI 线程立即重建（菜单每次打开时调用，确保托盘菜单为最新状态）。</summary>
        public void RefreshNow()
        {
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(RefreshNow));
                return;
            }
            RebuildSlot();
        }

        /// <summary>菜单关闭后由菜单侧调用，执行被延迟的重建。</summary>
        public void NotifyClosed()
        {
            if (_pendingAfterClose)
            {
                _pendingAfterClose = false;
                ScheduleAfterDebounce();
            }
        }

        private void ScheduleAfterDebounce()
        {
            if (_menu != null && _menu.IsOpen)
            {
                _pendingAfterClose = true;
                return;
            }
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
                _debounceTimer.Tick += (s, e) =>
                {
                    _debounceTimer.Stop();
                    _refreshScheduled = false;
                    RebuildSlot();
                };
            }
            if (!_refreshScheduled)
            {
                _refreshScheduled = true;
                _debounceTimer.Start();
            }
        }

        /// <summary>立即整区间重建（UI 线程）。</summary>
        public void RebuildSlot()
        {
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(RebuildSlot));
                return;
            }
            if (_menu == null)
            {
                return;
            }

            var anchorTop = _menu.SlotAnchorTop;
            var anchorBottom = _menu.SlotAnchorBottom;
            if (anchorTop == null || anchorBottom == null)
            {
                return;
            }

            RemoveAll();

            var visuals = new List<object>();
            foreach (var module in _modules.Modules.OrderBy(m => m.Order))
            {
                var nodes = GetItemsGuarded(module);
                var moduleVisuals = new List<object>();
                foreach (var node in nodes)
                {
                    var visual = CreateVisual(node, module);
                    if (visual != null)
                    {
                        moduleVisuals.Add(visual);
                        visuals.Add(visual);
                    }
                }
                if (moduleVisuals.Count > 0)
                {
                    _inserted[module] = moduleVisuals;
                }
            }

            // 空态折叠：两端区间为空时折叠相邻分隔线，禁止双分隔线空隙
            if (visuals.Count == 0)
            {
                SetSeparatorVisible(anchorTop, true);
                SetSeparatorVisible(anchorBottom, false);
                return;
            }

            TrimEdgeSeparators(visuals);
            CollapseConsecutiveSeparators(visuals);

            SetSeparatorVisible(anchorTop, true);
            SetSeparatorVisible(anchorBottom, true);

            int index = _menu.Items.IndexOf(anchorTop) + 1;
            for (int i = 0; i < visuals.Count; i++)
            {
                var visual = visuals[i];
                _slotItems.Add(visual);
                _menu.Items.Insert(index + i, visual);
            }
        }

        private IEnumerable<TrayMenuItem> GetItemsGuarded(IModule module)
        {
            try
            {
                var items = module.GetTrayMenuItems();
                return items ?? Enumerable.Empty<TrayMenuItem>();
            }
            catch (Exception ex)
            {
                TryLog(module, "获取托盘菜单项失败", ex);
                return Enumerable.Empty<TrayMenuItem>();
            }
        }

        // ---- 移除 ----
        private void RemoveAll()
        {
            foreach (var pair in _inserted)
            {
                var module = pair.Key;
                foreach (var visual in pair.Value)
                {
                    DetachVisual(module, visual);
                    _menu.Items.Remove(visual);
                }
            }
            _inserted.Clear();

            foreach (var visual in _slotItems)
            {
                _menu.Items.Remove(visual);
            }
            _slotItems.Clear();
            _nodeOwner.Clear();
        }

        private void DetachVisual(IModule module, object visual)
        {
            if (visual is MenuItem menuItem)
            {
                menuItem.Click -= OnToggleClick;
            }
            if (visual is ItemsControl itemsControl)
            {
                BindingOperations.ClearAllBindings(itemsControl);
                var children = new List<object>();
                foreach (var child in itemsControl.Items)
                {
                    children.Add(child);
                }
                foreach (var child in children)
                {
                    DetachVisual(module, child);
                }
                itemsControl.Items.Clear();
            }
            else if (visual is FrameworkElement fe)
            {
                BindingOperations.ClearAllBindings(fe);
            }
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var node = menuItem != null ? menuItem.DataContext as TrayMenuItem : null;
            if (node == null || node.Command != null || node.ClickAction == null)
            {
                return;
            }
            try
            {
                node.ClickAction();
            }
            catch (Exception ex)
            {
                // ClickAction 异常由 Host 记日志并气泡一次，禁止静默吞
                IModule module = null;
                if (!_nodeOwner.TryGetValue(node, out module)) module = null;
                TryLog(module, "托盘菜单点击执行异常（ClickAction）", ex);
                try { _notify?.Invoke((node.Header ?? "托盘操作") + " 执行失败", "CarroDesk"); }
                catch { }
            }
        }

        // ---- 构建 ----
        private object CreateVisual(TrayMenuItem node, IModule module)
        {
            if (node == null) return null;
            _nodeOwner[node] = module;

            var options = new MenuProjectionEngine.ProjectionOptions
            {
                EnableHoverBehavior = false,
                RequestRefresh = RequestRefresh,
                ErrorHandler = (n, ex) =>
                {
                    IModule mod = null;
                    if (!_nodeOwner.TryGetValue(n, out mod)) mod = module;
                    TryLog(mod, "托盘菜单点击执行异常（ClickAction）", ex);
                    try { _notify?.Invoke((n.Header ?? "托盘操作") + " 执行失败", "CarroDesk"); }
                    catch { }
                },
                BackgroundPropertyWarn = (n, propName) =>
                {
                    TryLog(module, $"托盘节点属性在后台线程更新，已忽略（请模块自行 Dispatcher 封送），属性={propName}", null);
                }
            };

            return MenuProjectionEngine.CreateVisual(node, options, _dispatcher);
        }

        private static void TrimEdgeSeparators(List<object> visuals)
        {
            while (visuals.Count > 0 && visuals[0] is Separator)
            {
                visuals.RemoveAt(0);
            }
            while (visuals.Count > 0 && visuals[visuals.Count - 1] is Separator)
            {
                visuals.RemoveAt(visuals.Count - 1);
            }
        }

        private static void CollapseConsecutiveSeparators(List<object> visuals)
        {
            var result = new List<object>(visuals.Count);
            bool prevWasSep = true; // 列表首为功能项，前驱按分隔处理以折叠多个前导分隔
            foreach (var visual in visuals)
            {
                if (visual is Separator)
                {
                    if (prevWasSep) continue;
                    prevWasSep = true;
                }
                else
                {
                    prevWasSep = false;
                }
                result.Add(visual);
            }
            visuals.Clear();
            visuals.AddRange(result);
        }

        private static void SetSeparatorVisible(Separator separator, bool visible)
        {
            if (separator == null) return;
            separator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TryLog(IModule module, string message, Exception ex)
        {
            try
            {
                if (_logger != null)
                {
                    var id = module != null ? module.Id : "Host";
                    if (ex != null) _logger.LogError(id, message, ex);
                    else _logger.LogWarning(id, message);
                }
            }
            catch { }
        }
    }
}