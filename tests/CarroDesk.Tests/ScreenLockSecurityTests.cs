using System;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Modules.ScreenLock.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 锁屏安全边界回归测试（R1 / R2）。
    ///
    /// 背景：锁屏把"忘记 PIN"的风险转移给用户，因此不能再留任何无需凭据的解除路径。
    /// 两个真实缺陷：
    ///   R1 锁定期间托盘菜单照常可用 → 用户可经托盘打开锁屏设置 → 关掉"启用"并保存
    ///      → OnConfigReloaded 执行 Controller.Unlock()，全程不需要 PIN；
    ///   R2 UnlockOnResume 默认 true → 任何一次 Windows 会话解锁都免 PIN 解除 CarroDesk 锁屏。
    ///
    /// 注意：这里**不**真实触发锁定——LockController 会安装全局低级键盘钩子并创建覆盖
    /// 真实显示器的置顶窗口，自动化测试环境绝不能走到那条路径。因此改为对
    /// "锁定态判定所依赖的两个接缝"做断言（配置默认值 + 设置窗口的锁定守卫），
    /// 真正的锁定行为需实机回归。
    /// </summary>
    [TestClass]
    public class ScreenLockSecurityTests
    {
        /// <summary>
        /// R2：UnlockOnResume 默认必须为 false。
        /// 该路径不校验 PIN，默认开启等于"设了 PIN 也能被任何一次会话解锁绕过"。
        /// </summary>
        [TestMethod]
        public void ScreenLockConfig_UnlockOnResume_DefaultsToFalse()
        {
            Assert.IsFalse(new ScreenLockConfig().UnlockOnResume,
                "UnlockOnResume 不校验 PIN，默认值必须是 false，否则用户以为设了 PIN 就安全，实际任何会话解锁都能解除锁屏。");
        }

        /// <summary>R2：Clone 必须保留该开关（防止克隆把用户显式设置回默认值）。</summary>
        [TestMethod]
        public void ScreenLockConfig_Clone_PreservesUnlockOnResume()
        {
            var cfg = new ScreenLockConfig { UnlockOnResume = true };
            Assert.IsTrue(cfg.Clone().UnlockOnResume);
            Assert.IsFalse(new ScreenLockConfig().Clone().UnlockOnResume);
        }

        /// <summary>
        /// R1：设置窗口必须按模块注入的锁定态拒绝保存（否则"关掉启用"可免 PIN 解锁）。
        /// </summary>
        [TestMethod]
        public void ScreenLockSettingsWindow_TracksInjectedLockState()
        {
            TestEnvironment.RunInSta(() =>
            {
                bool locked = true;
                var win = new ScreenLockSettingsWindow(null, null, () => locked);

                Assert.IsTrue(win.IsLockedNow(), "注入的锁定态为 true 时，窗口必须判定为已锁定。");

                locked = false;
                Assert.IsFalse(win.IsLockedNow(), "解锁后必须解除拦截。");

                // 守卫自身抛异常时按"未锁定"处理，不能因探测失败把窗口锁死
                var throwing = new ScreenLockSettingsWindow(null, null, () => throw new InvalidOperationException("probe"));
                Assert.IsFalse(throwing.IsLockedNow());

                // 未注入探测函数（单元测试/宿主直接构造）时保持旧行为
                Assert.IsFalse(new ScreenLockSettingsWindow().IsLockedNow());
            });
        }

        /// <summary>
        /// R1 守卫的模块侧接线：未锁定时托盘菜单必须照常输出（修复不能把菜单整体弄丢）。
        /// 已锁定分支需要真实锁定环境，不在此覆盖（见类注释）。
        /// </summary>
        [TestMethod]
        public void ScreenLockModule_TrayMenu_StillAvailableWhenNotLocked()
        {
            var module = new ScreenLockModule();
            var items = System.Linq.Enumerable.ToList(module.GetTrayMenuItems());
            Assert.AreEqual(1, items.Count, "未锁定时 ScreenLockModule 应仍输出唯一的根菜单项。");
            Assert.AreEqual("screenlock_root", items[0].Id);
        }
    }
}