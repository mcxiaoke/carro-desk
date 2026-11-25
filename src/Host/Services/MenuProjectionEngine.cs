using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public static class MenuProjectionEngine
    {
        private static readonly ConditionalWeakTable<DependencyObject, Action> DetachActions =
            new ConditionalWeakTable<DependencyObject, Action>();

        public class ProjectionOptions
        {
            public bool EnableHoverBehavior { get; set; }
            public Action<TrayMenuItem, Exception> ErrorHandler { get; set; }
            public Action<TrayMenuItem> AfterClick { get; set; }
            public Action RequestRefresh { get; set; }
            public Action<TrayMenuItem, string> BackgroundPropertyWarn { get; set; }
        }

        public static object CreateVisual(TrayMenuItem node, ProjectionOptions options, Dispatcher dispatcher = null)
        {
            if (node == null) return null;
            if (node.IsSeparator) return new Separator();

            var menuItem = new MenuItem { DataContext = node };

            if (options != null && options.EnableHoverBehavior)
            {
                AttachHoverBehavior(menuItem);
            }

            SetBinding(menuItem, MenuItem.HeaderProperty, "Header", node);
            SetBinding(menuItem, MenuItem.IsCheckedProperty, "IsChecked", node, BindingMode.OneWay);
            SetBinding(menuItem, MenuItem.IsEnabledProperty, "IsEnabled", node, BindingMode.OneWay);
            SetBinding(menuItem, MenuItem.InputGestureTextProperty, "InputGestureText", node, BindingMode.OneWay);
            SetBinding(menuItem, MenuItem.ToolTipProperty, "ToolTip", node, BindingMode.OneWay);
            SetBinding(menuItem, MenuItem.VisibilityProperty, "IsVisible", node, BindingMode.OneWay, new BooleanToVisibilityConverter());

            menuItem.Command = node.Command;
            menuItem.CommandParameter = node.CommandParameter;

            if (node.Command == null)
            {
                menuItem.Click += (s, e) =>
                {
                    if (node.ClickAction != null)
                    {
                        try
                        {
                            node.ClickAction();
                        }
                        catch (Exception ex)
                        {
                            options?.ErrorHandler?.Invoke(node, ex);
                        }
                    }
                    options?.AfterClick?.Invoke(node);
                };
            }

            foreach (var child in node.Children)
            {
                var childVisual = CreateVisual(child, options, dispatcher);
                if (childVisual != null)
                {
                    menuItem.Items.Add(childVisual);
                }
            }

            if (options?.RequestRefresh != null)
            {
                // 这些事件挂在**模块侧**的 node 上，而视觉元素（MenuItem）每次托盘重建都会新建。
                // 用具名处理器而非裸 lambda，并以视觉元素的 Unloaded 作为订阅的生命周期边界：
                // 若模块将来改为复用/缓存节点，订阅就不会随打开次数线性累积（否则会触发
                // N 倍 RequestRefresh 的递归刷新风暴）。
                NotifyCollectionChangedEventHandler onChildrenChanged = (s, e) =>
                {
                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        dispatcher.BeginInvoke(options.RequestRefresh);
                    }
                    else
                    {
                        options.RequestRefresh();
                    }
                };

                PropertyChangedEventHandler onPropertyChanged = (s, pc) =>
                {
                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        options?.BackgroundPropertyWarn?.Invoke(node, pc.PropertyName);
                    }
                };

                node.Children.CollectionChanged += onChildrenChanged;
                node.PropertyChanged += onPropertyChanged;

                int detached = 0;
                Action detach = () =>
                {
                    if (Interlocked.Exchange(ref detached, 1) != 0) return;
                    node.Children.CollectionChanged -= onChildrenChanged;
                    node.PropertyChanged -= onPropertyChanged;
                };
                DetachActions.Remove(menuItem);
                DetachActions.Add(menuItem, detach);
                menuItem.Unloaded += (s, e) => detach();
            }

            return menuItem;
        }

        /// <summary>
        /// 递归解除视觉树的数据绑定与模块事件订阅，即使菜单从未打开过（Unloaded 未触发）也不会泄漏。
        /// </summary>
        public static void DetachVisual(object visual)
        {
            if (visual == null) return;
            if (visual is DependencyObject d)
            {
                if (DetachActions.TryGetValue(d, out var detach))
                {
                    DetachActions.Remove(d);
                    try { detach(); } catch { }
                }
                BindingOperations.ClearAllBindings(d);
            }
            if (visual is ItemsControl itemsControl)
            {
                var children = new List<object>();
                foreach (var child in itemsControl.Items)
                {
                    children.Add(child);
                }
                foreach (var child in children)
                {
                    DetachVisual(child);
                }
                itemsControl.Items.Clear();
            }
        }

        public static void AttachHoverBehavior(MenuItem mi)
        {
            if (mi == null) return;
            mi.MouseEnter += (s, e) =>
            {
                CloseSiblingSubmenus(mi);
                if (mi.HasItems)
                {
                    mi.IsSubmenuOpen = true;
                }
            };
        }

        public static void CloseSiblingSubmenus(MenuItem current)
        {
            if (current == null) return;
            ItemsControl parent = ItemsControl.ItemsControlFromItemContainer(current) ?? current.Parent as ItemsControl;
            if (parent != null)
            {
                foreach (var item in parent.Items)
                {
                    MenuItem sibling = item as MenuItem;
                    if (sibling == null && parent.ItemContainerGenerator != null)
                    {
                        sibling = parent.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
                    }
                    if (sibling != null && sibling != current && sibling.IsSubmenuOpen)
                    {
                        sibling.IsSubmenuOpen = false;
                    }
                }
            }
        }

        public static void SetBinding(FrameworkElement element, DependencyProperty dp, string path,
            object source, BindingMode mode = BindingMode.OneWay, IValueConverter converter = null)
        {
            var binding = new Binding(path)
            {
                Source = source,
                Mode = mode,
                Converter = converter
            };
            element.SetBinding(dp, binding);
        }
    }
}
