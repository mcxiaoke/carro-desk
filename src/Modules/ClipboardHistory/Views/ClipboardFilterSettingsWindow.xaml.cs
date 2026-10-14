using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.ClipboardHistory.Views
{
    /// <summary>
    /// 敏感信息过滤设置窗口（独立于历史设置窗口）。
    ///
    /// 规则在这里编辑，但直接随 ClipboardHistoryConfig 一起落盘：
    /// 保存统一走 IConfigManager.SaveModuleConfig("ClipboardHistory", ...)，
    /// 随后由模块 OnConfigReloaded 重建过滤规则集。
    ///
    /// 编辑期间只在本地 _rules 上操作，绝不直接改模块持有的配置实例——
    /// 否则用户点“取消”也会把规则写进内存态，与磁盘状态脱节。
    /// </summary>
    public partial class ClipboardFilterSettingsWindow : Window
    {
        private readonly ClipboardHistoryModule _module;
        private readonly IConfigManager _configManager;
        private readonly List<ClipboardFilterRule> _rules = new List<ClipboardFilterRule>();

        public ClipboardFilterSettingsWindow(ClipboardHistoryModule module = null, IConfigManager configManager = null)
        {
            _module = module;
            _configManager = configManager;

            InitializeComponent();
            LoadCurrentSettings();
        }

        private ClipboardHistoryConfig ResolveConfig()
        {
            return _module?.Config
                   ?? (_configManager != null
                       ? _configManager.GetModuleConfig<ClipboardHistoryConfig>("ClipboardHistory")
                       : null)
                   ?? new ClipboardHistoryConfig();
        }

        private void LoadCurrentSettings()
        {
            var config = ResolveConfig();

            ChkFilterEnabled.IsChecked = config.FilterEnabled;

            _rules.Clear();
            if (config.FilterRules != null)
            {
                foreach (var rule in config.FilterRules)
                {
                    if (rule != null) _rules.Add(rule.Clone());
                }
            }

            RefreshList();
        }

        private ClipboardFilterRule SelectedRule
        {
            get { return LstRules.SelectedItem as ClipboardFilterRule; }
        }

        private void RefreshList(int selectIndex = -1)
        {
            // 先解绑再重绑，保证选中项刷新后重新加载到编辑区
            LstRules.ItemsSource = null;
            LstRules.ItemsSource = _rules;
            if (selectIndex >= 0 && selectIndex < _rules.Count)
            {
                LstRules.SelectedIndex = selectIndex;
            }

            UpdateRuleCount();
        }

        private void UpdateRuleCount()
        {
            int activeCount = _rules.Count(x => x.Enabled);
            TxtRuleCount.Text = Loc.T("Clipboard.FilterRuleCount",
                "共 {0} 条规则（{1} 条已启用），上限 {2} 条",
                _rules.Count, activeCount, ClipboardHistoryConfig.MaxFilterRules);
        }

        private void LstRules_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var rule = SelectedRule;
            if (rule == null)
            {
                TxtPattern.Text = string.Empty;
                ChkIgnoreCase.IsChecked = true;
                ChkRuleEnabled.IsChecked = true;
                BtnUpdate.IsEnabled = false;
                BtnDelete.IsEnabled = false;
                return;
            }

            TxtPattern.Text = rule.Pattern;
            ChkIgnoreCase.IsChecked = rule.IgnoreCase;
            ChkRuleEnabled.IsChecked = rule.Enabled;
            BtnUpdate.IsEnabled = true;
            BtnDelete.IsEnabled = true;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            string pattern;
            if (!TryReadPattern(out pattern)) return;

            if (_rules.Count >= ClipboardHistoryConfig.MaxFilterRules)
            {
                ShowWarning(Loc.T("Clipboard.FilterRuleLimit",
                    "过滤规则最多 {0} 条，请先删除不再使用的规则。", ClipboardHistoryConfig.MaxFilterRules));
                return;
            }

            if (FindDuplicate(pattern, null) >= 0)
            {
                ShowWarning(Loc.T("Clipboard.FilterRuleDuplicate", "该关键字已存在于规则列表中。"));
                return;
            }

            _rules.Add(new ClipboardFilterRule
            {
                Pattern = pattern,
                IgnoreCase = ChkIgnoreCase.IsChecked != false,
                Enabled = ChkRuleEnabled.IsChecked != false
            });

            RefreshList(_rules.Count - 1);
            TxtPattern.Text = string.Empty;
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            var rule = SelectedRule;
            if (rule == null) return;

            string pattern;
            if (!TryReadPattern(out pattern)) return;

            int duplicateIndex = FindDuplicate(pattern, rule);
            if (duplicateIndex >= 0)
            {
                ShowWarning(Loc.T("Clipboard.FilterRuleDuplicate", "该关键字已存在于规则列表中。"));
                return;
            }

            int index = _rules.IndexOf(rule);
            rule.Pattern = pattern;
            rule.IgnoreCase = ChkIgnoreCase.IsChecked != false;
            rule.Enabled = ChkRuleEnabled.IsChecked != false;

            RefreshList(index);
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var rule = SelectedRule;
            if (rule == null) return;

            int index = _rules.IndexOf(rule);
            _rules.Remove(rule);
            RefreshList(index < _rules.Count ? index : _rules.Count - 1);
        }

        /// <summary>读取并校验编辑区关键字，失败时弹提示并返回 false</summary>
        private bool TryReadPattern(out string pattern)
        {
            pattern = (TxtPattern.Text ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(pattern))
            {
                ShowWarning(Loc.T("Clipboard.FilterPatternRequired", "请输入过滤关键字。"));
                return false;
            }

            if (pattern.Length > ClipboardHistoryConfig.MaxFilterPatternLength)
            {
                ShowWarning(Loc.T("Clipboard.FilterPatternTooLong",
                    "单个关键字最长 {0} 个字符，当前为 {1} 个。",
                    ClipboardHistoryConfig.MaxFilterPatternLength, pattern.Length));
                return false;
            }

            return true;
        }

        /// <summary>查找重复关键字（忽略大小写），exclude 为需要跳过的规则</summary>
        private int FindDuplicate(string pattern, ClipboardFilterRule exclude)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                if (ReferenceEquals(_rules[i], exclude)) continue;
                if (string.Equals(_rules[i].Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var configMgr = _configManager;
            var config = ResolveConfig();
            var previous = config.Clone();

            config.FilterEnabled = ChkFilterEnabled.IsChecked == true;
            config.FilterRules = _rules.Select(x => x.Clone()).ToList();

            if (configMgr == null || !configMgr.SaveModuleConfig("ClipboardHistory", config))
            {
                // 回滚内存态，保证托盘显示的开关与磁盘一致
                config.FilterEnabled = previous.FilterEnabled;
                config.FilterRules = previous.FilterRules.Select(x => x.Clone()).ToList();
                MessageBox.Show(this, Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _module?.OnConfigReloaded();
            _module?.LogInfo("敏感信息过滤配置已保存并生效");

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowWarning(string message)
        {
            MessageBox.Show(this, message, Loc.T("Common.Prompt", "提示"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
