using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Modules.TaskScheduler.Views
{
    /// <summary>
    /// 单个任务的运行历史（只读）。
    ///
    /// 数据来自 RunHistoryStore.Query()（runs/history.jsonl 的 start/end/abandoned 事件归并），
    /// 一行 = 一次运行；双击打开该次运行的日志文件。运行中的实例会显示 running，
    /// 打开前先 FlushActive() 把内存缓冲落盘，避免刚写的日志读不到。
    /// </summary>
    public partial class RunHistoryWindow : Window
    {
        private const int MaxRows = 200;

        private readonly string _taskName;

        public RunHistoryWindow(string taskName)
        {
            InitializeComponent();
            _taskName = string.IsNullOrWhiteSpace(taskName) ? "" : taskName.Trim();
            TaskNameText.Text = _taskName;
            Title = string.Format(Loc.T("Tasks.HistoryTitle", "运行历史 - {0}"), _taskName);
            LoadRows();
        }

        private void LoadRows()
        {
            List<RunHistoryRow> rows;
            try
            {
                RunHistoryStore.Instance.FlushActive();
                rows = RunHistoryStore.Instance.Query(_taskName, MaxRows)
                    .Select(e => new RunHistoryRow(e))
                    .ToList();
            }
            catch (Exception)
            {
                rows = new List<RunHistoryRow>();
            }

            HistoryListView.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SummaryText.Text = string.Format(Loc.T("Tasks.HistorySummary", "{0} 条记录"), rows.Count);
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadRows();
        }

        private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = HistoryListView.SelectedItem as RunHistoryRow;
            if (row == null) return;
            string path = row.ResolveLogFullPath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show(this, Loc.Format("Tasks.NoLogFile", path ?? ""),
                    Loc.T("Tasks.NoLogTitle", "暂无日志"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.Format("Tasks.OpenLogFailed", ex.Message),
                    Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                TaskLogPaths.EnsureDirs();
                Process.Start(new ProcessStartInfo { FileName = TaskLogPaths.LogsDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.Format("Tasks.OpenLogsDirFailed", ex.Message),
                    Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>运行历史表格行（只读投影）。</summary>
    public sealed class RunHistoryRow
    {
        private readonly RunHistoryEntry _entry;

        public RunHistoryRow(RunHistoryEntry entry)
        {
            _entry = entry ?? new RunHistoryEntry();
        }

        public string StartText
        {
            get
            {
                if (_entry.StartUtc == DateTime.MinValue) return "--";
                return _entry.StartUtc.ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        public string Trigger
        {
            get { return string.IsNullOrEmpty(_entry.Trigger) ? "-" : _entry.Trigger; }
        }

        public string CommandDisplay
        {
            get
            {
                string text = ((_entry.Command ?? "") + " " + (_entry.Args ?? "")).Trim();
                return string.IsNullOrEmpty(text) ? "-" : text;
            }
        }

        public string DurationText
        {
            get
            {
                long ms = _entry.DurationMs;
                if (ms <= 0) return "-";
                if (ms < 1000) return ms.ToString(CultureInfo.InvariantCulture) + "ms";
                if (ms < 60000) return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
                return (ms / 60000).ToString(CultureInfo.InvariantCulture) + "m" +
                       ((ms % 60000) / 1000).ToString("00", CultureInfo.InvariantCulture) + "s";
            }
        }

        public string StatusText
        {
            get
            {
                if (!_entry.Completed) return "running";
                return string.IsNullOrEmpty(_entry.Outcome) ? "unknown" : _entry.Outcome;
            }
        }

        public string OutputText
        {
            get
            {
                // 运行中的 run 只有 run.start（不含输出统计），显示 0 会误导，统一用 "-"
                if (!_entry.Completed) return "-";
                string text = _entry.OutputLines.ToString(CultureInfo.InvariantCulture) + " · " +
                              FormatBytes(_entry.OutputBytes);
                return _entry.Truncated ? text + " · trunc" : text;
            }
        }

        public string ResolveLogFullPath()
        {
            return RunHistoryStore.Instance.ResolveLogPath(_entry.LogPath);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
