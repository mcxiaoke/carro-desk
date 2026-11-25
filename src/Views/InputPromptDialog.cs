using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    /// <summary>
    /// 通用单行文本/数值输入弹窗（供托盘自定义分钟数与设置页重命名等复用）。
    /// </summary>
    public static class InputPromptDialog
    {
        public static bool ShowText(Window owner, string title, string prompt, string defaultText, out string result)
        {
            result = defaultText;
            var dlg = new Window
            {
                Title = title,
                Width = 360,
                Height = 160,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.White,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI")
            };
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#FF374151")
            };
            var txt = new TextBox
            {
                Text = defaultText ?? string.Empty,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 13
            };
            txt.SelectAll();

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var btnOk = new Button { Content = Loc.T("Common.Ok", "确定"), Width = 70, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = Loc.T("Common.Cancel", "取消"), Width = 70, Height = 28, IsCancel = true };

            btnOk.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            btnCancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);

            Grid.SetRow(lbl, 0);
            Grid.SetRow(txt, 1);
            Grid.SetRow(btnPanel, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(txt);
            grid.Children.Add(btnPanel);

            dlg.Content = grid;
            dlg.Loaded += (s, e) => txt.Focus();

            if (dlg.ShowDialog() == true)
            {
                result = txt.Text.Trim();
                return true;
            }
            return false;
        }

        public static bool ShowIntRange(string title, string prompt, string defaultText, int min, int max, string invalidMessage, out int value)
        {
            value = 0;
            if (ShowText(null, title, prompt, defaultText, out string input))
            {
                if (int.TryParse(input, out int parsed) && parsed >= min && parsed <= max)
                {
                    value = parsed;
                    return true;
                }
                MessageBox.Show(invalidMessage, Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
    }
}
