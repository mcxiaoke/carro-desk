using System;
using System.Collections.Generic;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class ProcessHelperTests
    {
        [TestMethod]
        public void Normalize_HandlesVariousFormats()
        {
            Assert.AreEqual("chrome.exe", ProcessHelper.Normalize("chrome"));
            Assert.AreEqual("chrome.exe", ProcessHelper.Normalize("chrome.exe"));
            Assert.AreEqual("chrome.exe", ProcessHelper.Normalize("CHROME.EXE"));
            Assert.AreEqual("chrome.exe", ProcessHelper.Normalize("  \"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"  "));
            Assert.AreEqual("eldenring.exe", ProcessHelper.Normalize("'D:/Games/EldenRing/eldenring.exe'"));
            Assert.AreEqual("ffmpeg.exe", ProcessHelper.Normalize("  `ffmpeg`  "));
            Assert.IsNull(ProcessHelper.Normalize(null));
            Assert.IsNull(ProcessHelper.Normalize(""));
            Assert.IsNull(ProcessHelper.Normalize("   "));
        }

        [TestMethod]
        public void NormalizeNameOnly_ExtractsPureName()
        {
            Assert.AreEqual("chrome", ProcessHelper.NormalizeNameOnly("chrome.exe"));
            Assert.AreEqual("chrome", ProcessHelper.NormalizeNameOnly("chrome"));
            Assert.AreEqual("eldenring", ProcessHelper.NormalizeNameOnly("D:\\Games\\EldenRing.exe"));
            Assert.IsNull(ProcessHelper.NormalizeNameOnly(null));
            Assert.IsNull(ProcessHelper.NormalizeNameOnly("   "));
        }

        [TestMethod]
        public void NormalizeList_DeduplicatesAndStandardizes()
        {
            var raw = new List<string>
            {
                "chrome",
                "CHROME.EXE",
                "eldenring.exe",
                "  \"D:\\games\\eldenring.exe\" ",
                "",
                null,
                "blender"
            };

            var normalized = ProcessHelper.NormalizeList(raw);

            Assert.AreEqual(3, normalized.Count);
            Assert.AreEqual("chrome.exe", normalized[0]);
            Assert.AreEqual("eldenring.exe", normalized[1]);
            Assert.AreEqual("blender.exe", normalized[2]);
        }

        [TestMethod]
        public void IsMatch_ComparesFlexibly()
        {
            Assert.IsTrue(ProcessHelper.IsMatch("chrome", "chrome.exe"));
            Assert.IsTrue(ProcessHelper.IsMatch("chrome.exe", "chrome"));
            Assert.IsTrue(ProcessHelper.IsMatch("CHROME.EXE", "chrome"));
            Assert.IsTrue(ProcessHelper.IsMatch("C:\\app\\vlc.exe", "vlc"));
            Assert.IsTrue(ProcessHelper.IsMatch("vlc.exe", "D:\\tools\\vlc.exe"));

            Assert.IsFalse(ProcessHelper.IsMatch("chrome.exe", "firefox.exe"));
            Assert.IsFalse(ProcessHelper.IsMatch("chrome", null));
            Assert.IsFalse(ProcessHelper.IsMatch(null, "chrome"));
        }

        [TestMethod]
        public void ContainsProcess_MatchesRegardlessOfExtension()
        {
            var list = new List<string> { "chrome.exe", "blender.exe", "ffmpeg.exe" };

            Assert.IsTrue(ProcessHelper.ContainsProcess(list, "chrome"));
            Assert.IsTrue(ProcessHelper.ContainsProcess(list, "CHROME.EXE"));
            Assert.IsTrue(ProcessHelper.ContainsProcess(list, "blender"));
            Assert.IsTrue(ProcessHelper.ContainsProcess(list, "FFMPEG.exe"));

            Assert.IsFalse(ProcessHelper.ContainsProcess(list, "notepad"));
            Assert.IsFalse(ProcessHelper.ContainsProcess(list, null));
            Assert.IsFalse(ProcessHelper.ContainsProcess(null, "chrome.exe"));
        }

        [TestMethod]
        public void GetRunningWindowProcesses_ReturnsValidEntries()
        {
            var procs = ProcessHelper.GetRunningWindowProcesses();
            Assert.IsNotNull(procs);

            foreach (var p in procs)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(p.ProcessName));
                Assert.IsTrue(p.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(string.Equals(p.ProcessName, "carrodesk.exe", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(string.Equals(p.ProcessName, "explorer.exe", StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
