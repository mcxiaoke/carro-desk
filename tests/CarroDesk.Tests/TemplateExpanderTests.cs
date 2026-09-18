using System;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class TemplateExpanderTests
    {
        [TestMethod]
        public void Expand_EmptyOrNull_ReturnsOriginal()
        {
            Assert.IsNull(TemplateExpander.Expand(null, null));
            Assert.AreEqual("", TemplateExpander.Expand("", null));
            Assert.AreEqual("plain text without templates", TemplateExpander.Expand("plain text without templates", null));
        }

        [TestMethod]
        public void Expand_StandardTokens_ReplacesCorrectly()
        {
            var task = new TaskDefinition { Name = "BackupTask" };

            string template = "Run {{task}} at {{date}}";
            string expanded = TemplateExpander.Expand(template, task);

            string expectedDate = DateTime.Now.ToString("yyyy-MM-dd");
            Assert.AreEqual($"Run BackupTask at {expectedDate}", expanded);
        }

        [TestMethod]
        public void Expand_TimeAndDateTimeTokens_ReplacesCorrectly()
        {
            var task = new TaskDefinition { Name = "TestTask" };

            string timeExpanded = TemplateExpander.Expand("{{time}}", task);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(timeExpanded, @"^\d{2}-\d{2}-\d{2}$"));

            string dateTimeExpanded = TemplateExpander.Expand("{{datetime}}", task);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(dateTimeExpanded, @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$"));
        }

        [TestMethod]
        public void Expand_ConfigPaths_ReplacesSystemPaths()
        {
            string expanded = TemplateExpander.Expand("Scripts={{scripts}} Logs={{logs}} Dir={{dir}}", null);

            Assert.IsTrue(expanded.Contains(ConfigService.ScriptsDirPath));
            Assert.IsTrue(expanded.Contains(ConfigService.LogsDirPath));
            Assert.IsTrue(expanded.Contains(ConfigService.DirPath));
        }

        [TestMethod]
        public void Expand_CustomDateTimeFormat_FormatsCorrectly()
        {
            string expanded = TemplateExpander.Expand("File_{{yyyyMMdd}}.txt", null);
            string expected = $"File_{DateTime.Now:yyyyMMdd}.txt";
            Assert.AreEqual(expected, expanded);

            string yearMonth = TemplateExpander.Expand("Archive_{{yyyy-MM}}", null);
            Assert.AreEqual($"Archive_{DateTime.Now:yyyy-MM}", yearMonth);
        }

        [TestMethod]
        public void Expand_EnvironmentVariableFallback_ResolvesExistingEnvVars()
        {
            string testVarName = "CARRO_TEST_ENV_KEY";
            string testVarVal = "Carro_Value_456";
            Environment.SetEnvironmentVariable(testVarName, testVarVal);

            try
            {
                string input = "Env: {{" + testVarName + "}}";
                string result = TemplateExpander.Expand(input, null);
                Assert.AreEqual("Env: Carro_Value_456", result);
            }
            finally
            {
                Environment.SetEnvironmentVariable(testVarName, null);
            }
        }

        [TestMethod]
        public void Expand_UnknownVariable_PreservesOriginalToken()
        {
            string input = "Unknown: {{non_existent_token_xyz_123}}";
            string result = TemplateExpander.Expand(input, null);
            Assert.AreEqual("Unknown: {{non_existent_token_xyz_123}}", result);
        }
    }
}
