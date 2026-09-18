using System;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.Awake.Models;
using CarroDesk.Modules.Awake.Services;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class AwakeModuleTests
    {
        [TestMethod]
        public void AwakeConfig_Defaults_AreValid()
        {
            var config = AwakeConfig.CreateDefault();
            Assert.IsTrue(config.Enabled);
            Assert.AreEqual(AwakeMode.Passive, config.Mode);
            Assert.IsTrue(config.KeepDisplayOn);
            Assert.AreEqual(30, config.DefaultDurationMinutes);
            Assert.AreEqual("Win+Shift+W", config.Hotkey);
            Assert.IsTrue(config.DisableOnBattery);
            Assert.AreEqual(20, config.BatteryThreshold);
            Assert.IsNotNull(config.CustomPresets);
            Assert.IsTrue(config.CustomPresets.Contains(30));
            Assert.IsNotNull(config.AutoAwakeProcesses);
        }

        [TestMethod]
        public void AwakeModule_Metadata_FollowsModuleContract()
        {
            var module = new AwakeModule();
            Assert.AreEqual("Awake", module.Id);
            Assert.AreEqual(15, module.Order);
            Assert.IsTrue(module.DefaultEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Description));
        }

        [TestMethod]
        public void AwakeModule_GetTrayMenuItems_StrictlyFollowsTwoLevelDesign()
        {
            var module = new AwakeModule();
            var items = module.GetTrayMenuItems()?.ToList();

            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AwakeModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("awake_root", root.Id);
            Assert.IsTrue(root.Header.Contains("保持唤醒"));

            // 检查核心二级子菜单结构
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_passive"), "应包含关闭选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_indefinite"), "应包含无限期保持唤醒选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_timed_root"), "应包含定时保持唤醒子菜单");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_until_root"), "应包含保持至指定时刻子菜单");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_keep_display"), "应包含保持屏幕常亮选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_settings"), "应包含设置选项");

            // 检查定时子菜单预设
            var timedNode = root.Children.First(c => c.Id == "awake_mode_timed_root");
            Assert.IsTrue(timedNode.Children.Count >= 4, "定时子菜单应包含多个预设时长");
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_15"));
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_30"));
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_60"));
        }

        [TestMethod]
        public void AwakeService_ModeTransitions_WorkAsExpected()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var service = new AwakeService(dispatcher, null);
            service.Initialize(AwakeConfig.CreateDefault());

            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsActive);

            // 切换为无限期
            service.SetIndefinite();
            Assert.AreEqual(AwakeMode.Indefinite, service.Mode);
            Assert.IsTrue(service.IsActive);
            Assert.AreEqual(TimeSpan.Zero, service.RemainingTime);

            // 切换为定时
            service.SetTimed(45);
            Assert.AreEqual(AwakeMode.Timed, service.Mode);
            Assert.IsTrue(service.IsActive);
            Assert.IsTrue(service.RemainingTime.TotalMinutes > 44 && service.RemainingTime.TotalMinutes <= 45);

            // 切换为屏幕开关
            service.SetKeepDisplayOn(false);
            Assert.IsFalse(service.KeepDisplayOn);
            service.ToggleKeepDisplayOn();
            Assert.IsTrue(service.KeepDisplayOn);

            // 切换为关闭
            service.SetPassive();
            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsActive);

            service.Dispose();
        }

        [TestMethod]
        public void ConfigManager_AwakeConfig_SerializationRoundTrip()
        {
            var configService = new ConfigService();
            var manager = new ConfigManager(configService);

            var originalConfig = new AwakeConfig
            {
                KeepDisplayOn = false,
                DefaultDurationMinutes = 60,
                Hotkey = "Ctrl+Shift+F9",
                DisableOnBattery = true,
                BatteryThreshold = 30,
                AutoAwakeProcesses = new System.Collections.Generic.List<string> { "blender", "ffmpeg" }
            };

            manager.SaveModuleConfig("Awake", originalConfig);
            Assert.IsNotNull(configService.GetModuleToken("Awake"));

            var loadedConfig = manager.GetModuleConfig<AwakeConfig>("Awake");
            Assert.IsNotNull(loadedConfig);
            Assert.AreEqual(false, loadedConfig.KeepDisplayOn);
            Assert.AreEqual(60, loadedConfig.DefaultDurationMinutes);
            Assert.AreEqual("Ctrl+Shift+F9", loadedConfig.Hotkey);
            Assert.AreEqual(30, loadedConfig.BatteryThreshold);
            Assert.AreEqual(2, loadedConfig.AutoAwakeProcesses.Count);
            Assert.AreEqual("blender", loadedConfig.AutoAwakeProcesses[0]);
            Assert.AreEqual("ffmpeg", loadedConfig.AutoAwakeProcesses[1]);
        }
    }
}
