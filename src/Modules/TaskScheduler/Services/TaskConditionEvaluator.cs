using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CarroDesk.Core;
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

        /// <summary>旧重载：没有 IIdleService 时退回本地 IdleDetector，保持既有调用方与单测兼容。</summary>
        public static bool ShouldRun(TaskDefinition task, out string skipReason)
        {
            return ShouldRun(task, null, out skipReason);
        }

        /// <summary>
        /// 传入 IIdleService 时与触发器共用同一份 RawIdle（SystemIdleService 1s 扇出），
        /// 消除「触发器判空闲、条件判不空闲」的双数据源窗口（附录 B）。
        /// </summary>
        public static bool ShouldRun(TaskDefinition task, IIdleService idleService, out string skipReason)
        {
            skipReason = null;
            if (task == null || task.When == null || !task.When.HasAny()) return true;
            var c = task.When;

            if (c.OnlyIdle)
            {
                try
                {
                    int threshold = c.OnlyIdleSec > 0 ? c.OnlyIdleSec : 60;
                    double idleSec = idleService != null
                        ? idleService.RawIdle.TotalSeconds
                        : IdleDetector.GetIdleMilliseconds() / 1000.0;
                    if (idleSec < threshold)
                    {
                        skipReason = "when.onlyIdle: idle " + (long)idleSec + "s < " + threshold + "s";
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
