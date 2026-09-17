using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Models.Converters
{
    /// <summary>
    /// 支持数组 ["a.exe", "b.exe"] 与逗号/分号分隔字符串 "a.exe, b.exe; c.exe" 自适应互转的转换器
    /// </summary>
    public class StringOrStringListConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<string>);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var list = new List<string>();

            if (reader.TokenType == JsonToken.StartArray)
            {
                var arr = JArray.Load(reader);
                foreach (var item in arr)
                {
                    var s = item?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s))
                    {
                        list.Add(s);
                    }
                }
            }
            else if (reader.TokenType == JsonToken.String)
            {
                var val = reader.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var parts = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var t = p.Trim();
                        if (!string.IsNullOrEmpty(t))
                        {
                            list.Add(t);
                        }
                    }
                }
            }

            return list;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var list = value as List<string>;
            if (list == null)
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
                return;
            }

            writer.WriteStartArray();
            foreach (var item in list)
            {
                writer.WriteValue(item);
            }
            writer.WriteEndArray();
        }
    }
}
