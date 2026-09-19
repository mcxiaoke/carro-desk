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

            Loaded += (s, e) => RefreshList();
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
            CommitSelectedItem(autoPaste: true);
        }

        private void CommitSelectedItem(bool autoPaste = true)
        {
            if (HistoryList.SelectedItem is ClipboardItem item && !string.IsNullOrEmpty(item.FullText))
            {
                // 1. 抑制下一次系统广播防自环录入
                _service.SuppressNext(item.FullText);

                // 2. 回写至系统剪贴板
                ClipboardHelper.TrySetText(item.FullText);

                // 3. 隐藏浮窗
                Hide();

                // 4. 若需要自动粘贴，异步模拟 Ctrl+V 发送至原前台活动应用
                if (autoPaste)
                {
                    ClipboardHelper.SimulatePaste();
                }
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

        private void MenuCopyAndPaste_Click(object sender, RoutedEventArgs e)
        {
            CommitSelectedItem(autoPaste: true);
        }

        private void MenuCopyOnly_Click(object sender, RoutedEventArgs e)
        {
            CommitSelectedItem(autoPaste: false);
        }

        private void MenuTogglePin_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is ClipboardItem item)
            {
                _service.TogglePin(item.Id);
                RefreshList();
            }
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedItem();
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is ClipboardItem item)
            {
                MessageBox.Show(item.FullText, $"剪贴板详情 ({item.TextLength} 字符)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnItemPin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ClipboardItem item)
            {
                _service.TogglePin(item.Id);
                RefreshList();
                e.Handled = true;
            }
        }

        private void BtnItemDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ClipboardItem item)
            {
                _service.RemoveItem(item.Id);
                RefreshList();
                e.Handled = true;
            }
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            int total = _allItems.Count;
            if (total == 0) return;

            int pinnedCount = _allItems.Count(x => x.IsPinned);
            string prompt = pinnedCount > 0
                ? $"确定要清空剪贴板历史记录吗？\n\n(已固定的 {pinnedCount} 条记录将被保留)"
                : "确定要清空所有剪贴板历史记录吗？此操作不可撤销。";

            if (MessageBox.Show(prompt, "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _service.ClearAll(preservePinned: true);
                RefreshList();
            }
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
