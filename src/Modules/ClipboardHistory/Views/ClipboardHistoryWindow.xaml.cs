using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CarroDesk.Modules.ClipboardHistory.Views
{
    public partial class ClipboardHistoryWindow : Window
    {
        private readonly IClipboardHistoryService _service;
        private List<ClipboardItem> _allItems = new List<ClipboardItem>();

        public ClipboardHistoryWindow(IClipboardHistoryService service)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));

            _service.HistoryChanged += OnServiceHistoryChanged;

            Deactivated += OnWindowDeactivated;
            KeyDown += ClipboardHistoryWindow_KeyDown;
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            // 治本方案：若当前窗口内部仍具有键盘焦点（如正在使用输入法 IME 组合、选中文本），绝不误隐
            if (IsKeyboardFocusWithin)
            {
                return;
            }

            if (IsVisible)
            {
                Hide();
            }
        }

        public void ShowAndActivate()
        {
            RefreshList();
            ApplyPosition();

            if (!IsVisible)
            {
                Show();
            }

            WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            Focus();

            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void ApplyPosition()
        {
            try
            {
                // 使用 WPF 逻辑工作区（自动适配 DPI 缩放），将窗口居中偏上显示
                var workArea = SystemParameters.WorkArea;
                double targetLeft = workArea.Left + (workArea.Width - Width) / 2;
                double targetTop = workArea.Top + (workArea.Height - Height) / 2 - 40;

                if (targetLeft < workArea.Left) targetLeft = workArea.Left + 20;
                if (targetTop < workArea.Top) targetTop = workArea.Top + 20;

                Left = targetLeft;
                Top = targetTop;
            }
            catch
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void OnServiceHistoryChanged()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (IsVisible)
                {
                    RefreshList();
                }
            });
        }

        private void RefreshList()
        {
            _allItems = _service.GetItems().ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string keyword = SearchBox.Text?.Trim();

            IEnumerable<ClipboardItem> filtered = _allItems;
            if (!string.IsNullOrEmpty(keyword))
            {
                filtered = filtered.Where(x =>
                    (x.FullText != null && x.FullText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (x.PreviewText != null && x.PreviewText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var list = filtered.ToList();
            HistoryList.ItemsSource = list;

            TxtHeaderInfo.Text = $"共 {list.Count} 条记录";
            TxtEmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (list.Count > 0)
            {
                HistoryList.SelectedIndex = 0;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                if (HistoryList.Items.Count > 0)
                {
                    HistoryList.Focus();
                    if (HistoryList.SelectedIndex < 0)
                    {
                        HistoryList.SelectedIndex = 0;
                    }
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitSelectedItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        }

        private void HistoryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitSelectedItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelectedItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        }

        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            CommitSelectedItem();
        }

        private void CommitSelectedItem()
        {
            if (HistoryList.SelectedItem is ClipboardItem item && !string.IsNullOrEmpty(item.FullText))
            {
                // 1. 抑制下一次系统广播防自环录入
                _service.SuppressNext(item.FullText);

                // 2. 回写至系统剪贴板
                ClipboardHelper.TrySetText(item.FullText);

                // 3. 隐藏浮窗
                Hide();
            }
        }

        private void DeleteSelectedItem()
        {
            if (HistoryList.SelectedItem is ClipboardItem item)
            {
                _service.RemoveItem(item.Id);
                RefreshList();
            }
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            _service.ClearAll();
            RefreshList();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void ClipboardHistoryWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _service.HistoryChanged -= OnServiceHistoryChanged;
            base.OnClosed(e);
        }
    }
}
