using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Common;
using CarroDesk.Modules.ClipboardHistory.Models;
using Newtonsoft.Json;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    /// <summary>
    /// JSON 文件持久化。
    ///
    /// 写入策略：<see cref="Save"/> 只做"登记最新快照"并立即返回，真正的序列化与落盘
    /// 由后台线程合并执行（高频复制时只写最后一版，天然防抖）。
    ///
    /// 这样做的原因：本存储的调用点在 UI 线程（剪贴板事件封送到 Dispatcher 后触发），
    /// 1000 条历史的全量 JSON 序列化 + 磁盘 I/O 会直接卡住界面。同时改为
    /// <see cref="AtomicFile"/> 原子替换，消除原先 Delete+Move 之间"文件不存在"的窗口
    /// （进程崩溃/断电即丢历史）。
    ///
    /// 代价：<see cref="Save"/> 返回时数据尚未落盘。需要确定性持久化的场景
    /// （退出、测试）请调用 <see cref="Flush"/>。
    /// </summary>
    public class JsonClipboardHistoryStorage : IClipboardHistoryStorage, IDisposable
    {
        private readonly string _filePath;
        private readonly object _ioLock = new object();

        private List<ClipboardItem> _pendingSnapshot;
        private bool _writing;
        private bool _disposed;
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        public JsonClipboardHistoryStorage(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public List<ClipboardItem> Load()
        {
            // 等待挂起写入完成，避免读到上一版内容
            Flush();

            lock (_ioLock)
            {
                try
                {
                    if (!File.Exists(_filePath))
                    {
                        return new List<ClipboardItem>();
                    }

                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<ClipboardItem>();
                    }

                    var items = JsonConvert.DeserializeObject<List<ClipboardItem>>(json);
                    return items ?? new List<ClipboardItem>();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ClipboardStorage] load failed: " + ex.Message);
                    return new List<ClipboardItem>();
                }
            }
        }

        public void Save(List<ClipboardItem> items)
        {
            if (items == null) return;

            bool startWriter = false;
            lock (_ioLock)
            {
                if (_disposed) return;
                _pendingSnapshot = Snapshot(items);
                if (!_writing)
                {
                    _writing = true;
                    startWriter = true;
                    _idle.Reset();
                }
            }

            if (startWriter)
            {
                Task.Run(new Action(WriteLoop));
            }
        }

        /// <summary>等待已登记的写入全部落盘（退出流程与确定性测试使用）。</summary>
        public void Flush()
        {
            try
            {
                _idle.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ClipboardStorage] flush wait failed: " + ex.Message);
            }
        }

        private void WriteLoop()
        {
            while (true)
            {
                List<ClipboardItem> snapshot;
                lock (_ioLock)
                {
                    snapshot = _pendingSnapshot;
                    _pendingSnapshot = null;
                    if (snapshot == null)
                    {
                        // 必须在同一临界区内置位，否则会与 Save 的"是否启动写线程"判断竞争
                        _writing = false;
                        _idle.Set();
                        return;
                    }
                }

                WriteSnapshot(snapshot);
            }
        }

        private void WriteSnapshot(List<ClipboardItem> snapshot)
        {
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                AtomicFile.WriteAllText(_filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 持久化失败不能击穿业务，但必须留痕
                Debug.WriteLine("[ClipboardStorage] save failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 快照：调用方传入的是服务内部可变列表，且元素会被原地修改
        /// （CopiedAt/IsPinned），异步写盘必须基于独立副本，否则会序列化到半更新状态。
        /// </summary>
        private static List<ClipboardItem> Snapshot(List<ClipboardItem> items)
        {
            var copy = new List<ClipboardItem>(items.Count);
            foreach (var item in items)
            {
                if (item == null) continue;
                copy.Add(new ClipboardItem
                {
                    Id = item.Id,
                    FullText = item.FullText,
                    PreviewText = item.PreviewText,
                    TextLength = item.TextLength,
                    CopiedAt = item.CopiedAt,
                    Hash = item.Hash,
                    IsPinned = item.IsPinned
                });
            }
            return copy;
        }

        public void Dispose()
        {
            Flush();
            lock (_ioLock)
            {
                _disposed = true;
            }
            try { _idle.Dispose(); } catch { }
        }
    }
}
