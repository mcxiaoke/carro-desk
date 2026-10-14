using System;
using System.Collections.Generic;
using CarroDesk.Modules.ClipboardHistory.Models;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    /// <summary>
    /// 敏感信息过滤规则集（构造后不可变）。
    ///
    /// 在 <see cref="ClipboardHistoryService.Start"/> / <see cref="ClipboardHistoryService.ApplyConfig"/>
    /// 时随配置一并重建，整个运行期复用，避免每次复制都重新解析规则。
    ///
    /// 匹配一律走 string.IndexOf + Ordinal 族比较：字面语义确定、无正则回溯与超时风险。
    /// 开销可控——规则上限 200（<see cref="ClipboardHistoryConfig.MaxFilterRules"/>）、关键字 ≤256 字符；
    /// 最坏情况（200 条规则全部未命中 × MaxTextLength 100 万字符）仍在几十毫秒量级，
    /// 与既有的预览文本生成同一档次，且实际用法通常只有个位数规则。
    /// </summary>
    public sealed class ClipboardTextFilter
    {
        private static readonly ClipboardTextFilter EmptyInstance =
            new ClipboardTextFilter(new ClipboardFilterRule[0], new string[0], new StringComparison[0]);

        private readonly ClipboardFilterRule[] _rules;
        private readonly string[] _patterns;
        private readonly StringComparison[] _comparisons;

        private ClipboardTextFilter(ClipboardFilterRule[] rules, string[] patterns, StringComparison[] comparisons)
        {
            _rules = rules;
            _patterns = patterns;
            _comparisons = comparisons;
        }

        /// <summary>空规则集（总开关关闭或无规则时使用，匹配恒为 false 且零开销）</summary>
        public static ClipboardTextFilter Empty
        {
            get { return EmptyInstance; }
        }

        public ClipboardTextFilter(IEnumerable<ClipboardFilterRule> rules, bool enabled)
        {
            var accepted = new List<ClipboardFilterRule>();
            if (enabled && rules != null)
            {
                foreach (var rule in rules)
                {
                    // 空关键字与停用规则直接剔除：手工编辑过的坏配置不得让模块异常
                    if (rule == null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
                    accepted.Add(rule);
                }
            }

            _rules = accepted.ToArray();
            _patterns = new string[_rules.Length];
            _comparisons = new StringComparison[_rules.Length];
            for (int i = 0; i < _rules.Length; i++)
            {
                _patterns[i] = _rules[i].Pattern;
                _comparisons[i] = _rules[i].IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            }
        }

        /// <summary>是否存在可生效的规则（关闭时调用方应完全跳过匹配）</summary>
        public bool IsActive
        {
            get { return _rules.Length > 0; }
        }

        public int RuleCount
        {
            get { return _rules.Length; }
        }

        /// <summary>
        /// 判断文本是否命中任一规则。命中时返回对应规则（仅用于日志留痕，不携带原文）。
        /// </summary>
        public bool TryMatch(string text, out ClipboardFilterRule rule)
        {
            rule = null;
            if (_rules.Length == 0 || string.IsNullOrEmpty(text)) return false;

            for (int i = 0; i < _patterns.Length; i++)
            {
                if (text.IndexOf(_patterns[i], _comparisons[i]) >= 0)
                {
                    rule = _rules[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>按模块配置构建规则集（配置为 null 时返回空规则集）</summary>
        public static ClipboardTextFilter FromConfig(ClipboardHistoryConfig config)
        {
            return config == null ? EmptyInstance : new ClipboardTextFilter(config.FilterRules, config.FilterEnabled);
        }
    }
}
