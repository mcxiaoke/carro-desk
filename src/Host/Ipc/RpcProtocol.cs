using System;
using System.Collections.Generic;
using System.Text;
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

                if (obj["id"] != null) id = NormalizeScalar(obj["id"]);

                var method = (string)obj["method"];
                if (string.IsNullOrWhiteSpace(method)) return ParseError(id);

                var request = new CommandRequest
                {
                    Method = method.Trim(),
                    Source = (string)obj["source"] ?? "pipe",
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

        private static IncomingCall ParseError(object id)
        {
            return new IncomingCall { Id = id, IsParseError = true };
        }

        private static IDictionary<string, object> ExtractParams(JToken parameters)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            var obj = parameters as JObject;
            if (obj == null) return dict;
            foreach (var prop in obj.Properties())
            {
                dict[prop.Name] = NormalizeScalar(prop.Value);
            }
            return dict;
        }

        /// <summary>
        /// 标量取值：bool/int/float/string 原样交给内核（内核负责 int 收敛与拒绝）；
        /// 嵌套结构降级为 JSON 字符串，交由参数校验拒绝或按 string 消费。
        /// </summary>
        private static object NormalizeScalar(JToken token)
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
