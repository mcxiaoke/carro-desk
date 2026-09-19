using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 本地化契约测试（P1-11 / P2-1）。
    ///
    /// 保护三件事：
    ///   1) 两种语言的键集合完全一致（缺 key 时会静默回退到中文，肉眼极难发现）；
    ///   2) 同一 key 在两种语言中的格式化占位符一致（否则某一语言会显示字面量 {0}）；
    ///   3) 代码里引用的每个 key 都真实存在于语言包中。
    ///
    /// 直接读取源码树中的语言包，避免为测试在 I18nService 上开放诊断接口。
    /// </summary>
    [TestClass]
    public class LocalizationContractTests
    {
        private static readonly Regex KeyRefPattern =
            new Regex(@"Loc\.(?:T|Format)\s*\(\s*""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

        private static readonly Regex PlaceholderPattern =
            new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                dir = dir.Parent;
            }

            Assert.IsNotNull(dir, "未能定位仓库根目录（向上找不到 Directory.Build.props）");
            string src = Path.Combine(dir.FullName, "src");
            Assert.IsTrue(Directory.Exists(src), "未找到 src 目录: " + src);
            return src;
        }

        private static Dictionary<string, string> LoadLocale(string code)
        {
            string path = Path.Combine(FindSourceRoot(), "Assets", "Locales", code + ".json");
            Assert.IsTrue(File.Exists(path), "语言包不存在: " + path);

            var obj = JObject.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(obj, "", result);
            return result;
        }

        private static void Flatten(JObject obj, string prefix, Dictionary<string, string> target)
        {
            foreach (var prop in obj.Properties())
            {
                if (string.Equals(prop.Name, "_meta", StringComparison.OrdinalIgnoreCase)) continue;

                string key = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "." + prop.Name;
                var child = prop.Value as JObject;
                if (child != null)
                {
                    Flatten(child, key, target);
                }
                else
                {
                    target[key] = prop.Value == null ? string.Empty : prop.Value.ToString();
                }
            }
        }

        [TestMethod]
        public void Locales_BothLanguages_DefineExactlyTheSameKeySet()
        {
            var zh = LoadLocale("zh-CN");
            var en = LoadLocale("en-US");

            var onlyZh = zh.Keys.Except(en.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
            var onlyEn = en.Keys.Except(zh.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();

            Assert.AreEqual(0, onlyZh.Count, "仅中文语言包存在的 key: " + string.Join(", ", onlyZh));
            Assert.AreEqual(0, onlyEn.Count, "仅英文语言包存在的 key: " + string.Join(", ", onlyEn));
        }

        [TestMethod]
        public void Locales_Placeholders_MatchAcrossLanguages()
        {
            var zh = LoadLocale("zh-CN");
            var en = LoadLocale("en-US");

            var mismatches = new List<string>();
            foreach (var key in zh.Keys)
            {
                string zhValue;
                if (!en.TryGetValue(key, out var enValue)) continue;
                zhValue = zh[key];

                var zhSlots = PlaceholderPattern.Matches(zhValue).Cast<Match>().Select(m => m.Groups[1].Value)
                    .Distinct().OrderBy(v => v).ToList();
                var enSlots = PlaceholderPattern.Matches(enValue).Cast<Match>().Select(m => m.Groups[1].Value)
                    .Distinct().OrderBy(v => v).ToList();

                if (!zhSlots.SequenceEqual(enSlots))
                {
                    mismatches.Add(string.Format("{0}: zh=[{1}] en=[{2}]", key,
                        string.Join(",", zhSlots), string.Join(",", enSlots)));
                }
            }

            Assert.AreEqual(0, mismatches.Count,
                "占位符不一致（会导致某一语言显示字面量 {0}）:\n" + string.Join("\n", mismatches));
        }

        [TestMethod]
        public void Locales_EveryKeyReferencedInCode_IsDefinedInBothLanguages()
        {
            var zh = LoadLocale("zh-CN");
            var en = LoadLocale("en-US");

            var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(FindSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                foreach (Match m in KeyRefPattern.Matches(File.ReadAllText(file)))
                {
                    referenced.Add(m.Groups[1].Value);
                }
            }

            Assert.IsTrue(referenced.Count > 0, "未扫描到任何 Loc.T/Loc.Format 调用，正则或目录结构可能已变化");

            var missingZh = referenced.Where(k => !zh.ContainsKey(k)).ToList();
            var missingEn = referenced.Where(k => !en.ContainsKey(k)).ToList();

            Assert.AreEqual(0, missingZh.Count,
                "代码引用了但中文语言包缺失的 key（会静默回退到硬编码兜底文案）: " + string.Join(", ", missingZh));
            Assert.AreEqual(0, missingEn.Count,
                "代码引用了但英文语言包缺失的 key（英文界面会显示中文）: " + string.Join(", ", missingEn));
        }
    }
}
