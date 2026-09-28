using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Services;
using Newtonsoft.Json;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 宿主自身能力（IPC 设计 §4.2 首批 host.* 行）：hello / status / modules.list / capabilities.list。
    /// moduleId = "host"；App 接线处的模块状态查询对 "host" 恒放行（宿主自身不经过模块生命周期）。
    /// </summary>
    public static class HostCommands
    {
        public const string ModuleId = "host";
        public const string ProtocolVersion = "1.0";

        public static void Install(CommandRegistry registry, ModuleManager modules)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.Register(ModuleId, Hello(registry));
            registry.Register(ModuleId, Status(registry, modules));
            registry.Register(ModuleId, ModulesList(modules));
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
                    return CommandResult.Success(new
                    {
                        hostVersion = HostVersion(),
                        processId = process.Id,
                        startedAt = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        uptimeMs = uptimeMs,
                        capabilityCount = registry.Count,
                        modules = modules == null
                            ? null
                            : modules.Modules.Select(m => new { id = m.Id, status = m.Status.ToString() }).ToList()
                    });
                }
            };
        }

        private static CommandDescriptor ModulesList(ModuleManager modules)
        {
            return new CommandDescriptor
            {
                Name = "host.modules.list",
                Summary = "模块清单：id、名称、状态、是否运行",
                Risk = CommandRisk.ReadOnly,
                Handler = _ =>
                {
                    var list = modules != null ? modules.Modules : null;
                    return CommandResult.Success((list ?? new List<IModule>()).Select(m => new
                    {
                        id = m.Id,
                        name = m.Name,
                        status = m.Status.ToString(),
                        isRunning = m.IsRunning
                    }).ToList());
                }
            };
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
