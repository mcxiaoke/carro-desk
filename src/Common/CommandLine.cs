using System;
using System.Text;

namespace CarroDesk.Common
{
    /// <summary>
    /// Windows 命令行参数的引号化与 cmd.exe 转义。
    ///
    /// 背景：调度任务把脚本路径与用户参数拼成命令行交给解释器，其中 cmd.exe 会对
    /// 整条命令行做"二次解析"。原实现直接字符串相加，导致：
    ///   1) 路径/参数里出现空格或引号时被拆错、甚至闭合掉我们自己的引号；
    ///   2) 参数里的 &amp; | &lt; &gt; ( ) 被 cmd 当作命令分隔符执行（意外命令执行）。
    /// 这里提供两个纯函数，把"原样传递"变成可测试的显式行为。
    /// </summary>
    public static class CommandLine
    {
        private static readonly char[] NeedsQuoting = { ' ', '\t', '"' };
        private static readonly char[] CmdMetaChars = { '^', '&', '|', '<', '>', '(', ')' };

        /// <summary>
        /// 按 CommandLineToArgvW / CRT 规则对单个参数加引号。
        /// 不含空白与引号时原样返回，避免产生多余引号。
        /// 关键细节：紧邻引号或位于结尾的连续反斜杠需要翻倍，
        /// 否则会转义掉收尾引号（Windows 命令行的经典坑）。
        /// </summary>
        public static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.IndexOfAny(NeedsQuoting) < 0) return value;

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            int pendingBackslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    pendingBackslashes++;
                    continue;
                }

                if (c == '"')
                {
                    // 引号前的反斜杠必须翻倍，再额外加一个用来转义引号本身
                    sb.Append('\\', pendingBackslashes * 2 + 1);
                    sb.Append('"');
                    pendingBackslashes = 0;
                    continue;
                }

                if (pendingBackslashes > 0)
                {
                    sb.Append('\\', pendingBackslashes);
                    pendingBackslashes = 0;
                }
                sb.Append(c);
            }

            // 结尾的反斜杠会转义收尾引号，必须翻倍
            if (pendingBackslashes > 0) sb.Append('\\', pendingBackslashes * 2);

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 转义 cmd.exe 的命令分隔/分组元字符，使参数在 cmd 二次解析后仍为字面量。
        ///
        /// 注意两点：
        ///   1) 引号内的元字符对 cmd 本就无特殊含义，且引号内的 ^ 是字面量，
        ///      因此只转义"引号之外"的元字符——否则会把 "^" 污染进参数值；
        ///   2) 引号本身不转义（参数分组依赖它），% 也不处理
        ///      （%VAR% 已由调用方预先展开，且 cmd /c 与批处理内的 %% 语义不同）。
        /// </summary>
        public static string EscapeForCmd(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var sb = new StringBuilder(value.Length + 8);
            bool inQuotes = false;

            foreach (char c in value)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    continue;
                }

                if (!inQuotes && Array.IndexOf(CmdMetaChars, c) >= 0) sb.Append('^');
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>把参数段追加到命令行尾部；参数为空时不产生多余空格。</summary>
        public static string AppendArgs(string commandLine, string args)
        {
            if (string.IsNullOrEmpty(args)) return commandLine;
            return commandLine + " " + args;
        }
    }
}
