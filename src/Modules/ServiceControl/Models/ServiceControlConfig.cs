using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Modules.ServiceControl
{
    /// <summary>
    /// 授权清单条目（IPC 设计 §9.5 / SERVICE-CONTROL-PLAN §1）。
    /// 安全双层：本清单只是参数枚举与确认策略；真正的权限边界是服务对象 DACL。
    /// </summary>
    [JsonConverter(typeof(ServiceAllowlistEntryConverter))]
    public sealed class ServiceAllowlistEntry
    {
        /// <summary>Windows 服务短名（如 GameViewerService）。</summary>
        public string Name { get; set; }

        /// <summary>人类可读描述（如「UUYC 远程控制」），流入工具 schema 与 services.status 返回。</summary>
        public string Desc { get; set; }

        /// <summary>该服务 start/stop 是否要求口令。默认 true（fail-safe）；仅建议对"启停无害"的服务设 false。</summary>
        public bool RequiresPin { get; set; } = true;
    }

    /// <summary>
    /// 兼容两种写法：旧字符串数组（"Foo" → desc 空、requiresPin=true）
    /// 与新对象（{"name":"Foo","desc":"...","requiresPin":false}；description/pin 为别名）。
    /// </summary>
    public sealed class ServiceAllowlistEntryConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(ServiceAllowlistEntry);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            if (reader.TokenType == JsonToken.String)
            {
                return new ServiceAllowlistEntry
                {
                    Name = ((string)reader.Value)?.Trim(),
                    Desc = null,
                    RequiresPin = true
                };
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var obj = JObject.Load(reader);
                var requiresPin = ReadOptionalBool(obj, "requiresPin") ?? ReadOptionalBool(obj, "pin") ?? true;
                return new ServiceAllowlistEntry
                {
                    Name = (ReadString(obj, "name") ?? ReadString(obj, "service"))?.Trim(),
                    Desc = ReadString(obj, "desc") ?? ReadString(obj, "description"),
                    RequiresPin = requiresPin
                };
            }

            throw new JsonSerializationException(
                "AllowedServices 条目必须是服务名字符串或 {name, desc, requiresPin} 对象，实际: " + reader.TokenType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var entry = value as ServiceAllowlistEntry;
            if (entry == null)
            {
                writer.WriteNull();
                return;
            }
            writer.WriteStartObject();
            writer.WritePropertyName("name");
            writer.WriteValue(entry.Name);
            if (!string.IsNullOrEmpty(entry.Desc))
            {
                writer.WritePropertyName("desc");
                writer.WriteValue(entry.Desc);
            }
            writer.WritePropertyName("requiresPin");
            writer.WriteValue(entry.RequiresPin);
            writer.WriteEndObject();
        }

        private static string ReadString(JObject obj, string property)
        {
            var token = obj[property];
            var s = token == null ? null : token.Value<string>();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static bool? ReadOptionalBool(JObject obj, string property)
        {
            var token = obj[property];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            if (token.Type == JTokenType.Integer) return token.Value<long>() != 0;
            if (token.Type == JTokenType.String)
            {
                bool parsed;
                if (bool.TryParse((string)token, out parsed)) return parsed;
            }
            return null;
        }
    }

    /// <summary>
    /// 服务控制模块配置。AllowedServices 是「允许经能力通道启停的服务清单」，
    /// 同时作为 services.* 能力的参数枚举白名单（AllowedValues）与按服务口令策略。
    /// 真正的安全边界是服务对象 DACL（IPC 设计 §11.2 路径 A）：
    /// 即使配置被改写，未授权服务在内核对象权限处仍然拒绝。
    /// 存放于运行时 config.json 的模块节，不进代码/git。
    /// </summary>
    public sealed class ServiceControlConfig
    {
        public List<ServiceAllowlistEntry> AllowedServices { get; set; } = new List<ServiceAllowlistEntry>();
    }
}
