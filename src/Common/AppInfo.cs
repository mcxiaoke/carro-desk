using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CarroDesk.Core;
using CarroDesk.Services;

namespace CarroDesk.Common
{
    /// <summary>
    /// 应用程序元数据汇聚服务。
    /// 统一解析程序集属性、Git Commit、构建时间、运行时环境及数据目录信息。
    /// </summary>
    public static class AppInfo
    {
        public const string ProductName = "CarroDesk";

        private static bool _initialized;
        private static string _version = "1.0.0";
        private static string _informationalVersion = "1.0.0";
        private static string _displayVersion = "v1.0.0";
        private static string _gitCommit = "unknown";
        private static string _buildTime = "unknown";

        static AppInfo()
        {
            EnsureInitialized();
        }

        public static string Version
        {
            get
            {
                EnsureInitialized();
                return _version;
            }
        }

        public static string InformationalVersion
        {
            get
            {
                EnsureInitialized();
                return _informationalVersion;
            }
        }

        public static string DisplayVersion
        {
            get
            {
                EnsureInitialized();
                return _displayVersion;
            }
        }

        public static string GitCommit
        {
            get
            {
                EnsureInitialized();
                return _gitCommit;
            }
        }

        public static string BuildTime
        {
            get
            {
                EnsureInitialized();
                return _buildTime;
            }
        }

        public static string DotNetRuntime => RuntimeInformation.FrameworkDescription;

        public static string OperatingSystem => RuntimeInformation.OSDescription;

        public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

        public static bool IsPortable => ConfigService.IsPortableMode;

        public static string DataPath => ConfigService.DirPath;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            try
            {
                var asm = typeof(AppInfo).Assembly;
                var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
                {
                    string raw = infoAttr.InformationalVersion.Trim();
                    int plusIdx = raw.IndexOf('+');
                    _informationalVersion = plusIdx >= 0 ? raw.Substring(0, plusIdx) : raw;
                }

                var asmVer = asm.GetName().Version;
                if (asmVer != null)
                {
                    _version = $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";
                }

                // 若 InformationalVersion 为有效语义版本（例如 1.3.1-debug），优先取其前缀
                if (!string.IsNullOrWhiteSpace(_informationalVersion) && _informationalVersion.StartsWith(_version, StringComparison.OrdinalIgnoreCase))
                {
                    _displayVersion = "v" + _informationalVersion;
                }
                else
                {
                    _displayVersion = "v" + _version;
                }

                var metaAttrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
                foreach (var attr in metaAttrs)
                {
                    if (string.Equals(attr.Key, "GitCommit", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(attr.Value)) _gitCommit = attr.Value;
                    }
                    else if (string.Equals(attr.Key, "BuildTime", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(attr.Value)) _buildTime = attr.Value;
                    }
                }
            }
            catch
            {
                _version = "1.0.0";
                _displayVersion = "v1.0.0";
            }
            finally
            {
                _initialized = true;
            }
        }

        /// <summary>
        /// 生成统一格式的诊断与环境信息摘要（便于一键复制反馈）。
        /// </summary>
        public static string GetDiagnosticReport(IEnumerable<IModule> modules)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {ProductName} 系统与环境诊断信息");
            sb.AppendLine($"- 版本 (Version): {DisplayVersion} ({InformationalVersion})");
            sb.AppendLine($"- 提交 (Git Commit): {GitCommit}");
            sb.AppendLine($"- 构建时间 (Build Time): {BuildTime}");
            sb.AppendLine($"- 运行时 (.NET): {DotNetRuntime}");
            sb.AppendLine($"- 操作系统 (OS): {OperatingSystem} ({ProcessArchitecture})");
            sb.AppendLine($"- 部署模式 (Mode): {(IsPortable ? "便携模式 (Portable)" : "漫游模式 (Roaming)")}");
            sb.AppendLine($"- 数据目录 (Data Path): {DataPath}");
            sb.AppendLine();
            sb.AppendLine("## 模块清单 (Modules)");

            if (modules != null)
            {
                int index = 1;
                foreach (var m in modules)
                {
                    string statusStr = m.Status.ToString();
                    sb.AppendLine($"{index++}. **{m.Name}** (`{m.Id}`) - v{m.Version}");
                    sb.AppendLine($"   - 状态: {statusStr} (Running={m.IsRunning})");
                    if (!string.IsNullOrWhiteSpace(m.Description))
                    {
                        sb.AppendLine($"   - 描述: {m.Description}");
                    }
                }
            }
            else
            {
                sb.AppendLine("*(无可用模块信息)*");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
