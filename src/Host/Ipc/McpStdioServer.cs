using System;
using System.IO;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// MCP stdio 瘦进程（IPC 设计 §5.5，S4）：stdio 上按行收发 JSON-RPC（MCP 规范：
    /// 换行分隔、单行内不得有嵌入换行），工具调用经控制管道转发给宿主。
    /// stdout 只输出协议消息；一切日志走 stderr。能力表在首次 tools/list 时经
    /// host.capabilities.list 拉取并缓存（listChanged 未支持，宿主升级后重启会话即可）。
    /// 约定：工具参数里的 "pin" 被提取为请求口令（§9.6 口令即确认），不进入业务参数。
    /// </summary>
    public static class McpStdioServer
    {
        public const string DefaultProtocolVersion = "2025-06-18";
        private const string ServerName = "carrodesk";

        /// <summary>阻塞运行直至 stdin EOF；返回进程退出码（0 = 正常）。</summary>
        public static int Run(TextReader input, TextWriter output, TextWriter log, Func<PipeRpcClient> clientFactory)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (log == null) throw new ArgumentNullException(nameof(log));

            var session = new Session(log, clientFactory ?? (() => new PipeRpcClient()));
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var response = session.Handle(line);
                if (response == null) continue; // 通知不回应
                output.WriteLine(response);
                try { output.Flush(); }
                catch { return 1; } // stdout 断开（客户端退出）
            }
            return 0;
        }

        private sealed class Session
        {
            private readonly TextWriter _log;
            private readonly Func<PipeRpcClient> _clientFactory;
            private JObject _capabilities; // host.capabilities.list 的 result（含 hash + capabilities 数组）

            public Session(TextWriter log, Func<PipeRpcClient> clientFactory)
            {
                _log = log;
                _clientFactory = clientFactory;
            }

            public string Handle(string line)
            {
                JObject request;
                object id = null;
                try
                {
                    request = JToken.Parse(line) as JObject;
                    if (request == null)
                        return Error(null, CommandErrorCodes.ParseError, "request is not a JSON object");
                    if (request["id"] != null) id = RpcProtocol.NormalizeValue(request["id"]);
                }
                catch (Exception ex)
                {
                    return Error(null, CommandErrorCodes.ParseError, "parse failed: " + ex.Message);
                }

                var method = (string)request["method"];
                if (string.IsNullOrWhiteSpace(method))
                    return id == null ? null : Error(id, CommandErrorCodes.ParseError, "missing method");

                try
                {
                    if (method == "initialize")
                        return id == null ? null : Ok(id, BuildInitialize(request));
                    if (method == "ping")
                        return id == null ? null : Ok(id, new JObject());
                    if (method == "tools/list")
                        return id == null ? null : Ok(id, BuildToolsList());
                    if (method == "tools/call")
                        return id == null ? null : CallTool(id, request);

                    // MCP 通知（无 id）一律静默忽略；未知请求回 -32601
                    return id == null
                        ? null
                        : Error(id, CommandErrorCodes.CapabilityNotFound, "unknown method: " + method);
                }
                catch (Exception ex)
                {
                    _log.WriteLine("mcp: handler error for '" + method + "': " + ex.Message);
                    return id == null ? null : Error(id, CommandErrorCodes.Internal, "internal error: " + ex.Message);
                }
            }

            private JObject BuildInitialize(JObject request)
            {
                var parameters = request["params"] as JObject;
                var requested = parameters == null ? null : (string)parameters["protocolVersion"];
                return new JObject
                {
                    ["protocolVersion"] = string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested,
                    ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = ServerName,
                        ["version"] = HostVersion()
                    },
                    ["instructions"] = "CarroDesk 本机控制。先 tools/list 获取能力；标注需口令的工具在 arguments 里传 pin。"
                };
            }

            private JObject BuildToolsList()
            {
                EnsureCapabilities();
                var tools = new JArray();
                var list = _capabilities == null ? null : _capabilities["capabilities"] as JArray;
                if (list != null)
                {
                    foreach (var capability in list)
                    {
                        tools.Add(ToTool(capability));
                    }
                }
                return new JObject { ["tools"] = tools };
            }

            private void EnsureCapabilities()
            {
                if (_capabilities != null) return;
                var result = _clientFactory().Call(CommandRequest.Create("host.capabilities.list", "mcp"));
                if (!result.Ok)
                {
                    _log.WriteLine("mcp: capabilities fetch failed: "
                        + (result.Error != null ? result.Error.Code + " " + result.Error.Message : "unknown"));
                    _capabilities = new JObject { ["capabilities"] = new JArray() };
                    return;
                }
                _capabilities = result.Data as JObject ?? new JObject { ["capabilities"] = new JArray() };
            }

            private string CallTool(object id, JObject request)
            {
                var parameters = request["params"] as JObject;
                var name = parameters == null ? null : (string)parameters["name"];
                if (string.IsNullOrWhiteSpace(name))
                    return Error(id, -32602, "missing tool name");

                var arguments = parameters == null ? null : parameters["arguments"] as JObject;
                var dict = RpcProtocol.ExtractParams(arguments);

                // 约定：arguments["pin"] → 请求口令（若工具本身声明了 pin 参数，此约定冲突；当前无此工具）
                string pin = null;
                object pinValue;
                if (dict.TryGetValue("pin", out pinValue))
                {
                    pin = pinValue == null ? null : pinValue.ToString();
                    dict.Remove("pin");
                }

                var result = _clientFactory().Call(new CommandRequest
                {
                    Method = name.Trim(),
                    Source = "mcp",
                    Pin = pin,
                    Params = dict
                });

                var text = result.Ok
                    ? Textify(result.Data)
                    : "error " + result.Error.Code + ": " + result.Error.Message;
                return Ok(id, new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                    ["isError"] = !result.Ok
                });
            }

            private static JObject ToTool(JToken capability)
            {
                var properties = new JObject();
                var required = new JArray();
                var parameters = capability["parameters"] as JArray;
                if (parameters != null)
                {
                    foreach (var p in parameters)
                    {
                        var name = (string)p["name"];
                        var schema = new JObject { ["type"] = MapSchemaType((string)p["type"]) };
                        var description = (string)p["description"];
                        if (!string.IsNullOrEmpty(description)) schema["description"] = description;
                        var allowed = p["allowedValues"] as JArray;
                        if (allowed != null && allowed.Count > 0) schema["enum"] = allowed;
                        properties[name] = schema;
                        if ((bool?)p["required"] == true) required.Add(name);
                    }
                }

                var requiresPin = (bool?)capability["requiresPin"] == true;
                var summary = (string)capability["summary"] ?? (string)capability["name"];
                var descriptionText = requiresPin ? summary + "（需口令：arguments 中传 pin）" : summary;

                return new JObject
                {
                    ["name"] = (string)capability["name"],
                    ["description"] = descriptionText,
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required
                    }
                };
            }

            private static string MapSchemaType(string type)
            {
                if (string.Equals(type, "int", StringComparison.OrdinalIgnoreCase)) return "integer";
                if (string.Equals(type, "bool", StringComparison.OrdinalIgnoreCase)) return "boolean";
                return "string";
            }

            /// <summary>工具结果文本化：字符串去引号原样输出，标量直出，其余紧凑 JSON——对 LLM 更可读。</summary>
            private static string Textify(object data)
            {
                if (data == null) return "(no content)";
                var token = data as JToken;
                if (token != null)
                {
                    if (token.Type == JTokenType.String) return token.Value<string>();
                    if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float
                        || token.Type == JTokenType.Boolean || token.Type == JTokenType.Null)
                        return token.ToString(Formatting.None);
                    return token.ToString(Formatting.None);
                }
                return JsonConvert.SerializeObject(data, Formatting.None);
            }

            private static string HostVersion()
            {
                try
                {
                    var version = typeof(McpStdioServer).Assembly.GetName().Version;
                    return version != null ? version.ToString() : "unknown";
                }
                catch
                {
                    return "unknown";
                }
            }

            private static string Ok(object id, JToken result)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id == null ? JValue.CreateNull() : JToken.FromObject(id),
                    ["result"] = result
                }.ToString(Formatting.None);
            }

            private static string Error(object id, int code, string message)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id == null ? JValue.CreateNull() : JToken.FromObject(id),
                    ["error"] = new JObject { ["code"] = code, ["message"] = message }
                }.ToString(Formatting.None);
            }
        }
    }
}
