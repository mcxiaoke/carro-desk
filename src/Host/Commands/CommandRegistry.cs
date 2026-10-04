using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core.Commands;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 能力注册表 = 白名单本体（IPC 设计 §4.3 第 2 步）。线程安全；
    /// 能力名大小写不敏感唯一，重复注册/缺失 Handler 视为装配期编程错误并抛出。
    /// </summary>
    public sealed class CommandRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, CommandDescriptor> _commands =
            new Dictionary<string, CommandDescriptor>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 整体重建（宿主启动时经 ModuleManager.CollectCommands() 收集后调用）。
        /// 注册失败会整体抛出，由调用方决定降级策略。
        /// </summary>
        public void Rebuild(IEnumerable<KeyValuePair<string, CommandDescriptor>> collected)
        {
            if (collected == null) return;
            lock (_lock)
            {
                // 先在临时字典中构建，全部成功后再整体替换：避免中途抛异常留下"半注册"状态，
                // 使调用方捕获异常后的降级语义（能力集合确定为空）名副其实。
                var staged = new Dictionary<string, CommandDescriptor>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in collected)
                {
                    RegisterCore(staged, pair.Key, pair.Value);
                }
                _commands.Clear();
                foreach (var kvp in staged)
                {
                    _commands[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>注册单条能力。宿主自身能力（host.*）也走这里，moduleId 传 "host"。</summary>
        public void Register(string moduleId, CommandDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            lock (_lock)
            {
                RegisterCore(_commands, moduleId, descriptor);
            }
        }

        private static void RegisterCore(Dictionary<string, CommandDescriptor> target, string moduleId, CommandDescriptor descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Name))
                throw new ArgumentException("能力名不能为空", nameof(descriptor));
            // 两个执行体至少要有一个：只有 CancellableHandler 的能力同样是完整的能力定义
            if (descriptor.Handler == null && descriptor.CancellableHandler == null)
                throw new ArgumentException("能力 '" + descriptor.Name + "' 缺少 Handler/CancellableHandler", nameof(descriptor));

            descriptor.ModuleId = moduleId ?? descriptor.ModuleId ?? string.Empty;
            var key = descriptor.Name.Trim();
            if (target.ContainsKey(key))
                throw new InvalidOperationException("能力名 '" + key + "' 重复注册（模块 '" + descriptor.ModuleId + "'）");

            descriptor.Name = key;
            target[key] = descriptor;
        }

        public bool TryGet(string name, out CommandDescriptor descriptor)
        {
            descriptor = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            lock (_lock)
            {
                return _commands.TryGetValue(name.Trim(), out descriptor);
            }
        }

        /// <summary>能力快照，按名称排序（供 host.capabilities.list / MCP 工具表导出）。</summary>
        public List<CommandDescriptor> Snapshot()
        {
            lock (_lock)
            {
                return _commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _commands.Count; } }
        }
    }
}
