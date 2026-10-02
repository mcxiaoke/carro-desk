using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// JSON-RPC 2.0 形状与命令内核之间的映射（IPC 设计 §5.2）。本类只做协议翻译：
    /// 鉴权、白名单、参数校验全部在 CommandHost，传输层不得夹带业务逻辑。
    /// 请求形状：{"jsonrpc":"2.0","id":..,"method":"..","params":{..},"pin":"..","source":".."}
    /// pin/source 为本协议扩展字段：pin 支撑「口令即确认」（§9.6），独立于 params，
    /// 因此永不进入参数摘要与审计。
    /// </summary>
    public static class RpcProtocol
    {
        public sealed class IncomingCall
        {
            /// <summary>请求 id 原样回传（string/number/bool/null）。</summary>
            public object Id;
            public CommandRequest Request;
            /// <summary>true = 帧不是合法 JSON-RPC 请求，应以 -32700 回应。</summary>
            public bool IsParseError;
        }

        public static IncomingCall ParseFrame(byte[] frame)
        {
            object id = null;
            try
            {
                var token = JToken.Parse(Encoding.UTF8.GetString(frame));
                var obj = token as JObject;
                if (obj == null) return ParseError(null);

                if (obj["id"] != null) id = NormalizeValue(obj["id"]);

                var method = (string)obj["method"];
                if (string.IsNullOrWhiteSpace(method)) return ParseError(id);

                var request = new CommandRequest
                {
                    Method = method.Trim(),
                    Source = NormalizeSource((string)obj["source"]),
                    Pin = (string)obj["pin"],
                    Params = ExtractParams(obj["params"])
                };
                return new IncomingCall { Id = id, Request = request };
            }
            catch
            {
                return ParseError(id);
            }
        }

        /// <summary>
        /// 把请求体里的 source 归一化到封闭集合。
        ///
        /// 安全动因：source 参与限流键（能力 + 来源），而限流是按调用方声明的来源分桶的。
        /// 若原样采信任意字符串，调用方每次换一个 source 即可让限流恒真，
        /// 且限流字典键数随调用量无界增长（OOM）。因此未在白名单内的一律折叠为 unknown。
        /// 保留白名单而非直接忽略，是为了不破坏 CLI/MCP 区分来源的既有语义与审计可读性。
        /// </summary>
        private static string NormalizeSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "pipe";
            switch (source.Trim().ToLowerInvariant())
            {
                case "cli":
                case "pipe":
                case "mcp":
                case "local":
                case "test":
                case "custom":
                case "http":
                    return source.Trim().ToLowerInvariant();
                default:
                    return "unknown";
            }
        }

        public static byte[] EncodeResult(object id, CommandResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : JToken.FromObject(id)
            };

            if (result.Ok)
            {
                response["result"] = result.Data == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(result.Data);
            }
            else
            {
                response["error"] = new JObject
                {
                    ["code"] = result.Error != null ? result.Error.Code : CommandErrorCodes.Internal,
                    ["message"] = result.Error != null ? result.Error.Message : "internal error"
                };
            }
            return Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
        }

        // ---------- 客户端侧（S3：CLI / 二实例转发 / MCP 瘦进程共用） ----------

        private static long _requestId;

        /// <summary>把 CommandRequest 编码为 JSON-RPC 请求帧体（自增 id，pin/source 为扩展字段）。</summary>
        public static byte[] EncodeRequest(CommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var obj = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Interlocked.Increment(ref _requestId),
                ["method"] = request.Method
            };

            var parameters = new JObject();
            if (request.Params != null)
            {
                foreach (var kv in request.Params)
                {
                    parameters[kv.Key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
                }
            }
            obj["params"] = parameters;

            if (!string.IsNullOrEmpty(request.Pin)) obj["pin"] = request.Pin;
            if (!string.IsNullOrEmpty(request.Source)) obj["source"] = request.Source;
            return Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
        }

        /// <summary>解析 JSON-RPC 响应帧体为 CommandResult。解析失败折叠为 -32700，绝不抛异常。</summary>
        public static CommandResult ParseResponse(byte[] payload, out object id)
        {
            id = null;
            try
            {
                var obj = JToken.Parse(Encoding.UTF8.GetString(payload)) as JObject;
                if (obj == null)
                    return CommandResult.Fail(CommandErrorCodes.ParseError, "response is not a JSON object");

                if (obj["id"] != null) id = NormalizeValue(obj["id"]);

                var error = obj["error"] as JObject;
                if (error != null)
                    return CommandResult.Fail(
                        (int)error["code"],
                        (string)error["message"] ?? "unknown error");

                if (obj["result"] != null)
                    return CommandResult.Success(
                        obj["result"].Type == JTokenType.Null ? null : (object)obj["result"]);

                return CommandResult.Fail(CommandErrorCodes.ParseError, "response has neither result nor error");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(CommandErrorCodes.ParseError, "response parse failed: " + ex.Message);
            }
        }

        /// <summary>结果负载的文本化：JToken 原样，其他对象经 FromObject。供 CLI/工具调用输出。</summary>
        public static string FormatPayload(object data, bool pretty)
        {
            if (data == null) return "null";
            var token = data as JToken;
            var formatting = pretty ? Formatting.Indented : Formatting.None;
            return token != null
                ? token.ToString(formatting)
                : JToken.FromObject(data).ToString(formatting);
        }

        private static IncomingCall ParseError(object id)
        {
            return new IncomingCall { Id = id, IsParseError = true };
        }

        /// <summary>
        /// 把 JSON 对象参数规整为「原始类型」字典（bool/int/float/string；嵌套结构降级为 JSON 字符串）。
        /// 客户端侧（MCP 工具参数）与服务端侧共用同一收敛规则。
        /// </summary>
        public static IDictionary<string, object> ExtractParams(JToken parameters)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            var obj = parameters as JObject;
            if (obj == null) return dict;
            foreach (var prop in obj.Properties())
            {
                dict[prop.Name] = NormalizeValue(prop.Value);
            }
            return dict;
        }

        /// <summary>
        /// 标量取值：bool/int/float/string 原样交给内核（内核负责 int 收敛与拒绝）；
        /// 嵌套结构降级为 JSON 字符串，交由参数校验拒绝或按 string 消费。
        /// </summary>
        public static object NormalizeValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            switch (token.Type)
            {
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.Integer: return (long)token;
                case JTokenType.Float: return (double)token;
                case JTokenType.String: return (string)token;
                default: return token.ToString(Formatting.None);
            }
        }
    }
}
