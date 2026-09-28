using System;
using CarroDesk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>S3 验收：`ctl` 命令行解析（主 exe 二实例转发与 CarroDesk.Cli 共用）。</summary>
    [TestClass]
    public class ControlArgsTests
    {
        [TestMethod]
        public void Parse_Minimal()
        {
            ControlArgs parsed;
            string error;
            Assert.IsTrue(ControlArgs.TryParse(new[] { "ctl", "host.status" }, out parsed, out error), error);
            Assert.AreEqual("host.status", parsed.Method);
            Assert.AreEqual("cli", parsed.Source);
            Assert.IsNull(parsed.Pin);
            Assert.IsNull(parsed.PipeName);
            Assert.IsFalse(parsed.Json);
            Assert.AreEqual(0, parsed.TimeoutMs);
            Assert.AreEqual(0, parsed.Params.Count);
        }

        [TestMethod]
        public void Parse_FullWithOptionsAndParams()
        {
            ControlArgs parsed;
            string error;
            var ok = ControlArgs.TryParse(new[]
            {
                "ctl", "services.start", "--name", "UUService", "--pin", "1234",
                "--json", "--pipe", "custom-pipe", "--timeout", "8000",
                "--force", "--retries", "3"
            }, out parsed, out error);
            Assert.IsTrue(ok, error);

            Assert.AreEqual("services.start", parsed.Method);
            Assert.AreEqual("1234", parsed.Pin);
            Assert.AreEqual("custom-pipe", parsed.PipeName);
            Assert.AreEqual(8000, parsed.TimeoutMs);
            Assert.IsTrue(parsed.Json);
            Assert.AreEqual("UUService", parsed.Params["name"]);
            Assert.AreEqual(true, parsed.Params["force"]);
            Assert.AreEqual("3", parsed.Params["retries"]);
        }

        [TestMethod]
        public void Parse_MissingMethod_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl" }, out parsed, out error));
            Assert.IsNotNull(error);
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "--json" }, out parsed, out error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Parse_UnexpectedPositional_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "host.status", "stray" }, out parsed, out error));
            Assert.IsTrue(error.Contains("unexpected argument"));
        }

        [TestMethod]
        public void Parse_MissingOptionValue_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "services.start", "--pin" }, out parsed, out error));
            Assert.IsTrue(error.Contains("--pin"));
        }

        [TestMethod]
        public void Parse_BadTimeout_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "host.status", "--timeout", "abc" }, out parsed, out error));
            Assert.IsTrue(error.Contains("--timeout"));
        }

        [TestMethod]
        public void Parse_NegativeNumberValue_IsTreatedAsValue()
        {
            ControlArgs parsed;
            string error;
            Assert.IsTrue(ControlArgs.TryParse(new[] { "ctl", "test.add", "--delta", "-5" }, out parsed, out error), error);
            Assert.AreEqual("-5", parsed.Params["delta"]);
        }

        [TestMethod]
        public void IsControlInvocation()
        {
            Assert.IsFalse(ControlArgs.IsControlInvocation(null));
            Assert.IsFalse(ControlArgs.IsControlInvocation(new string[0]));
            Assert.IsFalse(ControlArgs.IsControlInvocation(new[] { "--mcp" }));
            Assert.IsFalse(ControlArgs.IsControlInvocation(new[] { "somethingelse" }));
            Assert.IsTrue(ControlArgs.IsControlInvocation(new[] { "CTL", "host.status" }));
        }
    }
}
