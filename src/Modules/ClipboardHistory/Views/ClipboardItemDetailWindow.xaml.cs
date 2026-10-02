using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Services.Localization;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CarroDesk.Modules.ClipboardHistory.Views
{
    /// <summary>
    /// 剪贴板条目全文查看窗口。
    ///
    /// 设计要点：
    ///   1. 独立窗口（非 MessageBox），文本可选中、Ctrl+A 全选、Ctrl+C 复制、滚动浏览；
    ///   2. 打开时自动全选，便于直接 Ctrl+C；不抢剪贴板内容；
    ///   3. 回写剪贴板后调用 <see cref="IClipboardHistoryService.SuppressNext"/>，
    ///      抑制监听器把本次回写再次录入历史造成自环；
    ///   4. 超长文本只做显示层截断（复制仍为全文），避免大文本卡死 UI。
    /// </summary>
    public partial class ClipboardItemDetailWindow : Window
    {
        /// <summary>超过该字符数时仅渲染前 N 字符，避免超大文本导致 UI 卡顿；复制仍为全文。</summary>
        private const int MaxRenderChars = 200000;

        private readonly IClipboardHistoryService _service;
        private ClipboardItem _item;
        private string _fullText;

        /// <summary>原始前台窗口句柄："复制并粘贴"时只允许粘贴回该窗口，避免误贴到其它应用。</summary>
        private IntPtr _pasteTargetWindow;

        public ClipboardItemDetailWindow(IClipboardHistoryService service)
            : this(service, IntPtr.Zero)
        {
        }

        /// <summary>
        /// 构造详情窗口。
        /// </summary>
        /// <param name="service">剪贴板历史服务，用于回写后抑制自环录入；可为 null。</param>
        /// <param name="pasteTargetWindow">原始前台窗口句柄，"复制并粘贴"时只允许粘贴回该窗口；可为 IntPtr.Zero。</param>
        public ClipboardItemDetailWindow(IClipboardHistoryService service, IntPtr pasteTargetWindow)
        {
            InitializeComponent();
            _service = service;
            _pasteTargetWindow = pasteTargetWindow;
        }

        /// <summary>更新粘贴目标窗口（复用窗口时调用），避免长开窗口后目标句柄已失效。</summary>
        public void SetPasteTarget(IntPtr pasteTargetWindow)
        {
            _pasteTargetWindow = pasteTargetWindow;
        }

        /// <summary>当前详情窗口中显示的文本（超长时被截断，测试与诊断用）。</summary>
        public string DisplayText => TxtFullText.Text;

        /// <summary>元信息文本，形如 "123 字符 · 4 行"（测试与诊断用）。</summary>
        public string MetaInfo => TxtMeta.Text;

        /// <summary>截断提示是否可见（测试与诊断用）。</summary>
        public bool IsTruncated => TxtTruncated.Visibility == Visibility.Visible;

        /// <summary>该条目完整原文（不受显示截断影响）。</summary>
        public string FullText => _fullText;

        /// <summary>载入条目内容并显示（不激活窗口，便于调用方决定 Show/Activate 时机）。</summary>
        public void LoadItem(ClipboardItem item)
        {
            _item = item;
            _fullText = item?.FullText ?? string.Empty;

            TxtPinned.Visibility = (item != null && item.IsPinned) ? Visibility.Visible : Visibility.Collapsed;

            TxtMeta.Text = Loc.T("Clipboard.DetailMetaFormat", "{0} 字符 · {1} 行", _fullText.Length, CountLines(_fullText));
            TxtCopiedAt.Text = item != null
                ? Loc.T("Clipboard.DetailCopiedAt", "复制于 {0}", item.CopiedAt.ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

            // 超长文本仅截断显示，复制仍走 _fullText 原文
            string renderText = _fullText;
            if (renderText.Length > MaxRenderChars)
            {
                renderText = renderText.Substring(0, MaxRenderChars);
                TxtTruncated.Visibility = Visibility.Visible;
                TxtTruncated.Text = Loc.T("Clipboard.DetailTruncated",
                    "内容过长（{0} 字符），仅显示前 {1} 字符；复制仍为完整内容。",
                    _fullText.Length, MaxRenderChars);
            }
            else
            {
                TxtTruncated.Visibility = Visibility.Collapsed;
            }

            TxtFullText.Text = renderText;
            TxtStatus.Text = Loc.T("Clipboard.DetailHint", "Ctrl+A 全选 · Ctrl+C 复制 · Esc 关闭");
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // 打开即聚焦并全选：可直接 Ctrl+C 拿走全文，也可取消选择后局部复制
            TxtFullText.Focus();
            TxtFullText.SelectAll();
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        private void ChkWrap_Changed(object sender, RoutedEventArgs e)
        {
            TxtFullText.TextWrapping = ChkWrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            TxtFullText.Focus();
            TxtFullText.SelectAll();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(closeAfter: false);
        }

        private void BtnCopyAndPaste_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(closeAfter: true);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 回写全文到系统剪贴板。<paramref name="closeAfter"/> 为 true 时关闭窗口并模拟粘贴回原目标窗口。
        /// </summary>
        private void CopyToClipboard(bool closeAfter)
        {
            if (string.IsNullOrEmpty(_fullText))
            {
                ShowStatus(Loc.T("Clipboard.DetailEmpty", "该条目内容为空"), isError: true);
                return;
            }

            // 先确认写回成功，再做任何关闭 / 模拟粘贴动作：
            // 写失败时若继续粘贴，会把剪贴板中的旧内容误发到目标应用。
            if (!ClipboardHelper.TrySetText(_fullText))
            {
                ShowStatus(Loc.T("Clipboard.WriteFailed", "无法写入系统剪贴板，请稍后重试"), isError: true);
                return;
            }

            // 抑制本次回写产生的系统广播，避免条目被再次录入历史
            _service?.SuppressNext(_fullText);

            if (!closeAfter)
            {
                ShowStatus(Loc.T("Clipboard.DetailCopied", "已复制全文到剪贴板（{0} 字符）", _fullText.Length), isError: false);
                return;
            }

            // 关闭自身让前台回到原目标窗口，再异步发送 Ctrl+V
            Close();
            ClipboardHelper.SimulatePaste(_pasteTargetWindow);
        }

        private void ShowStatus(string message, bool isError)
        {
            TxtStatus.Text = message;
            TxtStatus.Foreground = isError
                ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26))
                : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x09, 0x69, 0xDA));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
