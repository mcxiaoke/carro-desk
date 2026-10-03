using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Modules.ScreenLock.Services;

namespace CarroDesk.Tests
{
    [TestClass]
    public class DevicePresenceAndAutoLockTests
    {
        [TestMethod]
        public void ScreenLockConfig_DefaultsAndClone_WorkCorrectly()
        {
            var config = new ScreenLockConfig();
            Assert.IsTrue(config.AutoLockEnabled, "默认应启用自动锁屏");
            Assert.IsFalse(config.DevicePresenceEnabled, "默认不启用设备在线免锁");
            Assert.AreEqual("", config.TargetDeviceIP, "默认目标 IP 为空");
            Assert.AreEqual(30, config.DeviceOfflineGraceSeconds, "默认离线缓冲为 30 秒");

            // 修改后 Clone
            config.AutoLockEnabled = false;
            config.DevicePresenceEnabled = true;
            config.TargetDeviceIP = "192.168.1.88";
            config.DeviceOfflineGraceSeconds = 60;
            config.ExcludeProcesses.Add("game.exe");

            var cloned = config.Clone();
            Assert.IsFalse(cloned.AutoLockEnabled);
            Assert.IsTrue(cloned.DevicePresenceEnabled);
            Assert.AreEqual("192.168.1.88", cloned.TargetDeviceIP);
            Assert.AreEqual(60, cloned.DeviceOfflineGraceSeconds);
            Assert.AreEqual(1, cloned.ExcludeProcesses.Count);
            Assert.AreEqual("game.exe", cloned.ExcludeProcesses[0]);
        }

        [TestMethod]
        public void IpPresenceDetector_InvalidIp_ReturnsFalseGracefully()
        {
            var detector = new IpPresenceDetector();
            Assert.IsFalse(detector.IsPresentWithGrace(null, 30));
            Assert.IsFalse(detector.IsPresentWithGrace("", 30));
            Assert.IsFalse(detector.IsPresentWithGrace("   ", 30));
            Assert.IsFalse(detector.IsPresentWithGrace("999.999.999.999", 30));
            Assert.IsFalse(detector.IsPresentWithGrace("abc.def", 30));
        }

        [TestMethod]
        public void IpPresenceDetector_ProbeAsync_HandlesLoopbackOrInvalid()
        {
            // 探测非法 IP 应返回 false
            var task = IpPresenceDetector.ProbeAsync("invalid.ip");
            Assert.IsTrue(task.Wait(2000), "异步探测应在超时内返回");
            Assert.IsFalse(task.Result);

            // 探测本地回环 127.0.0.1（本机一般可 ping 通）
            var loopbackTask = IpPresenceDetector.ProbeAsync("127.0.0.1");
            Assert.IsTrue(loopbackTask.Wait(3000), "探测 127.0.0.1 应及时完成");
            // 不做结果断言：某些环境禁用 ICMP 会返回 false，属正常；
            // 关键是不抛异常且能在超时内返回（Wait + 访问 Result 已覆盖）。
            bool _ = loopbackTask.Result;
        }

        [TestMethod]
        public void ScreenLockModule_TrayMenu_ContainsExtendedPausePresetsAndAutoLockToggle()
        {
            var module = new ScreenLockModule();
            // 模块在未 Initialize 时构建菜单应优雅降级
            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count);

            var root = items[0];
            Assert.IsNotNull(root);

            // 检查空闲锁定子菜单
            var idleMenu = root.Children.FirstOrDefault(c => c.Id == "screenlock_idle_root");
            Assert.IsNotNull(idleMenu, "应包含空闲锁定二级菜单");
            var autoLockToggle = idleMenu.Children.FirstOrDefault(c => c.Id == "screenlock_idle_enable_toggle");
            Assert.IsNotNull(autoLockToggle, "空闲锁定菜单应包含启用自动锁定开关");

            // 检查暂停计时子菜单
            var pauseMenu = root.Children.FirstOrDefault(c => c.Id == "screenlock_pause_root");
            Assert.IsNotNull(pauseMenu, "应包含暂停计时二级菜单");

            // 验证 30m, 1h, 2h, 4h, 8h 项均存在
            var p30 = pauseMenu.Children.FirstOrDefault(c => c.Id == "screenlock_pause_30m");
            var p1h = pauseMenu.Children.FirstOrDefault(c => c.Id == "screenlock_pause_1h");
            var p2h = pauseMenu.Children.FirstOrDefault(c => c.Id == "screenlock_pause_2h");
            var p4h = pauseMenu.Children.FirstOrDefault(c => c.Id == "screenlock_pause_4h");
            var p8h = pauseMenu.Children.FirstOrDefault(c => c.Id == "screenlock_pause_8h");

            Assert.IsNotNull(p30, "应包含 30分钟 暂停项");
            Assert.IsNotNull(p1h, "应包含 1小时 暂停项");
            Assert.IsNotNull(p2h, "应包含 2小时 暂停项");
            Assert.IsNotNull(p4h, "应包含 4小时 暂停项");
            Assert.IsNotNull(p8h, "应包含 8小时 暂停项");
        }
    }
}
