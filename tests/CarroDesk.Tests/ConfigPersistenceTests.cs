using System;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Common;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 配置持久化的健壮性测试（P2-4 / P2-5 / P2-6）。
    /// </summary>
    [TestClass]
    public class ConfigPersistenceTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(TestEnvironment.TempRoot, "cfg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------- P2-6 孤儿临时文件清理 ----------

        [TestMethod]
        public void CleanupStaleTempFiles_RemovesOldOwnTempFiles_KeepsFreshAndForeign()
        {
            string dir = NewDir();
            string oldTemp = Path.Combine(dir, "config.json.tmp." + Guid.NewGuid().ToString("N"));
            string freshTemp = Path.Combine(dir, "config.json.tmp." + Guid.NewGuid().ToString("N"));
            string foreign = Path.Combine(dir, "notes.tmp.backup");

            File.WriteAllText(oldTemp, "x");
            File.WriteAllText(freshTemp, "x");
            File.WriteAllText(foreign, "x");

            File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-3));

            int removed = AtomicFile.CleanupStaleTempFiles(dir, TimeSpan.FromHours(1));

            Assert.AreEqual(1, removed, "只应删除过期且形如 <名字>.tmp.<32位hex> 的文件");
            Assert.IsFalse(File.Exists(oldTemp), "过期孤儿临时文件应被清理");
            Assert.IsTrue(File.Exists(freshTemp), "仍在写入窗口内的临时文件不得删除");
            Assert.IsTrue(File.Exists(foreign), "非本类生成的 .tmp. 文件不得删除");
        }

        [TestMethod]
        public void CleanupStaleTempFiles_MissingDirectory_IsNoOp()
        {
            Assert.AreEqual(0, AtomicFile.CleanupStaleTempFiles(Path.Combine(NewDir(), "nope"), TimeSpan.FromHours(1)));
            Assert.AreEqual(0, AtomicFile.CleanupStaleTempFiles(null, TimeSpan.FromHours(1)));
        }

        // ---------- P2-5 损坏文件自愈与 schema 版本 ----------

        [TestMethod]
        public void TaskConfig_Load_RecoversFromCorruptFile()
        {
            string path = TaskConfigService.FilePath;
            string original = "{ this is not valid json ";
            File.WriteAllText(path, original, Encoding.UTF8);

            var result = TaskConfigService.Load();

            Assert.IsTrue(result.FileRecovered, "损坏文件应触发备份 + 重建");
            Assert.IsNotNull(result.RecoveredBackupPath);
            Assert.IsTrue(File.Exists(result.RecoveredBackupPath), "备份文件应存在");
            Assert.AreEqual(original, File.ReadAllText(result.RecoveredBackupPath, Encoding.UTF8),
                "备份必须保留损坏前的原始内容");
            Assert.IsTrue(result.Errors.Any(e => e.IndexOf("not valid JSON", StringComparison.OrdinalIgnoreCase) >= 0
                                              || e.IndexOf("load exception", StringComparison.OrdinalIgnoreCase) >= 0),
                "应记录解析失败原因，实际: " + string.Join("; ", result.Errors));

            // 关键：重建之后必须能正常加载，否则用户会永远卡在"任务加载不出来"
            var second = TaskConfigService.Load();
            Assert.IsFalse(second.FileRecovered);
            Assert.AreEqual(0, second.Tasks.Count);
            Assert.AreEqual(0, second.Errors.Count, "重建后的文件应可无错加载，实际: " + string.Join("; ", second.Errors));
        }

        [TestMethod]
        public void TaskConfig_Load_EmptyFile_IsReportedNotSilentlyTreatedAsZeroTasks()
        {
            File.WriteAllText(TaskConfigService.FilePath, "   ", Encoding.UTF8);

            var result = TaskConfigService.Load();

            Assert.AreEqual(0, result.Tasks.Count);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("empty")),
                "空文件必须报错，而不是静默返回零任务。实际: " + string.Join("; ", result.Errors));
        }

        [TestMethod]
        public void TaskConfig_SchemaVersion_AbsentMeansV1_AndEnvelopeIsAccepted()
        {
            // 历史格式：裸数组，无版本字段 → 视为 v1
            File.WriteAllText(TaskConfigService.FilePath,
                "[{\"name\":\"t1\",\"enabled\":true,\"trigger\":{\"type\":\"manual\"},\"action\":{\"file\":\"cmd.exe\"}}]",
                Encoding.UTF8);

            var legacy = TaskConfigService.Load();
            Assert.AreEqual(TaskConfigService.CurrentSchemaVersion, legacy.SchemaVersion);
            Assert.AreEqual(1, legacy.Tasks.Count);

            // 带版本信封：应能识别版本并正常解析任务
            File.WriteAllText(TaskConfigService.FilePath,
                "{\"version\":1,\"tasks\":[{\"name\":\"t2\",\"enabled\":true,\"trigger\":{\"type\":\"manual\"},\"action\":{\"file\":\"cmd.exe\"}}]}",
                Encoding.UTF8);

            var enveloped = TaskConfigService.Load();
            Assert.AreEqual(1, enveloped.SchemaVersion);
            Assert.AreEqual(1, enveloped.Tasks.Count);
            Assert.AreEqual("t2", enveloped.Tasks[0].Name);
        }

        [TestMethod]
        public void TaskConfig_SchemaVersion_NewerThanSupported_IsReadOnlyAndPreservesFile()
        {
            string futureJson = "{\"version\":99,\"items\":[{\"name\":\"future-task\"}]}";
            File.WriteAllText(TaskConfigService.FilePath, futureJson, Encoding.UTF8);

            var result = TaskConfigService.Load();

            Assert.AreEqual(99, result.SchemaVersion);
            Assert.AreEqual(0, result.Tasks.Count, "旧版不得猜测解析未来 schema");
            Assert.IsTrue(result.Errors.Any(e => e.Contains("newer than supported")),
                "遇到不认识的更高版本必须留痕。实际: " + string.Join("; ", result.Errors));
            Assert.AreEqual(futureJson, File.ReadAllText(TaskConfigService.FilePath, Encoding.UTF8),
                "未来 schema 必须保持只读，绝不能被旧版重建为空数组");
        }

        [TestMethod]
        public void ConfigService_CorruptReload_PreservesLastValidSnapshotAndReturnsFalse()
        {
            var service = new ConfigService();
            service.LoadOrCreate();
            service.Current.Language = "zh-CN";
            service.SetModuleToken("ClipboardHistory", Newtonsoft.Json.Linq.JObject.Parse("{\"Enabled\":true,\"Hotkey\":\"Win+Alt+V\"}"));
            service.Save();

            string original = File.ReadAllText(ConfigService.FilePath, Encoding.UTF8);
            try
            {
                File.WriteAllText(ConfigService.FilePath, "{ invalid json", Encoding.UTF8);
                bool reloaded = service.Reload();

                Assert.IsFalse(reloaded, "损坏配置重载必须返回失败");
                Assert.AreEqual("zh-CN", service.Current.Language, "宿主字段必须保留上次有效值");
                Assert.IsNotNull(service.GetModuleToken("ClipboardHistory"), "模块配置必须保留上次有效值，不能与宿主默认值形成混合态");
                Assert.IsTrue(Directory.GetFiles(ConfigService.DirPath, "config.corrupt-*.json").Any(File.Exists));
            }
            finally
            {
                File.WriteAllText(ConfigService.FilePath, original, Encoding.UTF8);
            }
        }

        [TestMethod]
        public void TaskConfig_UnknownTrigger_IsRejectedWithoutBecomingStartup()
        {
            File.WriteAllText(TaskConfigService.FilePath,
                "[{\"name\":\"typo\",\"enabled\":true,\"trigger\":{\"type\":\"intervl\",\"every\":\"1h\"},\"action\":{\"file\":\"echo.exe\"}}]",
                Encoding.UTF8);

            var result = TaskConfigService.Load();

            Assert.AreEqual(0, result.Tasks.Count, "未知 trigger 必须被拒绝");
            Assert.IsTrue(result.Errors.Any(e => e.IndexOf("unknown trigger.type", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void TaskConfig_Alias_NormalizesToCanonicalIntervalType()
        {
            File.WriteAllText(TaskConfigService.FilePath,
                "[{\"name\":\"periodic\",\"enabled\":true,\"trigger\":{\"type\":\"periodic\",\"every\":\"1h\"},\"action\":{\"file\":\"echo.exe\"}}]",
                Encoding.UTF8);

            var result = TaskConfigService.Load();

            Assert.AreEqual(1, result.Tasks.Count);
            Assert.AreEqual(TaskTriggerType.Interval, result.Tasks[0].Trigger.Type);
            Assert.AreEqual("interval", TaskConfigService.GetCanonicalTriggerTag(result.Tasks[0].Trigger.Type));
        }

        // ---------- P2-4 PIN pending 与 current 语义一致 ----------

        [TestMethod]
        public void HostPinService_PendingPin_IsHonoredByVerifyBeforePersist()
        {
            // 数据目录已被测试隔离，这里不会碰到用户真实配置
            var config = new ConfigService();
            config.LoadOrCreate();

            var service = new HostPinService(config);
            service.SetNewPin("4321");

            Assert.IsTrue(service.IsConfigured, "设置了 pending PIN 即应视为已配置");

            // 关键：尚未写盘时也必须能用 pending 校验；
            // 此前 Verify 无条件走 current，会出现"显示已配置但永远验证失败"。
            Assert.IsTrue(service.Verify("4321"), "未落盘的 pending PIN 必须可校验");
            Assert.IsFalse(service.Verify("0000"));
        }
    }
}
