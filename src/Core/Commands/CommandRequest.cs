using System.Collections.Generic;

namespace CarroDesk.Core.Commands
{
    /// <summary>
    /// 一次能力调用（IPC 设计 §4.1/§9.6）。
    /// 内核只接受原始 CLR 类型参数（string/int/bool/数值），JSON 到原始类型的转换是传输适配器的职责。
    /// <see cref="Pin"/> 供 RequiresPin 能力内联校验（v1.2「口令即确认」）；本机弹窗适配器负责先收集再传入。
    /// </summary>
    public sealed class CommandRequest
    {
        public string Method { get; set; }

        public IDictionary<string, object> Params { get; set; }

        /// <summary>口令。仅用于 RequiresPin 能力校验；绝不入审计/日志（§9.6 第 5 条）。</summary>
        public string Pin { get; set; }

        /// <summary>调用来源标识（如 "local" / "cli" / "mcp" / "http"），用于限流维度与审计，不参与鉴权。</summary>
        public string Source { get; set; }

        public static CommandRequest Create(string method, string source = null)
        {
            return new CommandRequest
            {
                Method = method,
                Params = new Dictionary<string, object>(),
                Source = source ?? "local"
            };
        }
    }
}
