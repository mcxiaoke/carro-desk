using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 剪贴板 JSON 存储的写入语义测试（P1-7）。
    /// </summary>
    [TestClass]
    public class ClipboardStorageTests
    {
        private static string NewPath()
        {
            return Path.Combine(TestEnvironment.TempRoot, "clip-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TestMethod]
        public void Save_ThenFlush_RoundTripsItems()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "alpha", TextLength = 5, IsPinned = true },
                    new ClipboardItem { FullText = "beta", TextLength = 4 }
                });
                storage.Flush();

                Assert.IsTrue(File.Exists(path), "Flush 后文件应已落盘");

                var loaded = storage.Load();
                Assert.AreEqual(2, loaded.Count);
                Assert.AreEqual("alpha", loaded[0].FullText);
                Assert.IsTrue(loaded[0].IsPinned);
                Assert.AreEqual("beta", loaded[1].FullText);
            }
        }

        [TestMethod]
        public void Save_IsolatesSnapshotFromLaterMutation()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                var live = new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "first", TextLength = 5 }
                };

                storage.Save(live);

                // Save 之后立刻改动原列表（模拟服务继续在 UI 线程写入）
                live[0].FullText = "mutated-after-save";
                live.Add(new ClipboardItem { FullText = "second", TextLength = 6 });

                storage.Flush();

                var loaded = storage.Load();
                Assert.AreEqual(1, loaded.Count, "写盘应基于 Save 时刻的快照，而不是之后被改动的列表");
                Assert.AreEqual("first", loaded[0].FullText, "异步写盘读到了后续被修改的值");
            }
        }

        [TestMethod]
        public void Save_MultipleTimes_CoalescesToLatestSnapshot()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v1", TextLength = 2 } });
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v2", TextLength = 2 } });
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v3", TextLength = 2 } });
                storage.Flush();

                var loaded = storage.Load();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("v3", loaded[0].FullText, "合并写入应保留最后一版快照");
            }
        }

        [TestMethod]
        public void Load_OnMissingFile_ReturnsEmptyList()
        {
            using (var storage = new JsonClipboardHistoryStorage(NewPath()))
            {
                Assert.AreEqual(0, storage.Load().Count);
            }
        }

        [TestMethod]
        public void Save_OverwritesExistingFile_WithoutLosingContent()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "old", TextLength = 3 } });
                storage.Flush();
                Assert.AreEqual("old", storage.Load()[0].FullText);

                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "new", TextLength = 3 } });
                storage.Flush();
                Assert.AreEqual("new", storage.Load()[0].FullText);
            }
        }

        // ===================== 加密存储（DPAPI） =====================

        private static string NewEncPath()
        {
            return Path.Combine(TestEnvironment.TempRoot, "clip-enc-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static string ReadFileText(string path)
        {
            return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }

        private static string CorruptCipherPayload(string encryptedText)
        {
            byte[] payload = Convert.FromBase64String(encryptedText.Trim());
            payload[payload.Length / 2] ^= 0xFF;
            return Convert.ToBase64String(payload);
        }

        [TestMethod]
        public void Encrypt_RoundTripsItems()
        {
            string path = NewEncPath();
            var crypto = new ClipboardStorageCrypto { Enabled = true };
            using (var storage = new JsonClipboardHistoryStorage(path, crypto))
            {
                storage.Save(new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "秘密内容", TextLength = 4, IsPinned = true },
                    new ClipboardItem { FullText = "second", TextLength = 6 }
                });
                storage.Flush();

                Assert.IsTrue(File.Exists(path), "Flush 后文件应已落盘");
                string raw = ReadFileText(path);
                Assert.IsTrue(ClipboardStorageCrypto.IsEncryptedText(raw), "开启加密后磁盘文件应为密文格式");
                Assert.IsFalse(raw.Contains("秘密内容"), "密文文件不得包含明文内容");

                var loaded = storage.Load();
                Assert.AreEqual(2, loaded.Count);
                Assert.AreEqual("秘密内容", loaded[0].FullText);
                Assert.IsTrue(loaded[0].IsPinned);
            }
        }

        [TestMethod]
        public void EncryptedFile_WithoutCrypto_IsArchivedAndLoadReturnsEmpty()
        {
            string path = NewEncPath();

            // 先用加密 storage 写一份密文
            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = true }))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "secret", TextLength = 6 } });
                storage.Flush();
            }

            string archivedPath = null;
            using (var storage = new JsonClipboardHistoryStorage(path)) // 无 crypto：无法解密
            {
                storage.DecryptionFailed += p => archivedPath = p;
                var loaded = storage.Load();

                Assert.AreEqual(0, loaded.Count, "无法解密时应返回空列表");
                Assert.IsNotNull(archivedPath, "应触发 DecryptionFailed 并给出留档路径");
                Assert.IsTrue(File.Exists(archivedPath), "留档文件应存在");
                Assert.IsFalse(File.Exists(path), "原路径应已腾空，避免空历史覆盖密文");
                StringAssert.StartsWith(Path.GetFileName(archivedPath), "clip-enc-");
                StringAssert.Contains(Path.GetFileName(archivedPath), ".undecryptable-");
            }
        }

        [TestMethod]
        public void CorruptedCipher_IsArchived_ThenStorageKeepsWorking()
        {
            string path = NewEncPath();

            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = true }))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "secret", TextLength = 6 } });
                storage.Flush();
            }

            // 篡改 DPAPI 密文主体（保留 magic 头，格式合法但解密必失败）
            string corrupted = CorruptCipherPayload(ReadFileText(path));
            File.WriteAllText(path, corrupted, System.Text.Encoding.UTF8);
            Assert.IsTrue(ClipboardStorageCrypto.IsEncryptedText(corrupted), "篡改后仍是密文格式");

            int failureCount = 0;
            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = true }))
            {
                storage.DecryptionFailed += p => { failureCount++; Assert.IsNotNull(p); };
                Assert.AreEqual(0, storage.Load().Count, "解密失败应返回空列表");
                Assert.AreEqual(1, failureCount);
                Assert.IsFalse(File.Exists(path), "解密失败后原文件应已留档改名");

                // 留档成功不冻结 I/O：随后的保存应恢复正常落盘
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "fresh", TextLength = 5 } });
                storage.Flush();
                Assert.IsTrue(File.Exists(path), "留档成功后存储应恢复正常写入");
                Assert.AreEqual("fresh", storage.Load()[0].FullText);
            }
        }

        [TestMethod]
        public void PlainFile_LoadsWhileCryptoEnabled_ThenSavesEncrypted()
        {
            string path = NewEncPath();

            // 磁盘是明文、开关为开（配置丢失/被手改的场景）：自识别读入，落盘自愈为密文
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(
                new List<ClipboardItem> { new ClipboardItem { FullText = "legacy", TextLength = 6 } }),
                System.Text.Encoding.UTF8);

            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = true }))
            {
                Assert.AreEqual("legacy", storage.Load()[0].FullText);

                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "legacy", TextLength = 6 } });
                storage.Flush();

                Assert.IsTrue(ClipboardStorageCrypto.IsEncryptedText(ReadFileText(path)), "落盘后应自愈为密文");
                Assert.AreEqual("legacy", storage.Load()[0].FullText);
            }
        }

        [TestMethod]
        public void EncryptedFile_LoadsWhileCryptoDisabled_ThenSavesPlain()
        {
            string path = NewEncPath();

            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = true }))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "secret", TextLength = 6 } });
                storage.Flush();
            }

            using (var storage = new JsonClipboardHistoryStorage(path, new ClipboardStorageCrypto { Enabled = false }))
            {
                Assert.AreEqual("secret", storage.Load()[0].FullText, "开关关闭也应按文件头自识别解密读入");

                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "secret", TextLength = 6 } });
                storage.Flush();

                string raw = ReadFileText(path);
                Assert.IsFalse(ClipboardStorageCrypto.IsEncryptedText(raw), "关闭开关后落盘应为明文");
                Assert.IsTrue(raw.Contains("secret"), "明文文件应可直接读出内容");
            }
        }

        [TestMethod]
        public void ToggleFormats_KeepsDataIntact()
        {
            string path = NewEncPath();
            var crypto = new ClipboardStorageCrypto();
            using (var storage = new JsonClipboardHistoryStorage(path, crypto))
            {
                var items = new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "第一条", TextLength = 3, IsPinned = true },
                    new ClipboardItem { FullText = "second item", TextLength = 11 }
                };

                crypto.Enabled = false;
                storage.Save(items);
                storage.Flush();
                Assert.AreEqual(2, storage.Load().Count);

                crypto.Enabled = true;
                storage.Save(items);
                storage.Flush();
                Assert.AreEqual(2, storage.Load().Count, "明文→密文切换不得丢数据");

                crypto.Enabled = false;
                storage.Save(items);
                storage.Flush();
                var final = storage.Load();
                Assert.AreEqual(2, final.Count, "密文→明文切换不得丢数据");
                Assert.AreEqual("第一条", final[0].FullText);
                Assert.IsTrue(final[0].IsPinned);
                Assert.AreEqual("second item", final[1].FullText);
            }
        }
    }
}
