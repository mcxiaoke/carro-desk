namespace CarroDesk.Core.Commands
{
    public sealed class CommandError
    {
        public int Code { get; set; }
        public string Message { get; set; }
    }

    /// <summary>能力执行结果。错误码与 RPC 错误码共用一张表（IPC 设计 §5.2）。</summary>
    public sealed class CommandResult
    {
        public bool Ok { get; set; }
        public object Data { get; set; }
        public CommandError Error { get; set; }

        public static CommandResult Success(object data = null)
        {
            return new CommandResult { Ok = true, Data = data };
        }

        public static CommandResult Fail(int code, string message)
        {
            return new CommandResult
            {
                Ok = false,
                Error = new CommandError { Code = code, Message = message }
            };
        }
    }
}
