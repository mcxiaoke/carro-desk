using System;
using System.IO;
using System.Text;

namespace CarroDesk.Common
{
    public static class AtomicFile
    {
        public static void WriteAllText(string destinationPath, string content, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));
            if (content == null) content = string.Empty;
            if (encoding == null) encoding = Encoding.UTF8;

            string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = Path.Combine(dir, Path.GetFileName(destinationPath) + ".tmp." + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(tempFile, content, encoding);

                if (File.Exists(destinationPath))
                {
                    try
                    {
                        File.Replace(tempFile, destinationPath, null, true);
                    }
                    catch
                    {
                        string bakFile = Path.Combine(dir, Path.GetFileName(destinationPath) + ".bak." + Guid.NewGuid().ToString("N"));
                        File.Move(destinationPath, bakFile);
                        File.Move(tempFile, destinationPath);
                        try { File.Delete(bakFile); } catch { }
                    }
                }
                else
                {
                    File.Move(tempFile, destinationPath);
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>
        /// 清理本类遗留的孤儿临时文件。
        ///
        /// 正常路径下临时文件总会被 finally 删除；但进程被强杀（任务管理器结束、崩溃、断电）
        /// 时 finally 不会执行，会在数据目录留下 "&lt;名字&gt;.tmp.&lt;32位十六进制&gt;" 残file。
        /// 这里只匹配该精确形态，且只删除超过 <paramref name="maxAge"/> 的文件，
        /// 以免误删正在写入的临时文件。
        /// </summary>
        public static int CleanupStaleTempFiles(string directory, TimeSpan maxAge)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

            int removed = 0;
            try
            {
                DateTime cutoff = DateTime.UtcNow - maxAge;
                foreach (var path in Directory.GetFiles(directory, "*.tmp.*"))
                {
                    try
                    {
                        if (!IsOwnTempFile(path)) continue;
                        if (File.GetLastWriteTimeUtc(path) > cutoff) continue;

                        File.Delete(path);
                        removed++;
                    }
                    catch
                    {
                        // 单个文件删除失败（被占用等）不影响其余清理
                    }
                }
            }
            catch
            {
                // 目录不可读时静默跳过：清理属尽力而为，不得影响启动
            }
            return removed;
        }

        /// <summary>判断文件名是否为 WriteAllText 生成的 "xxx.tmp.&lt;32位十六进制&gt;" 形态。</summary>
        private static bool IsOwnTempFile(string path)
        {
            string name = Path.GetFileName(path);
            const string marker = ".tmp.";
            int idx = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return false;

            string suffix = name.Substring(idx + marker.Length);
            if (suffix.Length != 32) return false;

            for (int i = 0; i < suffix.Length; i++)
            {
                char c = suffix[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
