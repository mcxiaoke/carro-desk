using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public static class MenuProjectionEngine
    {
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
                node.Children.CollectionChanged += (s, e) =>
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
            }

            if (options?.BackgroundPropertyWarn != null && dispatcher != null)
            {
                node.PropertyChanged += (s, pc) =>
                {
                    if (!dispatcher.CheckAccess())
                    {
                        options.BackgroundPropertyWarn(node, pc.PropertyName);
                    }
                };
            }

            return menuItem;
        }

        private static void AttachHoverBehavior(MenuItem mi)
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
