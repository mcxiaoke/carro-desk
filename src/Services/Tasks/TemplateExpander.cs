using System;
using System.Text.RegularExpressions;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks
{
    public static class TemplateExpander
    {
        private static readonly Regex TemplateRegex = new Regex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);

        public static string Expand(string text, TaskDefinition task)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var now = DateTime.Now;
            return TemplateRegex.Replace(text, match =>
            {
                string key = match.Groups[1].Value.Trim();

                if (string.Equals(key, "date", StringComparison.OrdinalIgnoreCase))
                    return now.ToString("yyyy-MM-dd");

                if (string.Equals(key, "time", StringComparison.OrdinalIgnoreCase))
                    return now.ToString("HH-mm-ss");

                if (string.Equals(key, "datetime", StringComparison.OrdinalIgnoreCase))
                    return now.ToString("yyyy-MM-dd_HH-mm-ss");

                if (string.Equals(key, "task", StringComparison.OrdinalIgnoreCase))
                    return task != null && !string.IsNullOrEmpty(task.Name) ? task.Name : "";

                if (string.Equals(key, "scripts", StringComparison.OrdinalIgnoreCase))
                    return ConfigService.ScriptsDirPath;

                if (string.Equals(key, "logs", StringComparison.OrdinalIgnoreCase))
                    return ConfigService.LogsDirPath;

                if (string.Equals(key, "dir", StringComparison.OrdinalIgnoreCase))
                    return ConfigService.DirPath;

                // 环境变量回退优先匹配
                string envVal = Environment.GetEnvironmentVariable(key);
                if (envVal != null)
                {
                    return envVal;
                }

                // 尝试作为 DateTime 格式化串（如 yyyyMMdd, yyyy-MM-dd_HHmmss 等）
                if (IsLikelyDateTimeFormat(key))
                {
                    try
                    {
                        return now.ToString(key);
                    }
                    catch
                    {
                        // 忽略无效格式
                    }
                }

                // 无法识别的保留原样
                return match.Value;
            });
        }

        private static bool IsLikelyDateTimeFormat(string fmt)
        {
            if (string.IsNullOrWhiteSpace(fmt)) return false;

            bool hasCoreChar = false;
            foreach (char c in fmt)
            {
                if (c == 'y' || c == 'M' || c == 'd' || c == 'H' || c == 'h' || c == 'm' || c == 's')
                {
                    hasCoreChar = true;
                    break;
                }
            }
            if (!hasCoreChar) return false;

            // 格式串中所有非数字字符必须是合法的日期时间占位符或常见分隔符，
            // 避免任意包含 m, d, s 的普通单词（如 password, custom_var）被意外当成日期解析
            foreach (char c in fmt)
            {
                if (char.IsDigit(c)) continue;
                switch (c)
                {
                    case 'y': case 'Y':
                    case 'M':
                    case 'd': case 'D':
                    case 'h': case 'H':
                    case 'm':
                    case 's': case 'S':
                    case 'f': case 'F':
                    case 't': case 'T':
                    case 'z': case 'Z':
                    case '-': case '_': case ':': case '.': case '/': case ' ':
                    case '\\': case '\'': case '"':
                        continue;
                    default:
                        return false;
                }
            }
            return true;
        }
    }
}
