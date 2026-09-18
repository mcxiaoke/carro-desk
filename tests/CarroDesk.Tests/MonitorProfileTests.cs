using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.MonitorProfile;
using CarroDesk.Modules.MonitorProfile.Models;
using CarroDesk.Modules.MonitorProfile.Services;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class MonitorProfileTests
    {
        [TestMethod]
        public void MonitorTimeSetting_ToTimeSpan_ParsesCorrectly()
        {
            var setting1 = new MonitorTimeSetting { Time = "08:30", Brightness = 60, Contrast = 70 };
            Assert.AreEqual(new TimeSpan(8, 30, 0), setting1.ToTimeSpan());

            var setting2 = new MonitorTimeSetting { Time = "00:00", Brightness = 20, Contrast = 50 };
            Assert.AreEqual(TimeSpan.Zero, setting2.ToTimeSpan());

            var settingInvalid = new MonitorTimeSetting { Time = "invalid", Brightness = 50, Contrast = 50 };
            Assert.AreEqual(TimeSpan.Zero, settingInvalid.ToTimeSpan());
        }

        [TestMethod]
        public void MonitorProfileConfig_CreateDefault_HasExpectedStructure()
        {
            var cfg = MonitorProfileConfig.CreateDefault();
            Assert.IsTrue(cfg.Enabled);
            Assert.IsTrue(cfg.AutoSchedule);
            Assert.AreEqual("Daily", cfg.ActiveProfile);
            Assert.AreEqual(5, cfg.BrightnessStep);

            Assert.IsTrue(cfg.Profiles.ContainsKey("Daily"));
            Assert.IsTrue(cfg.Profiles.ContainsKey("Game"));
            Assert.IsTrue(cfg.Profiles.ContainsKey("Night"));

            Assert.IsTrue(cfg.Profiles["Daily"].Count >= 2);
            Assert.IsNotNull(cfg.Hotkeys);
            Assert.IsFalse(string.IsNullOrEmpty(cfg.Hotkeys.SwitchToDailyMode));
            Assert.IsFalse(string.IsNullOrEmpty(cfg.Hotkeys.IncreaseBrightness));
        }

        [TestMethod]
        public void ProfileScheduleEngine_TimeMatching_EvaluatesCorrectIntervals()
        {
            var ddc = new MonitorDdcService();
            using (var engine = new ProfileScheduleEngine(ddc, System.Windows.Threading.Dispatcher.CurrentDispatcher))
            {
                var cfg = new MonitorProfileConfig
                {
                    ActiveProfile = "Test"
                };
                cfg.Profiles["Test"] = new List<MonitorTimeSetting>
                {
                    new MonitorTimeSetting { Time = "07:00", Brightness = 70, Contrast = 80 },
                    new MonitorTimeSetting { Time = "18:00", Brightness = 50, Contrast = 70 },
                    new MonitorTimeSetting { Time = "23:00", Brightness = 30, Contrast = 60 }
                };

                engine.UpdateConfig(cfg);

                // 测试主动查询方法
                var active = engine.GetActiveSettingForProfile("Test");
                Assert.IsNotNull(active);
                Assert.IsTrue(active.Brightness >= 30 && active.Brightness <= 70);

                TimeSpan untilNext;
                var next = engine.GetNextSettingForProfile("Test", out untilNext);
                Assert.IsNotNull(next);
                Assert.IsTrue(untilNext.TotalSeconds > 0);
            }
        }

        [TestMethod]
        public void ProfileScheduleEngine_CrossMidnight_SelectsCorrectInterval()
        {
            var ddc = new MonitorDdcService();
            using (var engine = new ProfileScheduleEngine(ddc, System.Windows.Threading.Dispatcher.CurrentDispatcher))
            {
                var cfg = new MonitorProfileConfig
                {
                    ActiveProfile = "Daily"
                };
                cfg.Profiles["Daily"] = new List<MonitorTimeSetting>
                {
                    new MonitorTimeSetting { Time = "07:00", Brightness = 65, Contrast = 70 },
                    new MonitorTimeSetting { Time = "18:00", Brightness = 50, Contrast = 65 },
                    new MonitorTimeSetting { Time = "22:30", Brightness = 35, Contrast = 55 }
                };
                engine.UpdateConfig(cfg);

                // 跨午夜与早于第一档测试（应回退到前一天最后一段 22:30 档，即 35）
                Assert.AreEqual(35, engine.GetActiveSettingForProfile("Daily", new TimeSpan(0, 0, 0)).Brightness);
                Assert.AreEqual(35, engine.GetActiveSettingForProfile("Daily", new TimeSpan(6, 0, 0)).Brightness);
                Assert.AreEqual(35, engine.GetActiveSettingForProfile("Daily", new TimeSpan(6, 59, 59)).Brightness);

                // 日间第一档生效
                Assert.AreEqual(65, engine.GetActiveSettingForProfile("Daily", new TimeSpan(7, 0, 0)).Brightness);
                Assert.AreEqual(65, engine.GetActiveSettingForProfile("Daily", new TimeSpan(8, 0, 0)).Brightness);

                // 傍晚档生效
                Assert.AreEqual(50, engine.GetActiveSettingForProfile("Daily", new TimeSpan(18, 0, 0)).Brightness);
                Assert.AreEqual(50, engine.GetActiveSettingForProfile("Daily", new TimeSpan(19, 0, 0)).Brightness);

                // 夜间档生效
                Assert.AreEqual(35, engine.GetActiveSettingForProfile("Daily", new TimeSpan(22, 30, 0)).Brightness);
                Assert.AreEqual(35, engine.GetActiveSettingForProfile("Daily", new TimeSpan(23, 0, 0)).Brightness);
            }
        }

        [TestMethod]
        public void MonitorProfileModule_Follows_OneModuleOneSubmenu_Contract()
        {
            var module = new MonitorProfileModule();
            Assert.AreEqual("MonitorProfile", module.Id);
            Assert.AreEqual(30, module.Order);
            Assert.IsTrue(module.DefaultEnabled);

            // 托盘菜单必须遵循 CarroDesk 的二级子菜单规范（单一 root 项）
            var items = module.GetTrayMenuItems().ToList();
            Assert.AreEqual(1, items.Count, "MonitorProfileModule 必须只输出单一根节点以避免撑高主托盘菜单");

            var root = items[0];
            Assert.AreEqual("monitorprofile_root", root.Id);
            Assert.IsTrue(root.Children.Count >= 5, "二级菜单应包含模式选择、常用档位、自动计划开关与设置入口");

            // 验证二级菜单中包含设置入口
            Assert.IsTrue(root.Children.Any(c => c.Id == "monitor_settings_window"));
            Assert.IsTrue(root.Children.Any(c => c.Id == "monitor_auto_schedule"));
            Assert.IsTrue(root.Children.Any(c => c.Id == "monitor_refresh_now"));
        }

        [TestMethod]
        public void ConfigManager_MonitorProfile_Json_RoundTrip()
        {
            var configService = new ConfigService();
            var configMgr = new ConfigManager(configService);

            var initial = configMgr.GetModuleConfig<MonitorProfileConfig>("MonitorProfile");
            Assert.IsNotNull(initial);
            Assert.IsTrue(initial.Profiles.Count > 0);

            // 修改配置并保存
            initial.ActiveProfile = "Night";
            initial.BrightnessStep = 10;
            initial.AutoSchedule = false;
            initial.Profiles["CustomMode"] = new List<MonitorTimeSetting>
            {
                new MonitorTimeSetting { Time = "10:00", Brightness = 85, Contrast = 90 }
            };

            configMgr.SaveModuleConfig("MonitorProfile", initial);

            // 从底层 JSON 验证并重新反序列化
            Assert.IsFalse(string.IsNullOrEmpty(configService.MonitorProfileJson));
            var reloaded = configMgr.GetModuleConfig<MonitorProfileConfig>("MonitorProfile");

            Assert.AreEqual("Night", reloaded.ActiveProfile);
            Assert.AreEqual(10, reloaded.BrightnessStep);
            Assert.IsFalse(reloaded.AutoSchedule);
            Assert.IsTrue(reloaded.Profiles.ContainsKey("CustomMode"));
            Assert.AreEqual(85, reloaded.Profiles["CustomMode"][0].Brightness);
            Assert.AreEqual(90, reloaded.Profiles["CustomMode"][0].Contrast);
        }
    }
}
