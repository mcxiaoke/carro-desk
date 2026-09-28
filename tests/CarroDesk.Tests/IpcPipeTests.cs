using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Ipc;
using CarroDesk.Models;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S2 传输层验收（IPC 设计 §8）：帧编解码、JSON-RPC 映射、配置节拷贝、
    /// 以及经真实命名管道的端到端调用（无 ACL，同用户回环）。
    /// </summary>
    [TestClass]
    public class IpcPipeTests
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

        // ---------- 帧编解码 ----------

        [TestMethod]
        public async Task FrameCodec_RoundTrip_AndSequentialFrames()
        {
            var payload1 = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}");
            var payload2 = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2}");

            var stream = new MemoryStream();
            var frame1 = FrameCodec.EncodeFrame(payload1);
            var frame2 = FrameCodec.EncodeFrame(payload2);
            stream.Write(frame1, 0, frame1.Length);
            stream.Write(frame2, 0, frame2.Length);
            stream.Position = 0;

            var read1 = await FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes);
            var read2 = await FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes);
            var read3 = await FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes);

            CollectionAssert.AreEqual(payload1, read1);
            CollectionAssert.AreEqual(payload2, read2);
            Assert.IsNull(read3, "帧间干净 EOF 应返回 null");
        }

        [TestMethod]
        public async Task FrameCodec_CleanEofBetweenFrames_ReturnsNull()
        {
            using (var stream = new MemoryStream())
            {
                var read = await FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes);
                Assert.IsNull(read);
            }
        }

        [TestMethod]
        public async Task FrameCodec_OversizeLength_ThrowsInvalidData()
        {
            var stream = new MemoryStream();
            stream.Write(BitConverter.GetBytes(FrameCodec.DefaultMaxFrameBytes + 1), 0, 4);
            stream.Write(new byte[16], 0, 16);
            stream.Position = 0;

            await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes));
        }

        [TestMethod]
        public async Task FrameCodec_MidFrameEof_ThrowsEndOfStream()
        {
            var stream = new MemoryStream();
            stream.Write(BitConverter.GetBytes(64), 0, 4);
            stream.Write(new byte[10], 0, 10); // 声明 64 字节只给 10 字节
            stream.Position = 0;

            await Assert.ThrowsExceptionAsync<EndOfStreamException>(
                () => FrameCodec.ReadFrameAsync(stream, FrameCodec.DefaultMaxFrameBytes));
        }

        // ---------- JSON-RPC 映射 ----------

        [TestMethod]
        public void RpcProtocol_Parse_ExtractsMethodParamsPinSourceAndId()
        {
            var frame = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                id = 42,
                method = "audio.output.set",
                @params = new { device = "speakers", volume = 3 },
                pin = "9999",
                source = "mcp"
            }));

            var call = RpcProtocol.ParseFrame(frame);

            Assert.IsFalse(call.IsParseError);
            Assert.AreEqual(42L, call.Id);
            Assert.AreEqual("audio.output.set", call.Request.Method);
            Assert.AreEqual("mcp", call.Request.Source);
            Assert.AreEqual("9999", call.Request.Pin);
            Assert.AreEqual("speakers", call.Request.Params["device"]);
            Assert.AreEqual(3L, call.Request.Params["volume"]);
        }

        [TestMethod]
        public void RpcProtocol_Parse_Garbage_ReturnsParseError()
        {
            var call = RpcProtocol.ParseFrame(Encoding.UTF8.GetBytes("this is not json {"));
            Assert.IsTrue(call.IsParseError);

            var call2 = RpcProtocol.ParseFrame(Encoding.UTF8.GetBytes("[1,2,3]"));
            Assert.IsTrue(call2.IsParseError);

            var call3 = RpcProtocol.ParseFrame(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}"));
            Assert.IsTrue(call2.IsParseError && call3.IsParseError, "缺 method 也应视为解析失败");
        }

        [TestMethod]
        public void RpcProtocol_EncodeResult_SuccessAndErrorShapes()
        {
            var ok = RpcProtocol.EncodeResult(7, CommandResult.Success(new { locked = true }));
            var okToken = JToken.Parse(Encoding.UTF8.GetString(ok));
            Assert.AreEqual("2.0", (string)okToken["jsonrpc"]);
            Assert.AreEqual(7L, (long)okToken["id"]);
            Assert.IsNotNull(okToken["result"]);
            Assert.IsNull(okToken["error"]);

            var fail = RpcProtocol.EncodeResult(null, CommandResult.Fail(-32002, "pin rejected"));
            var failToken = JToken.Parse(Encoding.UTF8.GetString(fail));
            Assert.IsNull(failToken["result"]);
            Assert.AreEqual(-32002, (int)failToken["error"]["code"]);
            Assert.AreEqual("pin rejected", (string)failToken["error"]["message"]);
        }

        // ---------- 配置节 ----------

        [TestMethod]
        public void AppSettings_IpcSection_DeepCopiedAndMergeGuarded()
        {
            var source = new AppSettings();
            source.Ipc.Enabled = false;
            source.Ipc.PipeName = "custom";

            var target = new AppSettings();
            source.CopyTo(target);
            Assert.IsFalse(target.Ipc.Enabled);
            Assert.AreEqual("custom", target.Ipc.PipeName);

            // 深拷贝：源后续变更不得影响目标
            source.Ipc.Enabled = true;
            Assert.IsFalse(target.Ipc.Enabled);

            // 旧配置文件没有 Ipc 节时 Merge 必须兜底
            var merged = AppSettings.Merge(new AppSettings { Ipc = null });
            Assert.IsNotNull(merged.Ipc);
        }

        // ---------- 端到端管道 ----------

        /// <summary>每步测试独立 server + 唯一管道名，便于 blame 定位挂死步骤。</summary>
        private static NamedPipeCommandServer StartE2EServer(out string pipeName)
        {
            var registry = new CommandRegistry();
            registry.Register("test", new CommandDescriptor
            {
                Name = "test.ping",
                Summary = "回显",
                Risk = CommandRisk.ReadOnly,
                Params = new CommandParam[] { new CommandParam { Name = "text", Type = "string" } },
                Handler = r => CommandResult.Success("pong:" + (r.Params.ContainsKey("text") ? r.Params["text"] : ""))
            });
            registry.Register("test", new CommandDescriptor
            {
                Name = "test.secret",
                Summary = "需要口令",
                Risk = CommandRisk.Low,
                RequiresPin = true,
                Handler = r => CommandResult.Success("granted")
            });

            var host = new CommandHost(
                registry,
                id => ModuleStatus.Running,
                new PinGuard(new FakePinService()),
                new NullCommandAuditSink());
            pipeName = "CarroDesk.test." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeCommandServer(host, pipeName, null, useAcl: false);
            server.Start();
            return server;
        }

        [TestMethod]
        public void PipeServer_E2E_Step1_PingWithParams()
        {
            string pipeName;
            var server = StartE2EServer(out pipeName);
            try
            {
                var ok = CallOverPipe(pipeName, new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "test.ping",
                    @params = new { text = "hi" }
                });
                Assert.AreEqual("pong:hi", (string)ok["result"]);
                Assert.AreEqual(1L, (long)ok["id"]);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void PipeServer_E2E_Step2_UnknownMethod()
        {
            string pipeName;
            var server = StartE2EServer(out pipeName);
            try
            {
                var unknown = CallOverPipe(pipeName, new { jsonrpc = "2.0", id = 2, method = "nope.nope" });
                Assert.AreEqual(-32601, (int)unknown["error"]["code"]);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void PipeServer_E2E_Step3_PinGate()
        {
            string pipeName;
            var server = StartE2EServer(out pipeName);
            try
            {
                var noPin = CallOverPipe(pipeName, new { jsonrpc = "2.0", id = 3, method = "test.secret" });
                Assert.AreEqual(-32002, (int)noPin["error"]["code"]);
                var withPin = CallOverPipe(pipeName, new { jsonrpc = "2.0", id = 4, method = "test.secret", pin = "1234" });
                Assert.AreEqual("granted", (string)withPin["result"]);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void PipeServer_E2E_Step4_KeepAliveTwoRequests()
        {
            string pipeName;
            var server = StartE2EServer(out pipeName);
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    client.Connect(5000);
                    WriteFrame(client, EncodeRpc(new { jsonrpc = "2.0", id = 10, method = "test.ping" }));
                    WriteFrame(client, EncodeRpc(new { jsonrpc = "2.0", id = 11, method = "test.ping" }));
                    var r1 = JToken.Parse(Encoding.UTF8.GetString(ReadFrame(client)));
                    var r2 = JToken.Parse(Encoding.UTF8.GetString(ReadFrame(client)));
                    Assert.AreEqual(10L, (long)r1["id"]);
                    Assert.AreEqual(11L, (long)r2["id"]);
                }
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void PipeServer_E2E_Step5_ParseError()
        {
            string pipeName;
            var server = StartE2EServer(out pipeName);
            try
            {
                var parseFail = CallOverPipeRaw(pipeName,
                    FrameCodec.EncodeFrame(Encoding.UTF8.GetBytes("not json at all")));
                Assert.AreEqual(-32700, (int)parseFail["error"]["code"]);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void PipeDefaults_PipeNameCarriesSid_AndSecurityRulesPresent()
        {
            var name = NamedPipeCommandServer.BuildDefaultPipeName();
            Assert.IsTrue(name.StartsWith(@"\\.\pipe\CarroDesk.ctl.", StringComparison.Ordinal));

            var security = NamedPipeCommandServer.BuildDefaultSecurity();
            Assert.IsNotNull(security);
            Assert.IsTrue(security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count >= 1,
                "默认 DACL 应至少包含当前用户或 SYSTEM 的读写规则");
        }

        // ---------- 测试辅助 ----------

        private static byte[] EncodeRpc(object rpc)
        {
            return FrameCodec.EncodeFrame(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rpc)));
        }

        private static JToken CallOverPipe(string pipeName, object rpc)
        {
            return CallOverPipeRaw(pipeName, EncodeRpc(rpc));
        }

        private static JToken CallOverPipeRaw(string pipeName, byte[] frame)
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                client.Connect(5000);
                client.Write(frame, 0, frame.Length);
                client.Flush();
                return JToken.Parse(Encoding.UTF8.GetString(ReadFrame(client)));
            }
        }

        private static void WriteFrame(PipeStream stream, byte[] frame)
        {
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        private static byte[] ReadFrame(PipeStream stream)
        {
            var header = new byte[4];
            ReadExactly(stream, header);
            var length = BitConverter.ToInt32(header, 0);
            var body = new byte[length];
            ReadExactly(stream, body);
            return body;
        }

        private static void ReadExactly(PipeStream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) throw new EndOfStreamException("test client unexpected EOF");
                read += n;
            }
        }
    }
}
