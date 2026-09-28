using System;
using System.Collections.Generic;

namespace CarroDesk
{
    /// <summary>
    /// `ctl` 控制命令行解析（IPC 设计 §5.3，S3）：
    /// <code>
    ///   CarroDesk.exe ctl &lt;能力名&gt; [--json] [--pin &lt;口令&gt;] [--pipe &lt;名称&gt;] [--timeout &lt;ms&gt;] [--&lt;参数名&gt; &lt;值&gt;...]
    /// </code>
    /// 保留字（json/pin/pipe/timeout/source）为控制选项；其余 --key value 进入请求参数，
    /// 不带值的 --key 解析为布尔 true。值一律按字符串传递，类型收敛由内核参数校验完成。
    /// 主 exe 与 CarroDesk.Cli 共用本解析。
    /// </summary>
    public sealed class ControlArgs
    {
        public string Method;
        public string Pin;
        public string PipeName;
        public string Source = "cli";
        public int TimeoutMs;
        public bool Json;
        public readonly Dictionary<string, object> Params = new Dictionary<string, object>(StringComparer.Ordinal);

        private static readonly HashSet<string> ReservedValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pin", "pipe", "timeout", "source"
        };

        public static bool IsControlInvocation(string[] args)
        {
            return args != null && args.Length > 0
                && string.Equals(args[0], "ctl", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParse(string[] args, out ControlArgs parsed, out string error)
        {
            parsed = null;
            error = null;
            if (!IsControlInvocation(args))
            {
                error = "not a ctl invocation";
                return false;
            }

            var result = new ControlArgs();
            var index = 1;

            if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = "missing capability name after 'ctl'";
                return false;
            }
            result.Method = args[index];
            index++;

            while (index < args.Length)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
                {
                    error = "unexpected argument: " + token;
                    return false;
                }
                var key = token.Substring(2);
                index++;

                if (string.Equals(key, "json", StringComparison.OrdinalIgnoreCase))
                {
                    result.Json = true;
                    continue;
                }

                string value = null;
                var hasValue = index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal);
                if (hasValue)
                {
                    value = args[index];
                    index++;
                }

                if (ReservedValueOptions.Contains(key))
                {
                    if (!hasValue)
                    {
                        error = "option --" + key + " requires a value";
                        return false;
                    }
                    if (string.Equals(key, "pin", StringComparison.OrdinalIgnoreCase)) result.Pin = value;
                    else if (string.Equals(key, "pipe", StringComparison.OrdinalIgnoreCase)) result.PipeName = value;
                    else if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase)) result.Source = value;
                    else
                    {
                        int timeout;
                        if (!int.TryParse(value, out timeout) || timeout <= 0)
                        {
                            error = "option --timeout expects a positive integer (milliseconds), got: " + value;
                            return false;
                        }
                        result.TimeoutMs = timeout;
                    }
                    continue;
                }

                // 普通请求参数：有值取值，无值视为布尔 true
                result.Params[key] = hasValue ? (object)value : true;
            }

            parsed = result;
            return true;
        }
    }
}
