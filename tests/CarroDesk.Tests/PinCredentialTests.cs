using System;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S1/S2（docs/CODE-REVIEW-20261025.md）回归：PIN 凭据链路。
    /// </summary>
    [TestClass]
    public class PinCredentialTests
    {
        // ===== S1：修改 PIN 后 pending 凭据必须让位于磁盘配置 =====

        private static ConfigService NewConfig(out AppSettings settings)
        {
            var config = new ConfigService();
            config.LoadOrCreate();
            settings = config.Current;
            return config;
        }

        [TestMethod]
        public void HostPinService_PendingMatchesConfig_VerifyUsesPending()
        {
            AppSettings settings;
            var config = NewConfig(out settings);
            var host = new HostPinService(config);

            // 模拟首次运行向导：SetNewPin → 写入配置 → 落盘
            host.SetNewPin("1111");
            settings.PinSalt = host.Salt;
            settings.PinHash = host.Hash;
            config.Save();

            Assert.IsTrue(host.Verify("1111"), "pending 与配置一致时正确 PIN 必须通过");
            Assert.IsFalse(host.Verify("2222"));
        }

        [TestMethod]
        public void HostPinService_ConfigChangedExternally_NewPinVerified_OldPinRejected()
        {
            AppSettings settings;
            var config = NewConfig(out settings);
            var host = new HostPinService(config);

            // 首次运行向导：PIN A（pending 置位并落盘）
            host.SetNewPin("1111");
            settings.PinSalt = host.Salt;
            settings.PinHash = host.Hash;
            config.Save();

            // 配置编辑器修改 PIN 为 B（绕过 HostPinService，直接写配置并落盘）
            var salt = Convert.ToBase64String(PinService.GenerateSalt());
            settings.PinSalt = salt;
            settings.PinHash = PinService.ComputeHash(Convert.FromBase64String(salt), "2222");
            config.Save();

            Assert.IsTrue(host.Verify("2222"),
                "配置已更换为新 PIN：新 PIN 必须立即生效（修复前 pending 旧哈希导致失败）");
            Assert.IsFalse(host.Verify("1111"),
                "配置已更换为新 PIN：旧 PIN 必须立即失效（修复前 pending 旧哈希导致继续有效）");
        }

        // ===== S2：PIN 设置必须限定纯数字（锁屏键盘钩子只放行数字） =====

        [TestMethod]
        public void FirstRunWindow_ValidatePin_AcceptsNumericOnly()
        {
            string errorKey;
            Assert.IsTrue(FirstRunWindow.ValidatePin("1234", out errorKey));
            Assert.IsNull(errorKey);
            Assert.IsTrue(FirstRunWindow.ValidatePin("009999", out errorKey));
            Assert.IsNull(errorKey);
        }

        [TestMethod]
        public void FirstRunWindow_ValidatePin_RejectsShortOrNonNumeric()
        {
            string errorKey;
            Assert.IsFalse(FirstRunWindow.ValidatePin("123", out errorKey));
            Assert.AreEqual("FirstRun.ErrorMinLength", errorKey);

            Assert.IsFalse(FirstRunWindow.ValidatePin("", out errorKey));
            Assert.AreEqual("FirstRun.ErrorMinLength", errorKey);

            Assert.IsFalse(FirstRunWindow.ValidatePin(null, out errorKey));
            Assert.AreEqual("FirstRun.ErrorMinLength", errorKey);

            Assert.IsFalse(FirstRunWindow.ValidatePin("abcd1234", out errorKey));
            Assert.AreEqual("FirstRun.ErrorDigitsOnly", errorKey);

            Assert.IsFalse(FirstRunWindow.ValidatePin("12 45", out errorKey));
            Assert.AreEqual("FirstRun.ErrorDigitsOnly", errorKey);

            Assert.IsFalse(FirstRunWindow.ValidatePin("1234.5", out errorKey));
            Assert.AreEqual("FirstRun.ErrorDigitsOnly", errorKey);
        }

        [TestMethod]
        public void LocalePacks_ContainDigitsOnlyErrorKey()
        {
            // 两个语言包都必须提供新 key，否则界面会显示错误键名本身
            var message = CarroDesk.Services.Localization.I18nService.Instance.Get("FirstRun.ErrorDigitsOnly");
            Assert.AreNotEqual("FirstRun.ErrorDigitsOnly", message,
                "语言包缺少 FirstRun.ErrorDigitsOnly，回退机制会显示键名");
            Assert.IsFalse(string.IsNullOrEmpty(message));
        }
    }
}
