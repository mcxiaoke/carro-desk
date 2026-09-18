using System;
using System.IO;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class TaskConditionEvaluatorTests
    {
        [TestMethod]
        public void ShouldRun_NullOrEmptyConditions_ReturnsTrue()
        {
            string skip;
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(null, out skip));
            Assert.IsNull(skip);

            var taskNoCondition = new TaskDefinition();
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(taskNoCondition, out skip));
            Assert.IsNull(skip);

            var taskEmptyCondition = new TaskDefinition { When = new TaskCondition() };
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(taskEmptyCondition, out skip));
            Assert.IsNull(skip);
        }

        [TestMethod]
        public void ShouldRun_FileExists_EvaluatesCorrectly()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                var task = new TaskDefinition
                {
                    When = new TaskCondition { FileExists = tempFile }
                };

                string skip;
                Assert.IsTrue(TaskConditionEvaluator.ShouldRun(task, out skip));
                Assert.IsNull(skip);

                task.When.FileExists = tempFile + ".non_existent_file.xyz";
                Assert.IsFalse(TaskConditionEvaluator.ShouldRun(task, out skip));
                Assert.IsTrue(skip.Contains("when.fileExists"));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public void ShouldRun_FileNotExists_EvaluatesCorrectly()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                var task = new TaskDefinition
                {
                    When = new TaskCondition { FileNotExists = tempFile }
                };

                string skip;
                // Exists, so ShouldRun is false
                Assert.IsFalse(TaskConditionEvaluator.ShouldRun(task, out skip));
                Assert.IsTrue(skip.Contains("when.fileNotExists"));

                // Does not exist, so ShouldRun is true
                task.When.FileNotExists = tempFile + ".non_existent_file.xyz";
                Assert.IsTrue(TaskConditionEvaluator.ShouldRun(task, out skip));
                Assert.IsNull(skip);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public void ShouldRun_ExpandEnvironmentVariablesInPath_ResolvesCorrectly()
        {
            var task = new TaskDefinition
            {
                When = new TaskCondition { FileExists = "%WINDIR%" }
            };

            string skip;
            Assert.IsTrue(TaskConditionEvaluator.ShouldRun(task, out skip));
            Assert.IsNull(skip);
        }
    }
}
