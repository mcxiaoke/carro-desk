using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class FloatingPanelWindow : Window
    {
        private static FloatingPanelWindow _instance;
        public static FloatingPanelWindow Instance => _instance;

        private bool _isPinned;
        private bool _isLocked;

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                bool previous = _isPinned;
                _isPinned = value;
                UpdatePinVisual();
                if (!SaveSettings())
                {
                    _isPinned = previous;
                    if (CurrentSettings != null) CurrentSettings.FloatingPanelPinned = previous;
                    UpdatePinVisual();
                }
            }
        }

        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                bool previous = _isLocked;
                _isLocked = value;
                UpdateLockVisual();
                if (!SaveSettings())
                {
                    _isLocked = previous;
                    if (CurrentSettings != null) CurrentSettings.FloatingPanelLocked = previous;
                    UpdateLockVisual();
                }
            }
        }

        private readonly ConfigManager _configManager;
        private readonly ModuleManager _moduleManager;
        private readonly ServiceContainer _services;
        private readonly Action _reloadConfig;
        private readonly Action _promptExit;
        private readonly Action _updateTrayText;

        private AppSettings CurrentSettings => _configManager?.Current;

        private bool PersistSettings()
        {
            try
            {
                if (_configManager == null) return false;
                _configManager.Save();
                return true;
            }
            catch (Exception ex)
            {
                _services?.GetService<ILoggerService>()?.LogError("FloatingPanel", "保存悬浮面板配置失败", ex);
                _services?.GetService<INotificationService>()?.Show(Loc.T("Config.SaveFailed"), "CarroDesk");
                return false;
            }
        }

        public FloatingPanelWindow(
            ConfigManager configManager = null,
            ModuleManager moduleManager = null,
            ServiceContainer services = null,
            Action reloadConfig = null,
            Action promptExit = null,
            Action updateTrayText = null)
        {
            _instance = this;
            _configManager = configManager;
            _moduleManager = moduleManager;
            _services = services;
            _reloadConfig = reloadConfig;
            _promptExit = promptExit;
            _updateTrayText = updateTrayText;

            InitializeComponent();

            HeaderBar.MouseLeftButtonDown += OnHeaderMouseLeftButtonDown;
            HeaderBar.MouseEnter += (s, e) => CloseAllTopLevelSubmenus();

            Deactivated += OnWindowDeactivated;
            KeyDown += OnWindowKeyDown;

            // 监听语言切换
            I18nService.Instance.LanguageChanged += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RebuildMenu();
                    UpdatePinVisual();
                    UpdateLockVisual();
                }));
            };

            // 初始读取 Pin 与 Lock 状态
            var cfg = CurrentSettings;
            if (cfg != null)
            {
                _isPinned = cfg.FloatingPanelPinned;
                _isLocked = cfg.FloatingPanelLocked;
            }
            UpdatePinVisual();
            UpdateLockVisual();
        }

        public static void Toggle(
            ConfigManager configManager = null,
            ModuleManager moduleManager = null,
            ServiceContainer services = null,
            Action reloadConfig = null,
            Action promptExit = null,
            Action updateTrayText = null)
        {
            if (_instance == null)
            {
                _instance = new FloatingPanelWindow(configManager, moduleManager, services, reloadConfig, promptExit, updateTrayText);
            }

            if (_instance.IsVisible)
            {
                _instance.HidePanel();
            }
            else
            {
                _instance.ShowPanel();
            }
        }

        public void RefreshSettings()
        {
            var cfg = CurrentSettings;
            if (cfg == null) return;
            _isPinned = cfg.FloatingPanelPinned;
            _isLocked = cfg.FloatingPanelLocked;
            UpdatePinVisual();
            UpdateLockVisual();
            if (IsVisible) ApplyPosition();
        }

        public void ShowPanel()
        {
            RebuildMenu();
            ApplyPosition();
            Show();
            Activate();
            Focus();
        }

        public void HidePanel()
        {
            Hide();
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            // 如果已钉住，失焦不关闭；未钉住则自动隐藏
            if (!_isPinned)
            {
                HidePanel();
            }
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HidePanel();
                e.Handled = true;
            }
        }

        #region 拖拽与位置管理

        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 位置已锁定时禁止拖拽
            if (_isLocked) return;

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                // 若点击目标为右侧按钮或其子元素，则不启动拖拽
                DependencyObject source = e.OriginalSource as DependencyObject;
                while (source != null && source != HeaderBar)
                {
                    if (source is ButtonBase)
                    {
                        return;
                    }
                    source = VisualTreeHelper.GetParent(source);
                }

                try
                {
                    DragMove();
                    SaveCurrentPosition();
                }
                catch { }
            }
        }

        private void SaveCurrentPosition()
        {
            var cfg = CurrentSettings;
            if (cfg != null)
            {
                string oldMode = cfg.FloatingPanelPosition;
                double oldX = cfg.FloatingPanelX;
                double oldY = cfg.FloatingPanelY;
                cfg.FloatingPanelPosition = "Custom";
                cfg.FloatingPanelX = Left;
                cfg.FloatingPanelY = Top;
                if (!SaveSettings())
                {
                    cfg.FloatingPanelPosition = oldMode;
                    cfg.FloatingPanelX = oldX;
                    cfg.FloatingPanelY = oldY;
                }
            }
        }

        public void Reposition(string mode)
        {
            var cfg = CurrentSettings;
            if (cfg != null)
            {
                string oldMode = cfg.FloatingPanelPosition;
                cfg.FloatingPanelPosition = mode;
                if (!SaveSettings()) cfg.FloatingPanelPosition = oldMode;
            }
            ApplyPosition();
        }

        private void ApplyPosition()
        {
            UpdateLayout();
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double panelWidth = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 230);
            double panelHeight = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
            if (panelHeight < 100) panelHeight = 350; // 安全估算高度

            var workArea = SystemParameters.WorkArea;
            var mode = CurrentSettings?.FloatingPanelPosition ?? "Tray";

            double targetLeft;
            double targetTop;

            switch (mode)
            {
                case "Center":
                    targetLeft = workArea.Left + (workArea.Width - panelWidth) / 2;
                    targetTop = workArea.Top + (workArea.Height - panelHeight) / 2;
                    break;

                case "TopRight":
                    targetLeft = workArea.Right - panelWidth - 14;
                    targetTop = workArea.Top + 14;
                    break;

                case "Custom":
                    var cfg = CurrentSettings;
                    if (cfg != null && cfg.FloatingPanelX != -1 && cfg.FloatingPanelY != -1)
                    {
                        var monitors = DisplayMonitorHelper.GetAllMonitors();
                        var targetMonitor = monitors.FirstOrDefault(m =>
                            cfg.FloatingPanelX >= m.Left &&
                            cfg.FloatingPanelX < m.Left + m.Width &&
                            cfg.FloatingPanelY >= m.Top &&
                            cfg.FloatingPanelY < m.Top + m.Height);
                        if (targetMonitor == null) targetMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                        double customLeft = targetMonitor?.WorkLeft ?? workArea.Left;
                        double customTop = targetMonitor?.WorkTop ?? workArea.Top;
                        double customRight = customLeft + (targetMonitor?.WorkWidth ?? (int)workArea.Width);
                        double customBottom = customTop + (targetMonitor?.WorkHeight ?? (int)workArea.Height);
                        targetLeft = Math.Max(customLeft, Math.Min(customRight - panelWidth, cfg.FloatingPanelX));
                        targetTop = Math.Max(customTop, Math.Min(customBottom - panelHeight, cfg.FloatingPanelY));
                    }
                    else
                    {
                        // 回退到 Tray
                        targetLeft = workArea.Right - panelWidth - 14;
                        targetTop = workArea.Bottom - panelHeight - 14;
                    }
                    break;

                case "Tray":
                default:
                    targetLeft = workArea.Right - panelWidth - 14;
                    targetTop = workArea.Bottom - panelHeight - 14;
                    break;
            }

            Left = Math.Round(targetLeft);
            Top = Math.Round(targetTop);
        }

        #endregion

        #region 工具栏交互

        private void UpdatePinVisual()
        {
            if (TxtPin != null)
            {
                TxtPin.Foreground = _isPinned ? new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)) : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            }
            if (BtnPin != null)
            {
                BtnPin.ToolTip = _isPinned
                    ? Loc.T("Tray.FloatingPanelPinnedTooltip", "常驻桌面（失焦不收起，点击切换）")
                    : Loc.T("Tray.FloatingPanelUnpinnedTooltip", "自动隐藏（失焦自动收起，点击常驻）");
                BtnPin.Background = _isPinned ? new SolidColorBrush(Color.FromArgb(0x28, 0x1A, 0x73, 0xE8)) : Brushes.Transparent;
            }
        }

        private void OnPinClick(object sender, RoutedEventArgs e)
        {
            IsPinned = !IsPinned;
        }

        private void UpdateLockVisual()
        {
            if (TxtLock != null)
            {
                TxtLock.Text = _isLocked ? "🔒" : "🔓";
                TxtLock.Foreground = _isLocked ? new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)) : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            }
            if (BtnLock != null)
            {
                BtnLock.ToolTip = _isLocked
                    ? Loc.T("Tray.FloatingPanelLockedTooltip", "位置已锁定（禁止拖拽，点击解锁）")
                    : Loc.T("Tray.FloatingPanelUnlockedTooltip", "位置未锁定（可自由拖拽，点击锁定）");
                BtnLock.Background = _isLocked ? new SolidColorBrush(Color.FromArgb(0x28, 0x1A, 0x73, 0xE8)) : Brushes.Transparent;
            }
            if (HeaderBar != null)
            {
                HeaderBar.Cursor = _isLocked ? Cursors.Arrow : Cursors.SizeAll;
            }
        }

        private void OnLockClick(object sender, RoutedEventArgs e)
        {
            IsLocked = !IsLocked;
        }

        private void OnPositionClick(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            var currentMode = CurrentSettings?.FloatingPanelPosition ?? "Tray";

            var itemTray = new MenuItem
            {
                Header = Loc.T("Tray.PositionTray", "靠任务栏托盘"),
                IsChecked = currentMode == "Tray"
            };
            itemTray.Click += (s, ev) => Reposition("Tray");
            cm.Items.Add(itemTray);

            var itemCenter = new MenuItem
            {
                Header = Loc.T("Tray.PositionCenter", "屏幕中央"),
                IsChecked = currentMode == "Center"
            };
            itemCenter.Click += (s, ev) => Reposition("Center");
            cm.Items.Add(itemCenter);

            var itemTopRight = new MenuItem
            {
                Header = Loc.T("Tray.PositionTopRight", "屏幕右上角"),
                IsChecked = currentMode == "TopRight"
            };
            itemTopRight.Click += (s, ev) => Reposition("TopRight");
            cm.Items.Add(itemTopRight);

            cm.Items.Add(new Separator());

            var itemCustom = new MenuItem
            {
                Header = Loc.T("Tray.PositionCustom", "记忆当前位置"),
                IsChecked = currentMode == "Custom"
            };
            itemCustom.Click += (s, ev) =>
            {
                var cfg = CurrentSettings;
                if (cfg != null)
                {
                    string oldMode = cfg.FloatingPanelPosition;
                    double oldX = cfg.FloatingPanelX;
                    double oldY = cfg.FloatingPanelY;
                    cfg.FloatingPanelPosition = "Custom";
                    cfg.FloatingPanelX = Left;
                    cfg.FloatingPanelY = Top;
                    if (!SaveSettings())
                    {
                        cfg.FloatingPanelPosition = oldMode;
                        cfg.FloatingPanelX = oldX;
                        cfg.FloatingPanelY = oldY;
                    }
                }
            };
            cm.Items.Add(itemCustom);

            cm.PlacementTarget = BtnPosition;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            HidePanel();
        }

        private bool SaveSettings()
        {
            var cfg = CurrentSettings;
            if (cfg != null)
            {
                cfg.FloatingPanelPinned = _isPinned;
                cfg.FloatingPanelLocked = _isLocked;
                return PersistSettings();
            }
            return false;
        }

        #endregion

        #region 菜单内容构建（完全复用 TrayMenuItem 结构与动作）

        public void RebuildMenu()
        {
            ItemsHostMenu.Items.Clear();

            // ① 挂载各业务模块导出的标准二级根项
            var modules = _moduleManager != null
                ? _moduleManager.Modules
                    .Where(m => m.Status == ModuleStatus.Initialized || m.Status == ModuleStatus.Running)
                    .OrderBy(m => m.Order)
                : Enumerable.Empty<IModule>();
            bool hasModule = false;
            foreach (var module in modules)
            {
                try
                {
                    var items = module.GetTrayMenuItems();
                    if (items == null) continue;
                    foreach (var node in items.ToList())
                    {
                        var visual = CreateVisual(node);
                        if (visual != null)
                        {
                            ItemsHostMenu.Items.Add(visual);
                            hasModule = true;
                        }
                    }
                }
                catch { }
            }

            if (hasModule)
            {
                ItemsHostMenu.Items.Add(new Separator());
            }

            // ② 挂载宿主通用管理项（开机自启、配置、语言、退出）
            BuildHostItems();
        }

        private void BuildHostItems()
        {
            var config = CurrentSettings;

            // 开机自启
            var autoStartItem = new MenuItem
            {
                Header = Loc.T("Tray.AutoStart", "开机自启"),
                IsChecked = config != null && config.AutoStart
            };
            MenuProjectionEngine.AttachHoverBehavior(autoStartItem);
            autoStartItem.Click += (s, e) =>
            {
                HostMenuActions.ToggleAutoStart(_configManager, _services, () =>
                {
                    autoStartItem.IsChecked = CurrentSettings?.AutoStart ?? false;
                });
                CloseAllTopLevelSubmenus();
                DismissIfNotPinned();
            };
            ItemsHostMenu.Items.Add(autoStartItem);

            // 配置编辑器
            var configEditorItem = new MenuItem
            {
                Header = Loc.T("Tray.ConfigEditor", "配置编辑器...")
            };
            MenuProjectionEngine.AttachHoverBehavior(configEditorItem);
            configEditorItem.Click += (s, e) =>
            {
                CloseAllTopLevelSubmenus();
                DismissIfNotPinned();
                HostMenuActions.OpenConfigEditor(_services, _configManager, _reloadConfig, RebuildMenu);
            };
            ItemsHostMenu.Items.Add(configEditorItem);

            // 打开配置目录
            var openConfigDirItem = new MenuItem
            {
                Header = Loc.T("Tray.OpenConfigDir", "打开配置目录")
            };
            MenuProjectionEngine.AttachHoverBehavior(openConfigDirItem);
            openConfigDirItem.Click += (s, e) =>
            {
                CloseAllTopLevelSubmenus();
                DismissIfNotPinned();
                HostMenuActions.OpenConfigDirectory();
            };
            ItemsHostMenu.Items.Add(openConfigDirItem);

            // 重载配置
            var reloadConfigItem = new MenuItem
            {
                Header = Loc.T("Tray.ReloadConfig", "重载配置")
            };
            MenuProjectionEngine.AttachHoverBehavior(reloadConfigItem);
            reloadConfigItem.Click += (s, e) =>
            {
                _reloadConfig?.Invoke();
                RebuildMenu();
                CloseAllTopLevelSubmenus();
                DismissIfNotPinned();
            };
            ItemsHostMenu.Items.Add(reloadConfigItem);

            // 语言切换
            var langRoot = new MenuItem
            {
                Header = Loc.T("Tray.Language", "语言 / Language")
            };
            MenuProjectionEngine.AttachHoverBehavior(langRoot);
            string currentLang = config?.Language ?? "auto";

            void AddLangItem(string langKey, string header)
            {
                var item = new MenuItem
                {
                    Header = header,
                    IsChecked = string.Equals(currentLang, langKey, StringComparison.OrdinalIgnoreCase)
                };
                MenuProjectionEngine.AttachHoverBehavior(item);
                item.Click += (s, e) =>
                {
                    HostMenuActions.SetLanguage(_configManager, _services, langKey, () =>
                    {
                        RebuildMenu();
                        _updateTrayText?.Invoke();
                    });
                    DismissIfNotPinned();
                };
                langRoot.Items.Add(item);
            }

            AddLangItem("auto", Loc.T("Tray.LangAuto", "自动跟随系统 (Auto)"));
            langRoot.Items.Add(new Separator());
            AddLangItem("zh-CN", Loc.T("Tray.LangZh", "简体中文 (Chinese)"));
            AddLangItem("en-US", Loc.T("Tray.LangEn", "English"));

            ItemsHostMenu.Items.Add(langRoot);
            ItemsHostMenu.Items.Add(new Separator());

            // 退出
            var exitItem = new MenuItem
            {
                Header = Loc.T("Tray.Exit", "退出...")
            };
            MenuProjectionEngine.AttachHoverBehavior(exitItem);
            exitItem.Click += (s, e) =>
            {
                HidePanel();
                _promptExit?.Invoke();
            };
            ItemsHostMenu.Items.Add(exitItem);
        }

        private object CreateVisual(TrayMenuItem node)
        {
            var options = new MenuProjectionEngine.ProjectionOptions
            {
                EnableHoverBehavior = true,
                AfterClick = (n) =>
                {
                    CloseAllTopLevelSubmenus();
                    DismissIfNotPinned();
                },
                RequestRefresh = () =>
                {
                    Dispatcher.BeginInvoke(new Action(RebuildMenu));
                },
                ErrorHandler = (menuNode, ex) =>
                {
                    _services?.GetService<ILoggerService>()?.LogError("FloatingPanel", "菜单操作执行失败", ex);
                    _services?.GetService<INotificationService>()?.Show(
                        (menuNode?.Header ?? Loc.T("Msg.TrayAction", "托盘操作")) + " " + Loc.T("Msg.TrayActionFailed", "执行失败"),
                        "CarroDesk");
                }
            };
            return MenuProjectionEngine.CreateVisual(node, options, Dispatcher);
        }

        private void CloseAllTopLevelSubmenus()
        {
            foreach (var item in ItemsHostMenu.Items)
            {
                if (item is MenuItem mi && mi.IsSubmenuOpen)
                {
                    mi.IsSubmenuOpen = false;
                }
            }
        }

        private void DismissIfNotPinned()
        {
            if (!_isPinned)
            {
                HidePanel();
            }
        }

        #endregion
    }
}
