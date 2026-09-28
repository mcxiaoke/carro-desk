using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Services.Localization;
using System.ServiceProcess;

namespace CarroDesk.Modules.ServiceControl
{
    /// <summary>
    /// 服务控制模块（IPC 设计 §9.5/§11.2，S6）：把「启停指定 Windows 服务」暴露为
    /// services.status / services.start / services.stop 三个能力。
    /// 安全模型（双层）：
    ///  1. 允许清单（模块配置 AllowedServices）→ 参数枚举白名单，校验在内核；
    ///  2. 服务对象 DACL（一次性管理员授予）→ 真正的内核级边界，配置被篡改也拦得住。
    /// 口令策略按服务配置（requiresPin，缺省 true）：通过 RequiresPinFor 动态判定，
    /// PinGuard 失败限流与审计照常生效（SERVICE-CONTROL-PLAN §2）。
    /// 无托盘 UI；能力清单在启动时收集，配置重载时 App 重建注册表。
    /// </summary>
    public sealed class ServiceControlModule : ModuleBase<ServiceControlConfig>, ICommandProvider
    {
        public const string ModuleId = "Services";

        private const int DefaultOperationTimeoutSeconds = 15;
        private readonly IServiceControlAdapter _adapter;

        public ServiceControlModule() : this(new WindowsServiceControlAdapter())
        {
        }

        public ServiceControlModule(IServiceControlAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public override string Id { get { return ModuleId; } }
        public override string Name { get { return Loc.T("Tray.ServiceControlTitle", "服务控制 (Services)"); } }
        public override string Description { get { return Loc.T("Tray.ServiceControlDesc", "远程启停已授权的 Windows 服务"); } }
        public override int Order { get { return 900; } }

        protected override void OnStart()
        {
        }

        protected override void OnStop()
        {
        }

        public IEnumerable<CommandDescriptor> GetCommands()
        {
            var allowed = GetAllowedServices();
            yield return BuildStatus(allowed);
            yield return BuildStart(allowed);
            yield return BuildStop(allowed);
        }

        private List<ServiceAllowlistEntry> GetAllowedServices()
        {
            var config = Config;
            if (config == null || config.AllowedServices == null) return new List<ServiceAllowlistEntry>();
            return config.AllowedServices
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
                .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new ServiceAllowlistEntry
                {
                    Name = g.Key,
                    Desc = g.Select(e => e.Desc).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    RequiresPin = g.First().RequiresPin
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static ServiceAllowlistEntry FindEntry(List<ServiceAllowlistEntry> entries, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return entries.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>服务名（仅限授权清单）：GameViewerService=UUYC 远程控制；…（desc 流入工具 schema，供 AI 映射自然语言）</summary>
        private static string BuildNameParamDescription(List<ServiceAllowlistEntry> entries)
        {
            var withDesc = entries.Where(e => !string.IsNullOrWhiteSpace(e.Desc)).ToList();
            if (withDesc.Count == 0)
                return "服务名（仅限已授权清单）";
            return "服务名（仅限已授权清单）：" + string.Join("；", withDesc.Select(e => e.Name + "=" + e.Desc));
        }

        private static CommandResult Success(object data)
        {
            return CommandResult.Success(data);
        }

        private static CommandResult Fail(int code, string message)
        {
            return CommandResult.Fail(code, message);
        }

        private CommandDescriptor BuildStatus(List<ServiceAllowlistEntry> allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.status",
                Summary = allowed.Count == 0
                    ? "查询授权服务的状态（当前允许清单为空，先在 config.json 配置 Services.AllowedServices）"
                    : "查询服务的状态：不带参数列出全部授权服务（含 desc 与口令策略），带 name 查单个",
                Risk = CommandRisk.ReadOnly,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = false,
                        AllowedValues = allowed.Select(e => e.Name).ToArray(),
                        Description = BuildNameParamDescription(allowed)
                    }
                },
                Handler = r => StatusHandler(r, allowed)
            };
        }

        private CommandDescriptor BuildStart(List<ServiceAllowlistEntry> allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.start",
                Summary = "启动一个已授权的 Windows 服务",
                Risk = CommandRisk.Privileged,
                RequiresPin = false,
                RequiresPinFor = r => PinPolicyFor(r, allowed),
                TimeoutMs = 30000,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = true,
                        AllowedValues = allowed.Select(e => e.Name).ToArray(),
                        Description = BuildNameParamDescription(allowed)
                    }
                },
                Handler = r => StartHandler(r, allowed)
            };
        }

        private CommandDescriptor BuildStop(List<ServiceAllowlistEntry> allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.stop",
                Summary = "停止一个已授权的 Windows 服务",
                Risk = CommandRisk.Privileged,
                RequiresPin = false,
                RequiresPinFor = r => PinPolicyFor(r, allowed),
                TimeoutMs = 30000,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = true,
                        AllowedValues = allowed.Select(e => e.Name).ToArray(),
                        Description = BuildNameParamDescription(allowed)
                    }
                },
                Handler = r => StopHandler(r, allowed)
            };
        }

        /// <summary>按服务口令策略：查得到条目用其 requiresPin，查不到（fail-safe）一律要求口令。</summary>
        private static bool PinPolicyFor(CommandRequest request, List<ServiceAllowlistEntry> allowed)
        {
            var entry = FindEntry(allowed, GetRequestedName(request));
            return entry == null || entry.RequiresPin;
        }

        private static string GetRequestedName(CommandRequest request)
        {
            object value;
            if (request != null && request.Params != null && request.Params.TryGetValue("name", out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return null;
        }

        private CommandResult StatusHandler(CommandRequest request, List<ServiceAllowlistEntry> allowed)
        {
            var requestedName = GetRequestedName(request);
            if (requestedName != null)
            {
                var entry = FindEntry(allowed, requestedName);
                if (entry == null)
                    return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(requestedName, allowed));
                return Success(new
                {
                    name = entry.Name,
                    desc = entry.Desc,
                    requiresPin = entry.RequiresPin,
                    status = DescribeStatus(_adapter.GetStatus(entry.Name))
                });
            }

            var items = allowed.Select(e => new
            {
                name = e.Name,
                desc = e.Desc,
                requiresPin = e.RequiresPin,
                status = DescribeStatus(_adapter.GetStatus(e.Name))
            }).ToList();
            return Success(new { allowlist = allowed.Select(e => e.Name).ToList(), services = items });
        }

        private CommandResult StartHandler(CommandRequest request, List<ServiceAllowlistEntry> allowed)
        {
            var name = GetRequestedName(request);
            var entry = FindEntry(allowed, name);
            if (entry == null)
                return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(name, allowed));

            try
            {
                var status = _adapter.Start(entry.Name, TimeSpan.FromSeconds(DefaultOperationTimeoutSeconds));
                return Success(new { name = entry.Name, status = status.ToString(), action = "start" });
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                return Fail(CommandErrorCodes.Timeout, "timeout starting '" + entry.Name + "': " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                if (IsAccessDenied(ex))
                    return Fail(CommandErrorCodes.Internal,
                        "access denied for '" + entry.Name + "': grant control via docs/remote-admin/grant-service-control.ps1 (IPC §11.2)");
                return Fail(CommandErrorCodes.InvalidParams, "service '" + entry.Name + "' unavailable: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(CommandErrorCodes.Internal, "start failed for '" + entry.Name + "': " + ex.Message);
            }
        }

        private CommandResult StopHandler(CommandRequest request, List<ServiceAllowlistEntry> allowed)
        {
            var name = GetRequestedName(request);
            var entry = FindEntry(allowed, name);
            if (entry == null)
                return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(name, allowed));

            try
            {
                var status = _adapter.Stop(entry.Name, TimeSpan.FromSeconds(DefaultOperationTimeoutSeconds));
                return Success(new { name = entry.Name, status = status.ToString(), action = "stop" });
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                return Fail(CommandErrorCodes.Timeout, "timeout stopping '" + entry.Name + "': " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                if (IsAccessDenied(ex))
                    return Fail(CommandErrorCodes.Internal,
                        "access denied for '" + entry.Name + "': grant control via docs/remote-admin/grant-service-control.ps1 (IPC §11.2)");
                return Fail(CommandErrorCodes.InvalidParams, "service '" + entry.Name + "' unavailable: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(CommandErrorCodes.Internal, "stop failed for '" + entry.Name + "': " + ex.Message);
            }
        }

        private static string NotAllowedMessage(string name, List<ServiceAllowlistEntry> allowed)
        {
            return allowed.Count == 0
                ? "services allowlist is empty; configure Services.AllowedServices in config.json first"
                : "service '" + name + "' is not in the allowlist: " + string.Join(", ", allowed.Select(e => e.Name));
        }

        private static string DescribeStatus(ServiceControllerStatus? status)
        {
            return status.HasValue ? status.Value.ToString() : "NotFound";
        }

        private static bool IsAccessDenied(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                var win32 = current as Win32Exception;
                if (win32 != null && win32.NativeErrorCode == 5) return true;
                current = current.InnerException;
            }
            return false;
        }
    }
}
