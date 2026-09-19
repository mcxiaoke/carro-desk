using CarroDesk.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>命令行引号化 / cmd 转义的纯函数测试（P1-2 的基础防线）。</summary>
    [TestClass]
    public class CommandLineTests
    {
        [TestMethod]
        public void QuoteArgument_WithoutSpecialChars_ReturnsUnchanged()
        {
            Assert.AreEqual("plain.exe", CommandLine.QuoteArgument("plain.exe"));
            Assert.AreEqual(@"C:\a\b\c.bat", CommandLine.QuoteArgument(@"C:\a\b\c.bat"));
        }

        [TestMethod]
        public void QuoteArgument_WithSpace_IsWrapped()
        {
            Assert.AreEqual("\"a b\"", CommandLine.QuoteArgument("a b"));
            Assert.AreEqual("\"C:\\Program Files\\x.bat\"", CommandLine.QuoteArgument(@"C:\Program Files\x.bat"));
        }

        [TestMethod]
        public void QuoteArgument_EmptyOrNull_BecomesEmptyQuotes()
        {
            Assert.AreEqual("\"\"", CommandLine.QuoteArgument(""));
            Assert.AreEqual("\"\"", CommandLine.QuoteArgument(null));
        }

        [TestMethod]
        public void QuoteArgument_TrailingBackslashes_AreDoubled()
        {
            // 结尾反斜杠若不翻倍，会转义掉收尾引号，导致命令行被拆错
            Assert.AreEqual("\"a b\\\\\"", CommandLine.QuoteArgument("a b\\"));
            Assert.AreEqual("\"a b\\\\\\\\\"", CommandLine.QuoteArgument("a b\\\\"));
        }

        [TestMethod]
        public void QuoteArgument_EmbeddedQuote_IsEscaped()
        {
            Assert.AreEqual("\"a\\\"b\"", CommandLine.QuoteArgument("a\"b"));
        }

        [TestMethod]
        public void QuoteArgument_BackslashBeforeQuote_IsDoubled()
        {
            // a\"b -> "a\\\"b"：引号前的反斜杠翻倍，再转义引号本身
            Assert.AreEqual("\"a\\\\\\\"b\"", CommandLine.QuoteArgument("a\\\"b"));
        }

        [TestMethod]
        public void EscapeForCmd_UnquotedMetaChars_AreEscaped()
        {
            Assert.AreEqual("a^&b", CommandLine.EscapeForCmd("a&b"));
            Assert.AreEqual("a ^& b", CommandLine.EscapeForCmd("a & b"));
            Assert.AreEqual("a^|b", CommandLine.EscapeForCmd("a|b"));
            Assert.AreEqual("a^<b", CommandLine.EscapeForCmd("a<b"));
            Assert.AreEqual("a^>b", CommandLine.EscapeForCmd("a>b"));
            Assert.AreEqual("^(a^)", CommandLine.EscapeForCmd("(a)"));
        }

        [TestMethod]
        public void EscapeForCmd_Caret_IsEscaped()
        {
            Assert.AreEqual("a^^b", CommandLine.EscapeForCmd("a^b"));
        }

        [TestMethod]
        public void EscapeForCmd_MetaCharsInsideQuotes_AreLeftAlone()
        {
            // 引号内的 & 对 cmd 本无特殊含义，且引号内的 ^ 是字面量，
            // 若在此处补 ^ 会把 ^ 污染进参数值。
            Assert.AreEqual("\"a&b\"", CommandLine.EscapeForCmd("\"a&b\""));

            // 但引号外的 & 仍必须转义，否则会被 cmd 当作命令分隔符执行
            Assert.AreEqual("--msg \"a b\" ^& c", CommandLine.EscapeForCmd("--msg \"a b\" & c"));
        }

        [TestMethod]
        public void EscapeForCmd_EmptyOrNull_ReturnsAsIs()
        {
            Assert.AreEqual("", CommandLine.EscapeForCmd(""));
            Assert.IsNull(CommandLine.EscapeForCmd(null));
        }

        [TestMethod]
        public void AppendArgs_EmptyArgs_AddsNoTrailingSpace()
        {
            Assert.AreEqual("cmd", CommandLine.AppendArgs("cmd", ""));
            Assert.AreEqual("cmd", CommandLine.AppendArgs("cmd", null));
            Assert.AreEqual("cmd a", CommandLine.AppendArgs("cmd", "a"));
        }
    }
}
