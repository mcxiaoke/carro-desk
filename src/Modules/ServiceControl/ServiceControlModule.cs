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
    /// start/stop 统一 RequiresPin（v1.2「口令即确认」，本机与远程一致）。
    /// 无托盘 UI；能力清单在启动时收集，允许清单变更经配置重载生效
    /// （App.ReloadConfig 会重建能力注册表）。
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

        private string[] GetAllowedServices()
        {
            var config = Config;
            if (config == null || config.AllowedServices == null) return new string[0];
            return config.AllowedServices
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static CommandResult Success(object data)
        {
            return CommandResult.Success(data);
        }

        private static CommandResult Fail(int code, string message)
        {
            return CommandResult.Fail(code, message);
        }

        private CommandDescriptor BuildStatus(string[] allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.status",
                Summary = allowed.Length == 0
                    ? "查询授权服务的状态（当前允许清单为空，先配置 Services.AllowedServices）"
                    : "查询服务的状态：不带参数列出全部授权服务，带 name 查单个",
                Risk = CommandRisk.ReadOnly,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = false,
                        AllowedValues = allowed, Description = "服务名（可选）"
                    }
                },
                Handler = r => StatusHandler(r, allowed)
            };
        }

        private CommandDescriptor BuildStart(string[] allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.start",
                Summary = "启动一个已授权的 Windows 服务",
                Risk = CommandRisk.Privileged,
                RequiresPin = true,
                TimeoutMs = 30000,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = true,
                        AllowedValues = allowed, Description = "服务名（仅限已授权清单）"
                    }
                },
                Handler = r => StartHandler(r)
            };
        }

        private CommandDescriptor BuildStop(string[] allowed)
        {
            return new CommandDescriptor
            {
                Name = "services.stop",
                Summary = "停止一个已授权的 Windows 服务",
                Risk = CommandRisk.Privileged,
                RequiresPin = true,
                TimeoutMs = 30000,
                Params = new[]
                {
                    new CommandParam
                    {
                        Name = "name", Type = "string", Required = true,
                        AllowedValues = allowed, Description = "服务名（仅限已授权清单）"
                    }
                },
                Handler = r => StopHandler(r)
            };
        }

        private CommandResult StatusHandler(CommandRequest request, string[] allowed)
        {
            object nameValue = null;
            var hasName = request.Params != null && request.Params.TryGetValue("name", out nameValue) && nameValue != null;
            if (hasName)
            {
                var name = Convert.ToString(nameValue);
                if (!IsAllowed(name, allowed))
                    return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(name, allowed));
                return Success(new { name, status = DescribeStatus(_adapter.GetStatus(name)) });
            }

            var items = allowed.Select(name => new
            {
                name,
                status = DescribeStatus(_adapter.GetStatus(name))
            }).ToList();
            return Success(new { allowlist = allowed, services = items });
        }

        private CommandResult StartHandler(CommandRequest request)
        {
            var name = Convert.ToString(request.Params["name"]);
            var allowed = GetAllowedServices();
            if (!IsAllowed(name, allowed))
                return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(name, allowed));

            try
            {
                var status = _adapter.Start(name, TimeSpan.FromSeconds(DefaultOperationTimeoutSeconds));
                return Success(new { name, status = status.ToString(), action = "start" });
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                return Fail(CommandErrorCodes.Timeout, "timeout starting '" + name + "': " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                if (IsAccessDenied(ex))
                    return Fail(CommandErrorCodes.Internal,
                        "access denied for '" + name + "': grant control via docs/remote-admin/grant-service-control.ps1 (IPC §11.2)");
                return Fail(CommandErrorCodes.InvalidParams, "service '" + name + "' unavailable: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(CommandErrorCodes.Internal, "start failed for '" + name + "': " + ex.Message);
            }
        }

        private CommandResult StopHandler(CommandRequest request)
        {
            var name = Convert.ToString(request.Params["name"]);
            var allowed = GetAllowedServices();
            if (!IsAllowed(name, allowed))
                return Fail(CommandErrorCodes.InvalidParams, NotAllowedMessage(name, allowed));

            try
            {
                var status = _adapter.Stop(name, TimeSpan.FromSeconds(DefaultOperationTimeoutSeconds));
                return Success(new { name, status = status.ToString(), action = "stop" });
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                return Fail(CommandErrorCodes.Timeout, "timeout stopping '" + name + "': " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                if (IsAccessDenied(ex))
                    return Fail(CommandErrorCodes.Internal,
                        "access denied for '" + name + "': grant control via docs/remote-admin/grant-service-control.ps1 (IPC §11.2)");
                return Fail(CommandErrorCodes.InvalidParams, "service '" + name + "' unavailable: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(CommandErrorCodes.Internal, "stop failed for '" + name + "': " + ex.Message);
            }
        }

        private static bool IsAllowed(string name, string[] allowed)
        {
            return !string.IsNullOrWhiteSpace(name)
                && Array.IndexOf(allowed, name.Trim()) >= 0;
        }

        private static string NotAllowedMessage(string name, string[] allowed)
        {
            return allowed.Length == 0
                ? "services allowlist is empty; configure Services.AllowedServices first"
                : "service '" + name + "' is not in the allowlist: " + string.Join(", ", allowed);
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
