using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Services;
using Newtonsoft.Json;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 宿主自身能力（IPC 设计 §4.2 首批 host.* 行）：hello / guide / status / modules.list / capabilities.list。
    /// moduleId = "host"；App 接线处的模块状态查询对 "host" 恒放行（宿主自身不经过模块生命周期）。
    /// host 同时以伪模块形式出现在 modules.list / status 的模块清单首项（isHost=true），
    /// 保证客户端按 moduleId 反查不会落空；真实模块条目带 capabilityCount，
    /// 「暂无能力」的模块（分批开放中）由此可见。
    /// </summary>
    public static class HostCommands
    {
        public const string ModuleId = "host";
        public const string ProtocolVersion = "1.0";
        private const string ManualResourceSuffix = "ai-agent-manual.md";

        public static void Install(CommandRegistry registry, ModuleManager modules)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.Register(ModuleId, Hello(registry));
            registry.Register(ModuleId, Guide());
            registry.Register(ModuleId, Status(registry, modules));
            registry.Register(ModuleId, ModulesList(registry, modules));
            registry.Register(ModuleId, CapabilitiesList(registry));
        }

        private static CommandDescriptor Hello(CommandRegistry registry)
        {
            return new CommandDescriptor
            {
                Name = "host.hello",
                Summary = "握手：协议版本、宿主版本、能力表哈希（客户端据此缓存能力表）",
                Risk = CommandRisk.ReadOnly,
                Handler = _ => CommandResult.Success(new
                {
                    protocolVersion = ProtocolVersion,
                    hostVersion = HostVersion(),
                    capabilitiesHash = ComputeHash(registry.Snapshot()),
                    requiresToken = false
                })
            };
        }

        private static CommandDescriptor Guide()
        {
            return new CommandDescriptor
            {
                Name = "host.guide",
                Summary = "读取 AI 助手详细使用手册（Markdown）：接入方式、口令约定、错误码应对、能力边界",
                Risk = CommandRisk.ReadOnly,
                Handler = _ =>
                {
                    var manual = ReadEmbeddedManual();
                    if (manual == null)
                        return CommandResult.Fail(CommandErrorCodes.Internal, "manual resource missing in host assembly");
                    return CommandResult.Success(manual);
                }
            };
        }

        private static CommandDescriptor Status(CommandRegistry registry, ModuleManager modules)
        {
            return new CommandDescriptor
            {
                Name = "host.status",
                Summary = "宿主运行状态：版本、进程、运行时长、各模块状态摘要",
                Risk = CommandRisk.ReadOnly,
                Handler = _ =>
                {
                    var process = Process.GetCurrentProcess();
                    var uptimeMs = (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
                    var counts = CapabilityCounts(registry);
                    return CommandResult.Success(new
                    {
                        hostVersion = HostVersion(),
                        processId = process.Id,
                        startedAt = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        uptimeMs = uptimeMs,
                        capabilityCount = registry.Count,
                        modules = BuildModuleSummary(registry, counts, modules)
                    });
                }
            };
        }

        private static CommandDescriptor ModulesList(CommandRegistry registry, ModuleManager modules)
        {
            return new CommandDescriptor
            {
                Name = "host.modules.list",
                Summary = "模块清单：id、名称、状态、是否运行、能力数（host 伪模块在首项）",
                Risk = CommandRisk.ReadOnly,
                Handler = _ => CommandResult.Success(BuildModuleSummary(registry, CapabilityCounts(registry), modules,
                    detailed: true).ToList())
            };
        }

        /// <summary>
        /// 模块清单：首项为 host 伪模块（isHost=true），其后为真实模块。
        /// capabilityCount = 该模块暴露的能力数（0 = 分批开放中，尚无能力）。
        /// </summary>
        private static IEnumerable<object> BuildModuleSummary(
            CommandRegistry registry, Dictionary<string, int> counts, ModuleManager modules, bool detailed = false)
        {
            yield return detailed
                ? (object)new
                {
                    id = ModuleId,
                    name = "CarroDesk Host",
                    status = ModuleStatus.Running.ToString(),
                    isRunning = true,
                    isHost = true,
                    capabilityCount = counts.TryGetValue(ModuleId, out var hostCount) ? hostCount : 0
                }
                : new { id = ModuleId, status = ModuleStatus.Running.ToString(), isHost = true };

            if (modules == null) yield break;
            foreach (var m in modules.Modules)
            {
                if (detailed)
                {
                    yield return new
                    {
                        id = m.Id,
                        name = m.Name,
                        status = m.Status.ToString(),
                        isRunning = m.IsRunning,
                        isHost = false,
                        capabilityCount = counts.TryGetValue(m.Id, out var count) ? count : 0
                    };
                }
                else
                {
                    yield return new { id = m.Id, status = m.Status.ToString() };
                }
            }
        }

        private static Dictionary<string, int> CapabilityCounts(CommandRegistry registry)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in registry.Snapshot().GroupBy(c => c.ModuleId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                counts[group.Key] = group.Count();
            }
            return counts;
        }

        private static CommandDescriptor CapabilitiesList(CommandRegistry registry)
        {
            return new CommandDescriptor
            {
                Name = "host.capabilities.list",
                Summary = "能力表 + 参数 schema（供客户端缓存 / MCP 工具映射）",
                Risk = CommandRisk.ReadOnly,
                Handler = _ =>
                {
                    var snapshot = registry.Snapshot();
                    return CommandResult.Success(new
                    {
                        hash = ComputeHash(snapshot),
                        capabilities = snapshot.Select(Describe).ToList()
                    });
                }
            };
        }

        private static object Describe(CommandDescriptor c)
        {
            var parameters = c.Params ?? (IReadOnlyList<CommandParam>)new CommandParam[0];
            return new
            {
                name = c.Name,
                moduleId = c.ModuleId,
                summary = c.Summary,
                risk = c.Risk.ToString(),
                requiresPin = c.RequiresPin,
                timeoutMs = c.TimeoutMs,
                parameters = parameters.Select(p => new
                {
                    name = p.Name,
                    type = string.IsNullOrEmpty(p.Type) ? "string" : p.Type,
                    required = p.Required,
                    description = p.Description,
                    allowedValues = p.AllowedValues
                }).ToList()
            };
        }

        /// <summary>读取嵌入的 AI 助手手册（docs/AI-AGENT-MANUAL.md，构建时嵌入主程序）。</summary>
        internal static string ReadEmbeddedManual()
        {
            try
            {
                var assembly = typeof(HostCommands).Assembly;
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(ManualResourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using (var stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string HostVersion()
        {
            try
            {
                var version = typeof(HostCommands).Assembly.GetName().Version;
                return version != null ? version.ToString() : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>能力表指纹：客户端缓存能力表，哈希变化时重取（IPC 设计 §5.2 握手）。</summary>
        public static string ComputeHash(IEnumerable<CommandDescriptor> snapshot)
        {
            string raw;
            try
            {
                raw = JsonConvert.SerializeObject(
                    (snapshot ?? Enumerable.Empty<CommandDescriptor>())
                        .Select(c => new
                        {
                            c.Name,
                            c.ModuleId,
                            c.Summary,
                            risk = c.Risk.ToString(),
                            c.RequiresPin,
                            c.TimeoutMs
                        })
                        .ToList(),
                    Formatting.None);
            }
            catch
            {
                raw = "<unserializable>";
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return "sha256:" + sb;
            }
        }
    }
}
