using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks
{
    public static class TaskConditionEvaluator
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus; // 0 = Offline (Battery), 1 = Online (AC), 255 = Unknown
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        public static bool ShouldRun(TaskDefinition task, out string skipReason)
        {
            skipReason = null;
            if (task == null || task.When == null || !task.When.HasAny()) return true;
            var c = task.When;

            if (c.OnlyIdle)
            {
                try
                {
                    uint idleMs = IdleDetector.GetIdleMilliseconds();
                    // consider idle if > 60s without input
                    if (idleMs < 60 * 1000)
                    {
                        skipReason = "when.onlyIdle: idle " + (idleMs / 1000) + "s < 60s";
                        return false;
                    }
                }
                catch
                {
                    skipReason = "when.onlyIdle: idle state unavailable";
                    return false;
                }
            }

            if (c.AcPower)
            {
                try
                {
                    if (!GetSystemPowerStatus(out var status))
                    {
                        skipReason = "when.acPower: power state unavailable";
                        return false;
                    }
                    if (status.ACLineStatus != 1)
                    {
                        skipReason = "when.acPower: not on AC";
                        return false;
                    }
                }
                catch
                {
                    skipReason = "when.acPower: power state unavailable";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(c.FileExists))
            {
                string p = Expand(c.FileExists);
                bool exists;
                try { exists = PathExistsChecked(p); }
                catch
                {
                    skipReason = "when.fileExists: path state unavailable";
                    return false;
                }
                if (!exists)
                {
                    skipReason = "when.fileExists: not found " + p;
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(c.FileNotExists))
            {
                string p = Expand(c.FileNotExists);
                bool exists;
                try { exists = PathExistsChecked(p); }
                catch
                {
                    skipReason = "when.fileNotExists: path state unavailable";
                    return false;
                }
                if (exists)
                {
                    skipReason = "when.fileNotExists: exists " + p;
                    return false;
                }
            }

            if (c.NetworkAvailable)
            {
                try
                {
                    if (!NetworkInterface.GetIsNetworkAvailable())
                    {
                        skipReason = "when.networkAvailable: no network";
                        return false;
                    }
                }
                catch
                {
                    skipReason = "when.networkAvailable: network state unavailable";
                    return false;
                }
            }

            return true;
        }

        private static bool PathExistsChecked(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string Expand(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
            // also resolve scripts path if bare name
            try
            {
                if (!Path.IsPathRooted(path) && !path.Contains(":"))
                {
                    // try scripts dir
                    string cand = Path.Combine(ConfigService.ScriptsDirPath, path);
                    if (File.Exists(cand) || Directory.Exists(cand)) return cand;
                }
            }
            catch { }
            return path;
        }
    }
}
