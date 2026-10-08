using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class AboutWindow : Window
    {
        private readonly ModuleManager _modules;
        private DispatcherTimer _toastTimer;

        public AboutWindow(ModuleManager modules)
        {
            _modules = modules;
            InitializeComponent();
            LoadAppInfo();
        }

        private void LoadAppInfo()
        {
            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/Icon.ico");
                Icon = BitmapFrame.Create(iconUri);
                AppLogoImage.Source = BitmapFrame.Create(iconUri);
            }
            catch { }

            VersionBadgeText.Text = AppInfo.DisplayVersion;
            CommitText.Text = AppInfo.GitCommit;
            BuildTimeText.Text = AppInfo.BuildTime;

            RuntimeText.Text = $"{AppInfo.DotNetRuntime} ({AppInfo.ProcessArchitecture})";
            ModeText.Text = AppInfo.IsPortable
                ? Loc.T("About.ModePortable", "便携模式")
                : Loc.T("About.ModeRoaming", "漫游模式");
            DataPathText.Text = AppInfo.DataPath;

            LoadModulesList();
        }

        private void LoadModulesList()
        {
            var rawList = _modules?.Modules ?? Array.Empty<IModule>();
            var viewModels = new List<ModuleItemViewModel>();
            int runningCount = 0;

            foreach (var m in rawList.OrderBy(m => m.Order))
            {
                if (m.IsRunning) runningCount++;

                Brush statusBrush;
                string statusText;

                switch (m.Status)
                {
                    case ModuleStatus.Running:
                        statusBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // #22C55E
                        statusText = Loc.T("About.StatusRunning", "运行中");
                        break;
                    case ModuleStatus.Faulted:
                        statusBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // #EF4444
                        statusText = Loc.T("About.StatusFaulted", "异常");
                        break;
                    case ModuleStatus.Disabled:
                        statusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // #F59E0B
                        statusText = Loc.T("About.StatusDisabled", "已禁用");
                        break;
                    case ModuleStatus.Stopped:
                        statusBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // #94A3B8
                        statusText = Loc.T("About.StatusStopped", "已停止");
                        break;
                    default:
                        statusBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // #3B82F6
                        statusText = Loc.T("About.StatusInitialized", "就绪");
                        break;
                }
                statusBrush.Freeze();

                viewModels.Add(new ModuleItemViewModel
                {
                    Id = m.Id,
                    Name = m.Name,
                    Version = m.Version,
                    Description = m.Description,
                    Status = m.Status,
                    StatusText = statusText,
                    StatusBrush = statusBrush
                });
            }

            ModulesItemsControl.ItemsSource = viewModels;
            ModulesCountText.Text = string.Format(
                Loc.T("About.ModulesCount", "共 {0} 个模块，{1} 个运行中"),
                rawList.Count,
                runningCount);
        }

        private void OnCopyInfoClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = AppInfo.GetDiagnosticReport(_modules?.Modules);
                Clipboard.SetDataObject(text, true);

                CopiedToastText.Visibility = Visibility.Visible;
                _toastTimer?.Stop();
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _toastTimer.Tick += (s, args) =>
                {
                    _toastTimer.Stop();
                    CopiedToastText.Visibility = Visibility.Collapsed;
                };
                _toastTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制到剪贴板失败: " + ex.Message, "CarroDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }

    public class ModuleItemViewModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public ModuleStatus Status { get; set; }
        public string StatusText { get; set; }
        public Brush StatusBrush { get; set; }
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    }
}
