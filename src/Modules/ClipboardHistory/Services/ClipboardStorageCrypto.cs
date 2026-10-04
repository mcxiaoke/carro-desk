using System;
using System.Security.Cryptography;
using System.Text;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    /// <summary>
    /// 加密历史在当前环境无法解密（DPAPI masterkey 丢失、换 Windows 用户运行等）。
    /// 注意语义与"文件损坏"不同：文件本身完好，只是当前环境没有解密能力。
    /// </summary>
    public class StorageDecryptionException : Exception
    {
        public StorageDecryptionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// 剪贴板历史文件的 DPAPI 加密编码层。
    ///
    /// 使用 Windows 数据保护 API（ProtectedData，CurrentUser）：
    /// 启动解密与落盘加密全程无感，不引入任何凭据输入；密文绑定本机当前 Windows 用户，
    /// 拷贝到其他机器/其他用户下无法解开。同用户下的其他进程同样能解密（DPAPI 语义），
    /// 因此防的是"文件被拷走"，不防本机同用户进程——这一边界必须在设置 UI 中如实告知。
    ///
    /// 磁盘格式（两种之一，靠文件头自识别，不依赖配置状态）：
    ///   明文：UTF-8 JSON
    ///   密文：Base64( magic(8B) | DPAPI密文 )
    ///
    /// <see cref="Enabled"/> 只决定 Save 写哪种格式；Load 一律按文件头自识别，
    /// 即使配置丢失或被手改，配置与磁盘短暂不一致也能在下次落盘时自愈。
    /// </summary>
    public class ClipboardStorageCrypto
    {
        /// <summary>密文文件头："CDCE1" + 3 个 NUL，共 8 字节。</summary>
        private static readonly byte[] Magic =
        {
            (byte)'C', (byte)'D', (byte)'C', (byte)'E', (byte)'1', 0, 0, 0
        };

        // 应用级 entropy：仅让密文不能被其他程序按默认 DPAPI 参数直接解开。
        // 它编译进程序集，同用户下的进程可读取，因此不是安全边界。
        private static readonly byte[] AppEntropy =
            Encoding.UTF8.GetBytes("CarroDesk.ClipboardHistory.EncryptedStorage.v1");

        /// <summary>是否以加密格式写盘（Load 不受此值影响，一律自识别）。</summary>
        public bool Enabled { get; set; }

        /// <summary>判断文件文本是否为本模块的密文格式。</summary>
        public static bool IsEncryptedText(string fileText)
        {
            byte[] payload;
            return TryDecodeBase64(fileText, out payload) && StartsWithMagic(payload);
        }

        /// <summary>明文 JSON → Base64 密文文本。失败抛出原生 CryptographicException。</summary>
        public string EncryptToText(string jsonText)
        {
            if (jsonText == null) throw new ArgumentNullException(nameof(jsonText));

            byte[] plain = Encoding.UTF8.GetBytes(jsonText);
            byte[] cipher = ProtectedData.Protect(plain, AppEntropy, DataProtectionScope.CurrentUser);

            byte[] payload = new byte[Magic.Length + cipher.Length];
            Buffer.BlockCopy(Magic, 0, payload, 0, Magic.Length);
            Buffer.BlockCopy(cipher, 0, payload, Magic.Length, cipher.Length);
            return Convert.ToBase64String(payload);
        }

        /// <summary>
        /// Base64 密文文本 → 明文 JSON。
        /// DPAPI 解密失败（masterkey 丢失/换用户）抛 <see cref="StorageDecryptionException"/>；
        /// 其余格式问题同样归一为该异常，由存储层统一走"留档"路径。
        /// </summary>
        public string DecryptToText(string fileText)
        {
            if (fileText == null) throw new ArgumentNullException(nameof(fileText));

            byte[] payload;
            if (!TryDecodeBase64(fileText, out payload) || !StartsWithMagic(payload))
            {
                throw new StorageDecryptionException("clipboard history is not in encrypted format", null);
            }

            byte[] cipher = new byte[payload.Length - Magic.Length];
            Buffer.BlockCopy(payload, Magic.Length, cipher, 0, cipher.Length);

            try
            {
                byte[] plain = ProtectedData.Unprotect(cipher, AppEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException ex)
            {
                throw new StorageDecryptionException(
                    "clipboard history cannot be decrypted in current environment (user profile changed?)", ex);
            }
        }

        private static bool TryDecodeBase64(string fileText, out byte[] payload)
        {
            payload = null;
            if (string.IsNullOrWhiteSpace(fileText)) return false;

            try
            {
                payload = Convert.FromBase64String(fileText.Trim());
                return payload.Length > Magic.Length;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool StartsWithMagic(byte[] payload)
        {
            if (payload == null || payload.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (payload[i] != Magic[i]) return false;
            }
            return true;
        }
    }
}
