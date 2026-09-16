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

                // 尝试作为 DateTime 格式化串（如 yyyyMMdd, yyyy-MM-dd_HHmmss 等）
                if (ContainsDateTimeFormatChars(key))
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

                // 环境变量回退
                string envVal = Environment.GetEnvironmentVariable(key);
                if (envVal != null)
                {
                    return envVal;
                }

                // 无法识别的保留原样
                return match.Value;
            });
        }

        private static bool ContainsDateTimeFormatChars(string fmt)
        {
            foreach (char c in fmt)
            {
                if (c == 'y' || c == 'M' || c == 'd' || c == 'H' || c == 'h' || c == 'm' || c == 's' || c == 'f')
                    return true;
            }
            return false;
        }
    }
}
