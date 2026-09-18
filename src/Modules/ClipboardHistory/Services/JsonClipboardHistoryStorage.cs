using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CarroDesk.Modules.ClipboardHistory.Models;
using Newtonsoft.Json;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public class JsonClipboardHistoryStorage : IClipboardHistoryStorage
    {
        private readonly string _filePath;
        private readonly object _ioLock = new object();

        public JsonClipboardHistoryStorage(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public List<ClipboardItem> Load()
        {
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
                catch
                {
                    return new List<ClipboardItem>();
                }
            }
        }

        public void Save(List<ClipboardItem> items)
        {
            if (items == null) return;

            lock (_ioLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string json = JsonConvert.SerializeObject(items, Formatting.Indented);
                    string tempPath = _filePath + ".tmp";

                    File.WriteAllText(tempPath, json, Encoding.UTF8);

                    if (File.Exists(_filePath))
                    {
                        File.Delete(_filePath);
                    }

                    File.Move(tempPath, _filePath);
                }
                catch
                {
                    // 异常自愈，防止持久化 I/O 击穿业务
                }
            }
        }
    }
}
