using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CarroDesk.Host.Commands
{
    /// <summary>单条审计记录（IPC 设计 §6.2）。参数只记摘要（哈希+长度），口令原文绝不落盘（§9.6 第 5 条）。</summary>
    public sealed class CommandAuditEntry
    {
        public DateTime Ts { get; set; }
        public string Source { get; set; }
        public string Method { get; set; }
        public string ParamsDigest { get; set; }
        public bool Ok { get; set; }
        public int Code { get; set; }
        public long ElapsedMs { get; set; }
    }

    /// <summary>审计出口抽象：单测用内存实现，生产用 <see cref="FileCommandAuditSink"/>。</summary>
    public interface ICommandAuditSink
    {
        void Write(CommandAuditEntry entry);
    }

    /// <summary>审计关闭（Ipc.AuditEnabled=false）时的空出口。</summary>
    public sealed class NullCommandAuditSink : ICommandAuditSink
    {
        public void Write(CommandAuditEntry entry) { }
    }

    /// <summary>
    /// 单行 JSON 追加写，超 <c>maxBytes</c> 轮转为 ".1"（IPC 设计 §6.2）。
    /// 全部 I/O 吞异常：审计失败不得影响命令执行结果。
    /// </summary>
    public sealed class FileCommandAuditSink : ICommandAuditSink
    {
        private static readonly object FileLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _path;
        private readonly long _maxBytes;

        public FileCommandAuditSink(string path = null, long maxBytes = 1024 * 1024)
        {
            _path = string.IsNullOrEmpty(path)
                ? Path.Combine(CarroDesk.Services.ConfigService.DirPath, "ipc-audit.log")
                : path;
            _maxBytes = maxBytes;
        }

        public void Write(CommandAuditEntry entry)
        {
            if (entry == null) return;
            try
            {
                var line = JsonConvert.SerializeObject(entry, Formatting.None) + Environment.NewLine;
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                lock (FileLock)
                {
                    try
                    {
                        var info = new FileInfo(_path);
                        if (info.Exists && info.Length >= _maxBytes)
                        {
                            var backup = _path + ".1";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(_path, backup);
                        }
                    }
                    catch { /* 轮转失败不阻断追加 */ }
                    File.AppendAllText(_path, line, Utf8NoBom);
                }
            }
            catch { /* 审计失败不影响执行结果 */ }
        }

        /// <summary>
        /// 参数摘要：SHA-256 前 16 hex + 原文长度。只用于比对/检索，不可还原原文；
        /// 口令不在参数字典内（<see cref="CommandRequest.Pin"/> 独立字段），天然不入摘要。
        /// </summary>
        public static string DigestParams(IDictionary<string, object> parameters)
        {
            string raw;
            try
            {
                raw = parameters == null
                    ? string.Empty
                    : JsonConvert.SerializeObject(parameters, Formatting.None);
            }
            catch
            {
                raw = "<unserializable>";
            }

            string hashHex;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                hashHex = sb.ToString();
            }
            return "sha256:" + hashHex + ":len=" + raw.Length;
        }
    }
}
