using System;
using System.Security.Cryptography;
using System.Text;
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
    }
}
