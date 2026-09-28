using System;
using System.IO;
using System.Text;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Ipc;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S4 验收（IPC 设计 §5.5/§8）：MCP stdio 会话——握手、工具发现、工具调用（含口令约定）、
    /// 错误映射；经真实管道服务端到端验证。
    /// </summary>
    [TestClass]
    public class McpStdioTests
    {
        private sealed class FakePinService : IPinService
        {
            public bool IsConfigured { get { return true; } }
            public string Salt { get { return "salt"; } }
            public string Hash { get { return "hash"; } }
            public bool Verify(string pin) { return pin == "1234"; }
            public void SetNewPin(string pin) { }
            public void SetFromConfig(string salt, string hash) { }
        }

        private static NamedPipeCommandServer StartTestServer(out string pipeName)
        {
            var registry = new CommandRegistry();
            registry.Register("test", new CommandDescriptor
            {
                Name = "test.echo",
                Summary = "回显文本",
                Risk = CommandRisk.ReadOnly,
                Params = new CommandParam[]
                {
                    new CommandParam { Name = "text", Type = "string", Required = true, Description = "要回显的文本" }
                },
                Handler = r => CommandResult.Success("pong:" + r.Params["text"])
            });
            registry.Register("test", new CommandDescriptor
            {
                Name = "test.secret",
                Summary = "需要口令",
                Risk = CommandRisk.Low,
                RequiresPin = true,
                Handler = r => CommandResult.Success("granted")
            });
            HostCommands.Install(registry, null);

            var host = new CommandHost(
                registry,
                id => ModuleStatus.Running,
                new PinGuard(new FakePinService()),
                new NullCommandAuditSink());
            pipeName = "CarroDesk.mcp-test." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeCommandServer(host, pipeName, null, useAcl: false);
            server.Start();
            return server;
        }

        private static JArray RunSession(string pipeName, params string[] requestLines)
        {
            var output = new StringWriter();
            var inputLines = string.Join(Environment.NewLine, requestLines) + Environment.NewLine;
            var exit = McpStdioServer.Run(
                new StringReader(inputLines),
                output,
                new StringWriter(),
                () => new PipeRpcClient(pipeName));
            Assert.AreEqual(0, exit);
            var result = new JArray();
            foreach (var line in output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(JToken.Parse(line));
            }
            return result;
        }

        private static JToken FindResponse(JArray responses, long id)
        {
            foreach (var response in responses)
            {
                if (response["id"] != null && response["id"].Type == JTokenType.Integer && (long)response["id"] == id)
                    return response;
            }
            return null;
        }

        [TestMethod]
        public void Run_Handshake_ToolsAndCalls_EndToEnd()
        {
            string pipeName;
            var server = StartTestServer(out pipeName);
            try
            {
                var responses = RunSession(
                    pipeName,
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"test.echo\",\"arguments\":{\"text\":\"hi\"}}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"nope.nope\",\"arguments\":{}}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"test.secret\",\"arguments\":{}}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"test.secret\",\"arguments\":{\"pin\":\"1234\"}}}",
                    "not json at all",
                    "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"resources/list\"}");

                // 1. initialize：回显协议版本 + serverInfo
                var init = FindResponse(responses, 1);
                Assert.IsNotNull(init, "initialize 应有响应");
                Assert.IsNull(init["error"]);
                Assert.AreEqual("2025-06-18", (string)init["result"]["protocolVersion"]);
                Assert.AreEqual("carrodesk", (string)init["result"]["serverInfo"]["name"]);
                Assert.IsNotNull(init["result"]["capabilities"]["tools"]);

                // 2. 通知无响应；总响应数 = 9 行请求 - 1 通知 - 0 静默 = 8
                Assert.AreEqual(8, responses.Count);

                // 3. tools/list：能力表映射为工具 schema
                var toolsList = FindResponse(responses, 2);
                var tools = (JArray)toolsList["result"]["tools"];
                Assert.IsNotNull(tools);
                var echo = FindTool(tools, "test.echo");
                Assert.AreEqual("回显文本", (string)echo["description"]);
                Assert.AreEqual("string", (string)echo["inputSchema"]["properties"]["text"]["type"]);
                Assert.AreEqual("要回显的文本", (string)echo["inputSchema"]["properties"]["text"]["description"]);
                Assert.AreEqual("text", (string)echo["inputSchema"]["required"][0]);
                var secret = FindTool(tools, "test.secret");
                Assert.IsTrue(((string)secret["description"]).Contains("pin"), "需口令的工具描述必须提示 pin 约定");

                // 4. tools/call 成功：字符串结果原样文本化
                var call = FindResponse(responses, 3);
                Assert.AreEqual("pong:hi", (string)call["result"]["content"][0]["text"]);
                Assert.AreEqual(false, (bool)call["result"]["isError"]);

                // 5. 未知工具 → isError 携带服务端错误
                var unknown = FindResponse(responses, 4);
                Assert.AreEqual(true, (bool)unknown["result"]["isError"]);
                Assert.IsTrue(((string)unknown["result"]["content"][0]["text"]).Contains("-32601"));

                // 6. 需口令工具：缺口令失败 / 对口令成功
                var noPin = FindResponse(responses, 5);
                Assert.AreEqual(true, (bool)noPin["result"]["isError"]);
                Assert.IsTrue(((string)noPin["result"]["content"][0]["text"]).Contains("-32002"));
                var withPin = FindResponse(responses, 6);
                Assert.AreEqual("granted", (string)withPin["result"]["content"][0]["text"]);
                Assert.AreEqual(false, (bool)withPin["result"]["isError"]);

                // 7. 垃圾行 → id null 的 -32700
                var parseFail = FindParseErrorResponse(responses);
                Assert.IsNotNull(parseFail);
                Assert.AreEqual(-32700, (int)parseFail["error"]["code"]);

                // 8. 未知方法 → -32601
                var unknownMethod = FindResponse(responses, 8);
                Assert.AreEqual(-32601, (int)unknownMethod["error"]["code"]);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void Run_EmptyInput_ExitsCleanly()
        {
            var output = new StringWriter();
            var exit = McpStdioServer.Run(new StringReader(""), output, new StringWriter(), () => new PipeRpcClient());
            Assert.AreEqual(0, exit);
            Assert.AreEqual("", output.ToString());
        }

        [TestMethod]
        public void Run_MissingToolName_ReturnsInvalidParams()
        {
            string pipeName;
            var server = StartTestServer(out pipeName);
            try
            {
                var responses = RunSession(
                    pipeName,
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{}}");
                var response = FindResponse(responses, 1);
                Assert.IsNotNull(response);
                Assert.AreEqual(-32602, (int)response["error"]["code"]);
            }
            finally { server.Dispose(); }
        }

        private static JToken FindTool(JArray tools, string name)
        {
            foreach (var tool in tools)
            {
                if ((string)tool["name"] == name) return tool;
            }
            Assert.Fail("tool not found: " + name);
            return null;
        }

        private static JToken FindParseErrorResponse(JArray responses)
        {
            foreach (var response in responses)
            {
                if (response["error"] != null && (int)response["error"]["code"] == -32700) return response;
            }
            return null;
        }
    }
}
