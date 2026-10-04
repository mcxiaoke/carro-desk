using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// 可选加密：构造时传入 <see cref="ClipboardStorageCrypto"/> 后，磁盘文件可切换为
    /// DPAPI 密文格式（Base64 + magic 头）。Load 一律按文件头自识别格式，与开关状态无关；
    /// 解密失败时原文件改名留档（.undecryptable-*）并触发 <see cref="DecryptionFailed"/>，
    /// 不走损坏备份路径。设计细节见 docs/CLIPBOARD-ENCRYPTED-STORAGE-DESIGN-20261004.md。
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
        private readonly ClipboardStorageCrypto _crypto;
        private readonly object _ioLock = new object();

        private List<ClipboardItem> _pendingSnapshot;
        private bool _writing;
        private bool _disposed;
        // 保险丝：加密历史解密失败且留档也失败时冻结 I/O，禁止空历史覆盖密文
        private bool _ioFrozen;
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        /// <summary>
        /// 加密历史在当前环境解密失败且已成功留档后触发（参数为留档文件路径；
        /// 留档失败时为 null，此时存储已冻结 I/O）。
        /// </summary>
        public event Action<string> DecryptionFailed;

        public JsonClipboardHistoryStorage(string filePath)
            : this(filePath, null)
        {
        }

        public JsonClipboardHistoryStorage(string filePath, ClipboardStorageCrypto crypto)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _crypto = crypto;
        }

        public List<ClipboardItem> Load()
        {
            // 等待挂起写入完成，避免读到上一版内容
            Flush();

            lock (_ioLock)
            {
                if (_ioFrozen)
                {
                    return new List<ClipboardItem>();
                }

                try
                {
                    if (!File.Exists(_filePath))
                    {
                        return new List<ClipboardItem>();
                    }

                    string raw = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        return new List<ClipboardItem>();
                    }

                    // 密文自识别：与配置状态无关，避免配置与磁盘不一致时把密文当损坏清掉
                    if (ClipboardStorageCrypto.IsEncryptedText(raw))
                    {
                        if (_crypto == null)
                        {
                            ArchiveUndecryptable();
                            return new List<ClipboardItem>();
                        }

                        string json;
                        try
                        {
                            json = _crypto.DecryptToText(raw);
                        }
                        catch (StorageDecryptionException ex)
                        {
                            Debug.WriteLine("[ClipboardStorage] decrypt failed: " + ex.Message);
                            ArchiveUndecryptable();
                            return new List<ClipboardItem>();
                        }

                        return ParseItems(json);
                    }

                    return ParseItems(raw);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ClipboardStorage] load failed: " + ex.Message);
                    try
                    {
                        string dir = Path.GetDirectoryName(_filePath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        string backup = _filePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json";
                        File.Copy(_filePath, backup, true);
                        Debug.WriteLine("[ClipboardStorage] corrupt history backed up to: " + backup);
                    }
                    catch { }
                    return new List<ClipboardItem>();
                }
            }
        }

        private static List<ClipboardItem> ParseItems(string json)
        {
            var items = JsonConvert.DeserializeObject<List<ClipboardItem>>(json);
            if (items != null && items.Any(x => x == null))
                throw new InvalidDataException("clipboard history contains null item");
            return items ?? new List<ClipboardItem>();
        }

        /// <summary>
        /// 加密历史无法解密（DPAPI masterkey 丢失、换 Windows 用户运行）：原文件改名留档，
        /// 腾出干净路径让后续保存从空历史开始；留档失败则冻结 I/O 禁止覆盖原文件。
        /// </summary>
        private void ArchiveUndecryptable()
        {
            string archived = null;
            try
            {
                archived = _filePath + ".undecryptable-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(_filePath, archived);
                Debug.WriteLine("[ClipboardStorage] undecryptable history archived to: " + archived);
            }
            catch (Exception ex)
            {
                _ioFrozen = true;
                Debug.WriteLine("[ClipboardStorage] archive undecryptable history failed, io frozen: " + ex.Message);
            }

            var handler = DecryptionFailed;
            if (handler != null)
            {
                try { handler(archived); } catch { }
            }
        }

        public void Save(List<ClipboardItem> items)
        {
            if (items == null) return;

            bool startWriter = false;
            lock (_ioLock)
            {
                if (_disposed || _ioFrozen) return;
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

                // Enabled 只决定写盘格式；Load 靠文件头自识别，配置与磁盘短暂不一致可自愈
                string output = json;
                var crypto = _crypto;
                if (crypto != null && crypto.Enabled)
                {
                    output = crypto.EncryptToText(json);
                }

                AtomicFile.WriteAllText(_filePath, output, Encoding.UTF8);
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
