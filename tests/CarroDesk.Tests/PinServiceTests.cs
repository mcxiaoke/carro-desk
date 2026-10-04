using System;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Models;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class PinServiceTests
    {
        [TestMethod]
        public void SetNewPin_GeneratesPbkdf2Hash_And_VerifiesSuccessfully()
        {
            var pinService = new PinService();
            Assert.IsFalse(pinService.IsConfigured);

            pinService.SetNewPin("123456");

            Assert.IsTrue(pinService.IsConfigured);
            Assert.IsFalse(string.IsNullOrEmpty(pinService.Salt));
            Assert.IsFalse(string.IsNullOrEmpty(pinService.Hash));
            Assert.IsTrue(pinService.Hash.StartsWith("pbkdf2$100000$"));
            Assert.IsFalse(pinService.JustUpgraded);

            // Correct PIN
            Assert.IsTrue(pinService.Verify("123456"));
            Assert.IsFalse(pinService.JustUpgraded);

            // Wrong PIN
            Assert.IsFalse(pinService.Verify("654321"));
            Assert.IsFalse(pinService.Verify(""));
            Assert.IsFalse(pinService.Verify(null));
        }

        [TestMethod]
        public void Verify_UnconfiguredService_ReturnsFalse()
        {
            var pinService = new PinService();
            Assert.IsFalse(pinService.Verify("123456"));

            pinService.SetFromConfig("invalid_salt", "some_hash");
            Assert.IsFalse(pinService.Verify("123456"));
        }

        [TestMethod]
        public void Verify_LegacySha256Hash_TransparentlyUpgradesToPbkdf2()
        {
            var pinService = new PinService();
            var saltBytes = PinService.GenerateSalt();
            var saltBase64 = Convert.ToBase64String(saltBytes);
            string rawPin = "MySecurePin987";

            // Compute legacy single-round SHA256
            string legacyHash;
            using (var sha = SHA256.Create())
            {
                var input = Encoding.UTF8.GetBytes(saltBase64 + ":" + rawPin);
                legacyHash = Convert.ToBase64String(sha.ComputeHash(input));
            }

            pinService.SetFromConfig(saltBase64, legacyHash);
            Assert.IsTrue(pinService.IsConfigured);
            Assert.IsFalse(pinService.JustUpgraded);
            Assert.AreEqual(legacyHash, pinService.Hash);

            // First verify with legacy hash: should succeed and trigger upgrade
            bool verifyResult = pinService.Verify(rawPin);
            Assert.IsTrue(verifyResult);
            Assert.IsTrue(pinService.JustUpgraded);
            Assert.AreNotEqual(legacyHash, pinService.Hash);
            Assert.IsTrue(pinService.Hash.StartsWith("pbkdf2$100000$"));

            // Clear upgrade flag and verify again
            pinService.ClearUpgraded();
            Assert.IsFalse(pinService.JustUpgraded);

            // Subsequent verify uses PBKDF2 directly
            Assert.IsTrue(pinService.Verify(rawPin));
            Assert.IsFalse(pinService.JustUpgraded);
        }

        [TestMethod]
        public void ComputeHash_WithSameSaltAndPin_ProducesDeterministicHash()
        {
            byte[] salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            string hash1 = PinService.ComputeHash(salt, "secret");
            string hash2 = PinService.ComputeHash(salt, "secret");
            string hash3 = PinService.ComputeHash(salt, "different");

            Assert.AreEqual(hash1, hash2);
            Assert.AreNotEqual(hash1, hash3);
        }

        [TestMethod]
        public void AppSettings_HasPin_RejectsMalformedCredentialAndAcceptsValidOne()
        {
            var malformed = new AppSettings { PinSalt = "not-base64", PinHash = "broken" };
            Assert.IsFalse(malformed.HasPin(), "损坏 PIN 必须进入恢复流程，不能显示为已配置");

            var service = new PinService();
            service.SetNewPin("1234");
            var valid = new AppSettings { PinSalt = service.Salt, PinHash = service.Hash };
            Assert.IsTrue(valid.HasPin());
        }

        [TestMethod]
        public void PinGuard_UnderMaxFreeAttempts_DoesNotBlock()
        {
            var pinService = new PinService();
            pinService.SetNewPin("8888");
            var guard = new PinGuard(pinService);

            // First 4 wrong attempts
            for (int i = 0; i < 4; i++)
            {
                TimeSpan remaining;
                var res = guard.Try("wrong", out remaining);
                Assert.AreEqual(PinAttemptResult.Wrong, res);
                Assert.AreEqual(TimeSpan.Zero, remaining);
                Assert.AreEqual(TimeSpan.Zero, guard.RemainingBlock());
            }

            // 5th wrong attempt triggers lockout (since fails >= 5)
            TimeSpan blockAfter5;
            var res5 = guard.Try("wrong", out blockAfter5);
            Assert.AreEqual(PinAttemptResult.Wrong, res5);
            Assert.IsTrue(blockAfter5.TotalSeconds > 0, "5th failure should trigger lockout");

            // 6th attempt should return Blocked immediately
            TimeSpan blockAfter6;
            var res6 = guard.Try("wrong", out blockAfter6);
            Assert.AreEqual(PinAttemptResult.Blocked, res6);
            Assert.IsTrue(blockAfter6.TotalSeconds > 0);
        }

        [TestMethod]
        public void PinGuard_CorrectPin_ReturnsSuccessAndResetsLockout()
        {
            var pinService = new PinService();
            pinService.SetNewPin("8888");
            var guard = new PinGuard(pinService);

            // Make 3 wrong attempts
            for (int i = 0; i < 3; i++)
            {
                TimeSpan block;
                guard.Try("wrong", out block);
            }

            // Correct attempt
            TimeSpan remaining;
            var res = guard.Try("8888", out remaining);
            Assert.AreEqual(PinAttemptResult.Success, res);
            Assert.AreEqual(TimeSpan.Zero, remaining);
            Assert.AreEqual(TimeSpan.Zero, guard.RemainingBlock());
        }

        [TestMethod]
        public void PinGuard_ResetAndReload_ClearsLockout()
        {
            var pinService = new PinService();
            pinService.SetNewPin("8888");
            var guard = new PinGuard(pinService);

            for (int i = 0; i < 5; i++)
            {
                TimeSpan block;
                guard.Try("wrong", out block);
            }

            Assert.IsTrue(guard.RemainingBlock().TotalSeconds > 0);

            guard.Reset();
            Assert.AreEqual(TimeSpan.Zero, guard.RemainingBlock());

            // Lockout again, then test Reload
            for (int i = 0; i < 5; i++)
            {
                TimeSpan block;
                guard.Try("wrong", out block);
            }
            Assert.IsTrue(guard.RemainingBlock().TotalSeconds > 0);

            guard.Reload(pinService);
            Assert.AreEqual(TimeSpan.Zero, guard.RemainingBlock());
        }

        // ===== R3：失败计数/封锁窗口必须跨进程重启存活 =====

        private static string NewGuardStatePath(string name)
        {
            return System.IO.Path.Combine(TestEnvironment.TempRoot, "pin-guard-" + name + ".json");
        }

        /// <summary>
        /// 只存内存时，"输错 4 次 → 重启宿主 → 计数归零 → 继续试" 可无限试探，限流形同虚设。
        /// 换一个 PinGuard 实例（等价于重启进程）后，失败计数与封锁窗口必须仍在。
        /// </summary>
        [TestMethod]
        public void PinGuard_FailuresSurviveGuardRecreation()
        {
            string statePath = NewGuardStatePath("survive");
            var pinService = new PinService();
            pinService.SetNewPin("8888");

            var first = new PinGuard(pinService, statePath);
            for (int i = 0; i < 5; i++)
            {
                TimeSpan block;
                first.Try("wrong", out block);
            }
            Assert.IsTrue(first.RemainingBlock() > TimeSpan.Zero, "5 次失败后应进入封锁期");
            Assert.IsTrue(System.IO.File.Exists(statePath), "限流状态必须落盘，否则重启即清零");

            // 等价于宿主重启：全新实例从磁盘读回
            var afterRestart = new PinGuard(pinService, statePath);
            Assert.IsTrue(afterRestart.RemainingBlock() > TimeSpan.Zero,
                "重启后封锁窗口必须仍在（否则限流可被绕过）");

            TimeSpan remaining;
            Assert.AreEqual(PinAttemptResult.Blocked, afterRestart.Try("8888", out remaining),
                "重启后即使 PIN 正确也必须先等封锁结束");

            // 正确 PIN 校验成功后计数复位，状态文件也应随之清除
            var third = new PinGuard(pinService, statePath);
            third.Reset();
            Assert.AreEqual(TimeSpan.Zero, third.RemainingBlock());
            Assert.IsFalse(System.IO.File.Exists(statePath), "复位后不应残留限流状态文件");
        }

        /// <summary>未显式传入状态路径时保持纯内存语义（单元测试不得污染用户数据目录）。</summary>
        [TestMethod]
        public void PinGuard_WithoutStatePath_DoesNotTouchDisk()
        {
            string defaultPath = PinGuard.DefaultStatePath;
            bool existedBefore = System.IO.File.Exists(defaultPath);
            long stampBefore = existedBefore ? System.IO.File.GetLastWriteTimeUtc(defaultPath).Ticks : 0;

            var pinService = new PinService();
            pinService.SetNewPin("8888");
            var guard = new PinGuard(pinService);
            for (int i = 0; i < 6; i++)
            {
                TimeSpan block;
                guard.Try("wrong", out block);
            }
            Assert.IsTrue(guard.RemainingBlock() > TimeSpan.Zero, "内存态限流本身必须仍然生效");

            Assert.AreEqual(existedBefore, System.IO.File.Exists(defaultPath),
                "未注入状态路径时不得创建默认状态文件");
            if (existedBefore)
            {
                Assert.AreEqual(stampBefore, System.IO.File.GetLastWriteTimeUtc(defaultPath).Ticks,
                    "未注入状态路径时不得改写默认状态文件");
            }
        }

        /// <summary>状态文件损坏不得让 PIN 校验链路整体不可用（降级为无历史失败）。</summary>
        [TestMethod]
        public void PinGuard_CorruptStateFile_DegradesGracefully()
        {
            string statePath = NewGuardStatePath("corrupt");
            System.IO.File.WriteAllText(statePath, "{ this is not json");

            var pinService = new PinService();
            pinService.SetNewPin("8888");
            var guard = new PinGuard(pinService, statePath);

            Assert.AreEqual(TimeSpan.Zero, guard.RemainingBlock(), "损坏状态文件应按无历史失败处理");

            TimeSpan remaining;
            Assert.AreEqual(PinAttemptResult.Success, guard.Try("8888", out remaining),
                "状态文件损坏不应影响正确 PIN 的校验");
        }
    }
}
