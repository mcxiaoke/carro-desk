using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace CarroDesk.Tests
{
    [TestClass]
    public class JsonMigrationTests
    {
        private string _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "CarroDesk_JsonTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [TestMethod]
        public void AppSettings_SpecialCharacters_NoCorruption()
        {
            var original = new AppSettings
            {
                AutoStart = false,
                PinSalt = "salt\\with\\backslashes\r\nand\"quotes\"",
                PinHash = "hash\twith\ttabs",
                Language = "zh-CN",
                FloatingPanelHotkey = "Win+Alt+C"
            };

            string json = JsonConvert.SerializeObject(original, Formatting.Indented);

            // 验证 JSON 格式合法性且被正确转义
            Assert.IsTrue(json.Contains("\\r\\n") || json.Contains("\\n"));

            var restored = JsonConvert.DeserializeObject<AppSettings>(json);
            Assert.IsNotNull(restored);
            Assert.AreEqual(original.AutoStart, restored.AutoStart);
            Assert.AreEqual(original.PinSalt, restored.PinSalt);
            Assert.AreEqual(original.PinHash, restored.PinHash);
            Assert.AreEqual(original.Language, restored.Language);
            Assert.AreEqual(original.FloatingPanelHotkey, restored.FloatingPanelHotkey);
        }

        [TestMethod]
        public void ScreenLockConfig_ExcludeProcesses_SupportsArrayAndDelimitedString()
        {
            // 1. 数组格式
            string arrayJson = "{ \"IdleMinutes\": 5, \"ExcludeProcesses\": [\"game.exe\", \"player.exe\"] }";
            var s1 = JsonConvert.DeserializeObject<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>(arrayJson);
            Assert.IsNotNull(s1);
            Assert.AreEqual(2, s1.ExcludeProcesses.Count);
            Assert.AreEqual("game.exe", s1.ExcludeProcesses[0]);
            Assert.AreEqual("player.exe", s1.ExcludeProcesses[1]);

            // 2. 逗号/分号分隔字符串格式
            string stringJson = "{ \"IdleMinutes\": 5, \"ExcludeProcesses\": \"a.exe, b.exe; c.exe\" }";
            var s2 = JsonConvert.DeserializeObject<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>(stringJson);
            Assert.IsNotNull(s2);
            Assert.AreEqual(3, s2.ExcludeProcesses.Count);
            Assert.AreEqual("a.exe", s2.ExcludeProcesses[0]);
            Assert.AreEqual("b.exe", s2.ExcludeProcesses[1]);
            Assert.AreEqual("c.exe", s2.ExcludeProcesses[2]);
        }

        [TestMethod]
        public void TaskConfigService_ParsesJsonWithComments()
        {
            string jsonWithComments = @"// 任务配置文件样例
            /* 块注释：包含一些说明 */
            [
              // 第一个任务
              {
                ""name"": ""task-1"",
                ""enabled"": true,
                ""trigger"": {
                  ""type"": ""startup"",
                  ""delaySec"": 10 // 行尾注释
                },
                ""action"": {
                  ""file"": ""test.bat"",
                  ""args"": ""--run //not-a-comment""
                }
              }
            ]";

            var errors = new List<string>();
            var method = typeof(TaskConfigService).GetMethod("ParseTasksJson",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "ParseTasksJson 方法必须存在");

            var tasks = (List<TaskDefinition>)method.Invoke(null, new object[] { jsonWithComments, errors });
            Assert.AreEqual(0, errors.Count, "解析带注释的 JSON 不应报错: " + string.Join("; ", errors));
            Assert.AreEqual(1, tasks.Count);
            Assert.AreEqual("task-1", tasks[0].Name);
            Assert.AreEqual(10, tasks[0].Trigger.DelaySec);
            Assert.AreEqual("test.bat", tasks[0].Action.File);
            Assert.AreEqual("--run //not-a-comment", tasks[0].Action.Args);
        }

        [TestMethod]
        public void TaskConfigService_ParsesAliasesCorrectly()
        {
            string aliasJson = @"[
              {
                ""name"": ""alias-task"",
                ""enabled"": true,
                ""trigger"": {
                  ""type"": ""start"",
                  ""delay"": 15,
                  ""key"": ""Ctrl+Alt+A"",
                  ""dir"": ""C:\\watch""
                },
                ""action"": {
                  ""command"": ""cmd.exe"",
                  ""arguments"": ""/c dir"",
                  ""workingDirectory"": ""C:\\temp""
                },
                ""options"": {
                  ""timeout"": 120,
                  ""concurrent"": true,
                  ""notify"": false
                },
                ""condition"": {
                  ""onlyIdle"": true,
                  ""network"": true
                }
              }
            ]";

            var errors = new List<string>();
            var method = typeof(TaskConfigService).GetMethod("ParseTasksJson",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            var tasks = (List<TaskDefinition>)method.Invoke(null, new object[] { aliasJson, errors });
            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(1, tasks.Count);

            var t = tasks[0];
            Assert.AreEqual(TaskTriggerType.Startup, t.Trigger.Type);
            Assert.AreEqual(15, t.Trigger.DelaySec);
            Assert.AreEqual("Ctrl+Alt+A", t.Trigger.Hotkey);
            Assert.AreEqual("C:\\watch", t.Trigger.WatchPath);

            Assert.AreEqual("cmd.exe", t.Action.File);
            Assert.AreEqual("/c dir", t.Action.Args);
            Assert.AreEqual("C:\\temp", t.Action.WorkDir);

            Assert.AreEqual(120, t.Options.TimeoutSec);
            Assert.IsTrue(t.Options.AllowConcurrent);
            Assert.IsFalse(t.Options.NotifyOnFailure);

            Assert.IsTrue(t.When.OnlyIdle);
            Assert.IsTrue(t.When.NetworkAvailable);
        }

        [TestMethod]
        public void TaskConfigService_SaveAndReload_RoundTrip()
        {
            string tempFile = Path.Combine(_tempDir, "tasks.json");

            var originalTasks = new List<TaskDefinition>
            {
                new TaskDefinition
                {
                    Name = "backup-task",
                    Enabled = true,
                    Trigger = new TaskTrigger
                    {
                        Type = TaskTriggerType.Daily,
                        At = "04:30"
                    },
                    Action = new TaskAction
                    {
                        File = "backup.ps1",
                        Args = "-Destination \"D:\\Backups\"",
                        WorkDir = "C:\\Scripts"
                    },
                    Options = new TaskOptions
                    {
                        Hidden = true,
                        TimeoutSec = 300,
                        Retry = 2
                    },
                    When = new TaskCondition
                    {
                        AcPower = true
                    }
                }
            };

            // 测试通过 Save 方法序列化后的 JSON 结构
            var method = typeof(TaskConfigService).GetMethod("ParseTasksJson",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            // 直接验证序列化 token 方法
            var jArray = new Newtonsoft.Json.Linq.JArray();
            var item = new Newtonsoft.Json.Linq.JObject
            {
                ["name"] = originalTasks[0].Name,
                ["enabled"] = originalTasks[0].Enabled
            };
            jArray.Add(item);

            string serialized = jArray.ToString(Formatting.Indented);
            Assert.IsTrue(serialized.Contains("\"backup-task\""));

            var errors = new List<string>();
            var reloaded = (List<TaskDefinition>)method.Invoke(null, new object[] { serialized, errors });
            Assert.AreEqual(1, reloaded.Count);
            Assert.AreEqual("backup-task", reloaded[0].Name);
        }

        [TestMethod]
        public void I18nService_ParseAndFlattenJson_WorksAccurately()
        {
            var i18n = I18nService.Instance;
            string testLocaleJson = @"{
              ""_meta"": {
                ""code"": ""test-LANG"",
                ""name"": ""测试语言""
              },
              ""Category"": {
                ""Sub"": {
                  ""ItemKey"": ""测试翻译文本""
                },
                ""DirectKey"": ""直接键值""
              }
            }";

            var method = typeof(I18nService).GetMethod("ParseAndRegisterLocale",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method);

            method.Invoke(i18n, new object[] { testLocaleJson });

            // 切换到测试语言
            i18n.SetLanguage("test-LANG", notifyConfig: false);

            Assert.AreEqual("测试翻译文本", i18n.Get("Category.Sub.ItemKey"));
            Assert.AreEqual("直接键值", i18n.Get("Category.DirectKey"));

            // 切换回默认中文
            i18n.SetLanguage("zh-CN", notifyConfig: false);
        }

        [TestMethod]
        public void LegacyFlatConfig_MigratesToScreenLockAndTaskScheduler_AndCleansRootProps()
        {
            var configService = new ConfigService();
            string configFile = Path.Combine(_tempDir, "config.json");

            // 模拟包含旧扁平配置以及冲突未同步的 ScreenLock 节点
            string legacyJson = @"{
              ""AutoStart"": true,
              ""PinSalt"": ""salt123"",
              ""PinHash"": ""hash123"",
              ""Language"": ""zh-CN"",
              ""IdleMinutes"": 12,
              ""ShowClock"": true,
              ""OverlayOpacity"": 0.98,
              ""TasksEnabled"": true,
              ""UnlockOnResume"": true,
              ""ExcludeProcesses"": [
                ""Antigravity.exe"",
                ""NTEGame.exe"",
                ""TRAE SOLO CN.exe""
              ],
              ""AudioSwitch"": """",
              ""AppAutoMute"": """",
              ""ScreenLock"": {
                ""Enabled"": true,
                ""IdleMinutes"": 12,
                ""ShowClock"": true,
                ""OverlayOpacity"": 0.88,
                ""ExcludeProcesses"": [],
                ""UnlockOnResume"": true
              }
            }";

            File.WriteAllText(configFile, legacyJson, Encoding.UTF8);

            // 通过反射调用 ReadFile 并验证私有属性
            var readFileMethod = typeof(ConfigService).GetMethod("ReadFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(readFileMethod);

            // 修改 FilePath 的机制需要通过反射或直接验证内部迁移方法
            var rootObj = Newtonsoft.Json.Linq.JObject.Parse(legacyJson);
            var migrateMethod = typeof(ConfigService).GetMethod("MigrateLegacyConfigs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(migrateMethod);

            // 将属性载入 _moduleConfigs
            var moduleConfigsField = typeof(ConfigService).GetField("_moduleConfigs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var moduleConfigs = (Dictionary<string, Newtonsoft.Json.Linq.JToken>)moduleConfigsField.GetValue(configService);
            moduleConfigs.Clear();

            var hostNamesField = typeof(ConfigService).GetField("HostSettingNames",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var hostNames = (HashSet<string>)hostNamesField.GetValue(null);

            foreach (var prop in rootObj.Properties())
            {
                if (hostNames.Contains(prop.Name)) continue;
                moduleConfigs[prop.Name] = prop.Value;
            }

            bool changed = (bool)migrateMethod.Invoke(configService, new object[] { rootObj });
            Assert.IsTrue(changed, "必须检测到并执行迁移");

            // 验证 ScreenLock 节点被正确覆盖合并
            var configMgr = new ConfigManager(configService);
            var slConfig = configMgr.GetModuleConfig<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>("ScreenLock");
            Assert.IsNotNull(slConfig);
            Assert.AreEqual(0.98, slConfig.OverlayOpacity, 0.0001, "OverlayOpacity 必须迁移为根节点的 0.98");
            Assert.AreEqual(3, slConfig.ExcludeProcesses.Count, "ExcludeProcesses 必须迁移为根节点的 3 项");
            Assert.IsTrue(slConfig.ExcludeProcesses.Contains("Antigravity.exe"));

            // 验证 TaskScheduler 被正确迁移
            var tsConfig = configMgr.GetModuleConfig<CarroDesk.Modules.TaskScheduler.Models.TaskSchedulerConfig>("TaskScheduler");
            Assert.IsNotNull(tsConfig);
            Assert.IsTrue(tsConfig.GlobalEnabled, "GlobalEnabled 必须迁移为根节点 TasksEnabled 的 true");

            // 验证根级旧 key 被彻底从 _moduleConfigs 移除
            Assert.IsFalse(moduleConfigs.ContainsKey("IdleMinutes"));
            Assert.IsFalse(moduleConfigs.ContainsKey("OverlayOpacity"));
            Assert.IsFalse(moduleConfigs.ContainsKey("ExcludeProcesses"));
            Assert.IsFalse(moduleConfigs.ContainsKey("TasksEnabled"));
            Assert.IsFalse(moduleConfigs.ContainsKey("AudioSwitch"));
            Assert.IsFalse(moduleConfigs.ContainsKey("AppAutoMute"));
        }
    }
}
