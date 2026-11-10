using System;
using System.IO;
using CarroDesk;
using CarroDesk.Host.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

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
        public void Parse_InlineValues_MatchSpaceSeparatedForm()
        {
            ControlArgs parsed;
            string error;
            var ok = ControlArgs.TryParse(new[]
            {
                "ctl", "awake.on", "--minutes=10", "--pin=1234", "--timeout=8000",
                "--name=a=b", "--force"
            }, out parsed, out error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual("awake.on", parsed.Method);
            Assert.AreEqual("10", parsed.Params["minutes"]);
            Assert.AreEqual("1234", parsed.Pin);
            Assert.AreEqual(8000, parsed.TimeoutMs);
            Assert.AreEqual("a=b", parsed.Params["name"], "只按第一个 '=' 拆分，值中允许再出现 '='");
            Assert.AreEqual(true, parsed.Params["force"]);
        }

        [TestMethod]
        public void Parse_InlineEmptyValue_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "awake.on", "--minutes=" }, out parsed, out error));
            Assert.IsTrue(error.Contains("--minutes"));
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "awake.on", "--pin=" }, out parsed, out error));
            Assert.IsTrue(error.Contains("--pin"));
        }

        [TestMethod]
        public void Parse_InlineValueOnBooleanFlag_Fails()
        {
            ControlArgs parsed;
            string error;
            Assert.IsFalse(ControlArgs.TryParse(new[] { "ctl", "host.status", "--json=1" }, out parsed, out error));
            Assert.IsTrue(error.Contains("--json"));
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

        [TestMethod]
        public void Forward_JsonFailure_EmitsStructuredErrorObject()
        {
            var args = new ControlArgs { Method = "host.status", Json = true };
            var client = new PipeRpcClient("CarroDesk.no-such-pipe." + Guid.NewGuid().ToString("N"), 200);
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exit = ControlForwarder.Forward(args, client, stdout, stderr);

            Assert.AreEqual(3, exit);
            Assert.AreEqual("", stdout.ToString().Trim(), "--json 失败时 stdout 应为空");
            var obj = JObject.Parse(stderr.ToString().Trim());
            Assert.AreEqual(false, (bool)obj["ok"]);
            Assert.AreEqual(-32020, (int)obj["code"]);
            Assert.IsFalse(string.IsNullOrEmpty((string)obj["message"]));
        }
    }
}
