using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Ipc;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>S3 验收：管道 RPC 客户端（CLI / 二实例转发 / MCP 共用）经真实管道服务的往返。</summary>
    [TestClass]
    public class PipeRpcClientTests
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
                Summary = "回显",
                Risk = CommandRisk.ReadOnly,
                Handler = r => CommandResult.Success("echo:" + r.Method)
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
            pipeName = "CarroDesk.client-test." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeCommandServer(host, pipeName, null, useAcl: false);
            server.Start();
            return server;
        }

        [TestMethod]
        public void Call_OverRealServer_ReturnsResult()
        {
            string pipeName;
            var server = StartTestServer(out pipeName);
            try
            {
                var client = new PipeRpcClient(pipeName);
                var result = client.Call(CommandRequest.Create("test.echo"));
                Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
                Assert.AreEqual("echo:test.echo", (string)((Newtonsoft.Json.Linq.JToken)result.Data));
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void Call_UnknownMethod_MapsServerError()
        {
            string pipeName;
            var server = StartTestServer(out pipeName);
            try
            {
                var client = new PipeRpcClient(pipeName);
                var result = client.Call(CommandRequest.Create("nope.nope"));
                Assert.IsFalse(result.Ok);
                Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, result.Error.Code);
            }
            finally { server.Dispose(); }
        }

        [TestMethod]
        public void Call_PinRoundTrip()
        {
            string pipeName;
            var server = StartTestServer(out pipeName);
            try
            {
                var client = new PipeRpcClient(pipeName);

                var noPin = CommandRequest.Create("test.secret");
                Assert.AreEqual(CommandErrorCodes.PinRequired, client.Call(noPin).Error.Code);

                var withPin = CommandRequest.Create("test.secret");
                withPin.Pin = "1234";
                var ok = client.Call(withPin);
                Assert.IsTrue(ok.Ok, ok.Error != null ? ok.Error.Message : "");
                Assert.AreEqual("granted", (string)((Newtonsoft.Json.Linq.JToken)ok.Data));
            }
            finally { server.Dispose(); }
        }

        /// <summary>
        /// H7：对端接受连接、读走请求后故意不响应，客户端必须在读超时后返回 TransportError，
        /// 而不是永久阻塞（宿主卡死时 CLI/MCP 不能一起挂住）。
        /// </summary>
        [TestMethod]
        public void Call_ServerNeverResponds_ReturnsTransportTimeout()
        {
            string pipeName = "CarroDesk.timeout-test." + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var accept = server.WaitForConnectionAsync();
                var client = new PipeRpcClient(pipeName, 2000, 300);
                var sw = Stopwatch.StartNew();
                var call = Task.Run(() => client.Call(CommandRequest.Create("host.status")));

                Assert.IsTrue(accept.Wait(3000), "客户端应能连上测试管道");
                // 读走完整请求帧，但故意不写响应
                var header = new byte[4];
                server.ReadExactly(header, 0, header.Length);
                var body = new byte[BitConverter.ToInt32(header, 0)];
                server.ReadExactly(body, 0, body.Length);

                Assert.IsTrue(call.Wait(5000), "客户端必须超时返回，不能永久挂起");
                sw.Stop();
                Assert.IsFalse(call.Result.Ok);
                Assert.AreEqual(CommandErrorCodes.TransportError, call.Result.Error.Code);
                StringAssert.Contains(call.Result.Error.Message, "timeout");
                Assert.IsTrue(sw.ElapsedMilliseconds < 2500,
                    "读超时应及时返回，实际 " + sw.ElapsedMilliseconds + "ms");
            }
        }

        [TestMethod]
        public void Call_UnreachablePipe_FoldsToTransportError()
        {
            var client = new PipeRpcClient("CarroDesk.nonexistent." + Guid.NewGuid().ToString("N"), 300);
            var result = client.Call(CommandRequest.Create("host.status"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.TransportError, result.Error.Code);
        }

        [TestMethod]
        public void ParseResponse_SuccessAndErrorShapes()
        {
            object id;
            var ok = RpcProtocol.ParseResponse(
                System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":{\"a\":1}}"), out id);
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual(9L, id);
            Assert.AreEqual(1L, (long)((Newtonsoft.Json.Linq.JToken)ok.Data)["a"]);

            var fail = RpcProtocol.ParseResponse(
                System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32601,\"message\":\"nope\"}}"), out id);
            Assert.IsFalse(fail.Ok);
            Assert.AreEqual(-32601, fail.Error.Code);
            Assert.AreEqual("nope", fail.Error.Message);

            var broken = RpcProtocol.ParseResponse(System.Text.Encoding.UTF8.GetBytes("not json"), out id);
            Assert.AreEqual(CommandErrorCodes.ParseError, broken.Error.Code);
        }

        [TestMethod]
        public void EncodeRequest_IncludesMethodParamsPinSource()
        {
            var request = CommandRequest.Create("audio.output.set", "mcp");
            request.Params["device"] = "speakers";
            request.Params["volume"] = 3;
            request.Pin = "777";

            var payload = System.Text.Encoding.UTF8.GetString(RpcProtocol.EncodeRequest(request));
            var token = Newtonsoft.Json.Linq.JToken.Parse(payload);

            Assert.AreEqual("audio.output.set", (string)token["method"]);
            Assert.AreEqual("speakers", (string)token["params"]["device"]);
            Assert.AreEqual(3L, (long)token["params"]["volume"]);
            Assert.AreEqual("777", (string)token["pin"]);
            Assert.AreEqual("mcp", (string)token["source"]);
        }
    }
}
