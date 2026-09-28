using System;
using System.IO;
using System.Threading.Tasks;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// 帧编解码（IPC 设计 §5.1）：[int32 LE 长度][UTF-8 JSON]。
    /// 单帧默认上限 1 MB，超限抛 <see cref="InvalidDataException"/>，由连接层直接断开。
    /// </summary>
    public static class FrameCodec
    {
        public const int DefaultMaxFrameBytes = 1024 * 1024;

        /// <summary>读取一帧。返回 null 表示帧间干净 EOF（对端正常关闭）。</summary>
        public static async Task<byte[]> ReadFrameAsync(Stream stream, int maxFrameBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var header = new byte[4];
            var headerRead = await ReadExactlyAsync(stream, header, 4).ConfigureAwait(false);
            if (headerRead == 0) return null;
            if (headerRead < 4) throw new EndOfStreamException("stream ended in frame header");

            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > maxFrameBytes)
                throw new InvalidDataException("frame length out of range: " + length);

            var body = new byte[length];
            var bodyRead = await ReadExactlyAsync(stream, body, length).ConfigureAwait(false);
            if (bodyRead < length) throw new EndOfStreamException("stream ended mid-frame");
            return body;
        }

        public static byte[] EncodeFrame(byte[] body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            var frame = new byte[body.Length + 4];
            BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        /// <summary>恰好读 count 字节；返回实际读取数（小于 count 仅在 EOF 时发生）。</summary>
        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n <= 0) return read;
                read += n;
            }
            return read;
        }
    }
}
