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
    }
}
