using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Services.Localization;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CarroDesk.Modules.ClipboardHistory.Views
{
    public partial class ClipboardHistoryWindow : Window
    {
        private readonly IClipboardHistoryService _service;
        private List<ClipboardItem> _allItems = new List<ClipboardItem>();
        private bool _isEnlarged;
        private double _normalWidth = 460;
        private double _normalHeight = 540;
        private double _normalLeft;
        private double _normalTop;
        private IntPtr _pasteTargetWindow;
        private ClipboardItemDetailWindow _detailWindow;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public bool AutoCloseOnDeactivate { get; set; } = true;

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
            if (!AutoCloseOnDeactivate)
            {
                return;
            }

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
            ShowAndActivate(capturePasteTarget: true);
        }

        /// <summary>
        /// 唤出并激活浮窗。
        /// </summary>
        /// <param name="capturePasteTarget">
        /// 是否把当前前台窗口记为粘贴目标。从全文详情窗口返回时必须传 false，
        /// 否则会把详情窗口误记为粘贴目标，导致"复制并粘贴"发错窗口。
        /// </param>
        public void ShowAndActivate(bool capturePasteTarget)
        {
            RefreshList();
            ApplyPosition();

            if (!IsVisible)
            {
                // 在历史浮窗抢前台前记录原目标，提交时只允许粘贴回该 HWND。
                if (capturePasteTarget)
                {
                    _pasteTargetWindow = GetForegroundWindow();
                }
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

            TxtHeaderInfo.Text = Loc.T("Clipboard.TotalCount", "共 {0} 条记录", list.Count);
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
            else if (e.Key == Key.V)
            {
                if (HistoryList.SelectedItem is ClipboardItem item)
                {
                    ShowDetailWindow(item);
                }
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
                // 1. 先回写至系统剪贴板；失败时绝不能隐藏窗口或发送全局 Ctrl+V，
                // 否则可能把剪贴板中的旧内容误粘贴到当前活动应用。
                if (!ClipboardHelper.TrySetText(item.FullText))
                {
                    MessageBox.Show(this,
                        Loc.T("Clipboard.WriteFailed", "无法写入系统剪贴板，请稍后重试"),
                        Loc.T("Common.Error", "错误"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // 2. 抑制由本次成功回写产生的下一次系统广播，避免自环录入。
                _service.SuppressNext(item.FullText);

                // 3. 隐藏浮窗
                Hide();

                // 4. 若需要自动粘贴，异步模拟 Ctrl+V 发送至原前台活动应用
                if (autoPaste)
                {
                    ClipboardHelper.SimulatePaste(_pasteTargetWindow);
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
                ShowDetailWindow(item);
            }
        }

        private void BtnItemDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ClipboardItem item)
            {
                ShowDetailWindow(item);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 打开（或复用）全文详情窗口显示条目内容。
        ///
        /// 复用策略：全局只保留一个详情窗口实例，重复打开时仅换内容并前置，
        /// 避免连点行内按钮刷出多个窗口。
        /// </summary>
        private void ShowDetailWindow(ClipboardItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullText))
            {
                return;
            }

            try
            {
                if (_detailWindow == null)
                {
                    _detailWindow = new ClipboardItemDetailWindow(_service, _pasteTargetWindow);
                    _detailWindow.Closed += DetailWindow_Closed;
                }
                else
                {
                    // 复用前同步最新的粘贴目标，避免窗口长时间开着后目标已失效
                    _detailWindow.SetPasteTarget(_pasteTargetWindow);
                }

                _detailWindow.LoadItem(item);

                // 浮窗是"失焦即隐"的一次性面板，详情窗口是常驻阅读窗口：
                // 这里主动隐藏浮窗，避免详情窗口被浮窗遮挡。
                if (IsVisible)
                {
                    Hide();
                }

                if (!_detailWindow.IsVisible)
                {
                    _detailWindow.Show();
                }
                _detailWindow.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    Loc.T("Clipboard.DetailOpenFailed", "无法打开内容详情窗口：{0}", ex.Message),
                    Loc.T("Common.Error", "错误"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void DetailWindow_Closed(object sender, EventArgs e)
        {
            if (_detailWindow != null)
            {
                _detailWindow.Closed -= DetailWindow_Closed;
                _detailWindow = null;
            }

            // 详情窗口关闭后回到历史浮窗；不重新采集粘贴目标，
            // 保持浮窗打开前记录的原目标 HWND 有效。
            if (!IsVisible && !IsClosing)
            {
                ShowAndActivate(capturePasteTarget: false);
            }
        }

        private bool IsClosing { get; set; }

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
                ? Loc.T("Clipboard.ClearPreservePinnedPrompt", "确定要清空剪贴板历史记录吗？\n\n(已固定的 {0} 条记录将被保留)", pinnedCount)
                : Loc.T("Clipboard.ClearConfirm", "确定要清空全部剪贴板历史记录吗？此操作不可撤销。");

            string title = Loc.T("Common.Confirm", "确认");
            if (MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _service.ClearAll(preservePinned: true);
                RefreshList();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            if (!_isEnlarged)
            {
                _normalWidth = Width;
                _normalHeight = Height;
                _normalLeft = Left;
                _normalTop = Top;

                // 放大为宽敞的大窗口展示更多内容
                double targetW = Math.Min(780, workArea.Width - 40);
                double targetH = Math.Min(720, workArea.Height - 60);

                Width = targetW;
                Height = targetH;
                Left = workArea.Left + (workArea.Width - targetW) / 2;
                Top = workArea.Top + (workArea.Height - targetH) / 2 - 20;

                _isEnlarged = true;
                BtnMaximize.Content = "❐";
                BtnMaximize.ToolTip = Loc.T("Clipboard.RestoreTip", "还原窗口大小");
            }
            else
            {
                Width = _normalWidth;
                Height = _normalHeight;
                Left = _normalLeft;
                Top = _normalTop;

                _isEnlarged = false;
                BtnMaximize.Content = "🗖";
                BtnMaximize.ToolTip = Loc.T("Clipboard.MaximizeTip", "放大 / 还原窗口");
            }
        }

        private void ResizeGripThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newW = Width + e.HorizontalChange;
            double newH = Height + e.VerticalChange;

            if (newW >= MinWidth && newW <= MaxWidth)
            {
                Width = newW;
            }
            if (newH >= MinHeight && newH <= MaxHeight)
            {
                Height = newH;
            }
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

            // 浮窗关闭时同步关掉详情窗口，避免模块停止后残留幽灵窗口
            IsClosing = true;
            if (_detailWindow != null)
            {
                try
                {
                    _detailWindow.Closed -= DetailWindow_Closed;
                    _detailWindow.Close();
                }
                catch
                {
                }
                _detailWindow = null;
            }

            base.OnClosed(e);
        }
    }
}
