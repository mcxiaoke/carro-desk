namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 命令内核错误码，与 RPC 错误码共用一张表（IPC 设计 §5.2）。
    /// -32700（帧/JSON 解析）是传输层职责，此处仅占位对齐。
    /// </summary>
    public static class CommandErrorCodes
    {
        public const int ParseError = -32700;          // 传输层使用
        public const int CapabilityNotFound = -32601;  // 白名单未命中
        public const int InvalidParams = -32602;
        public const int ModuleUnavailable = -32001;   // 模块未运行/故障
        public const int PinRequired = -32002;         // 需要口令、口令被拒或触发失败限流
        public const int RateLimited = -32003;
        public const int Timeout = -32004;
        public const int Internal = -32010;
    }
}
