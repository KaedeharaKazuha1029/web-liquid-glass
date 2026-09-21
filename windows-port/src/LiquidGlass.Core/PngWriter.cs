using System.Buffers.Binary;
using System.IO.Compression;

namespace LiquidGlass.Core;

/// <summary>
/// 极简 PNG 编码器（8 位 RGB / RGBA，无滤波）。
/// 自己写而不引入 System.Drawing.Common，是为了让整个项目保持
/// "零第三方依赖、任何 .NET 10 环境 dotnet build 即可通过"。
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>把 BGRA 帧写成 PNG。Alpha 全为 255 时自动输出 RGB 以减小体积。</summary>
    public static void Write(string path, BgraFrame frame, bool forceAlpha = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        Write(stream, frame, forceAlpha);
    }

    public static void Write(Stream stream, BgraFrame frame, bool forceAlpha = false)
    {
        var hasAlpha = forceAlpha || HasTransparency(frame);
        var channels = hasAlpha ? 4 : 3;

        stream.Write(Signature);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), frame.Height);
        ihdr[8] = 8;                        // bit depth
        ihdr[9] = (byte)(hasAlpha ? 6 : 2); // color type: 6=RGBA, 2=RGB
        ihdr[10] = 0;                       // compression
        ihdr[11] = 0;                       // filter
        ihdr[12] = 0;                       // interlace
        WriteChunk(stream, "IHDR", ihdr);

        // IDAT：逐行前缀一个 0 号滤波字节
        var rawStride = frame.Width * channels;
        var raw = new byte[(rawStride + 1) * frame.Height];
        var src = frame.Pixels;
        var si = 0;
        var di = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            raw[di++] = 0;   // filter type: None
            for (var x = 0; x < frame.Width; x++)
            {
                raw[di++] = src[si + 2];    // R
                raw[di++] = src[si + 1];    // G
                raw[di++] = src[si];        // B
                if (hasAlpha) raw[di++] = src[si + 3];
                si += 4;
            }
        }

        using (var compressed = new MemoryStream())
        {
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            WriteChunk(stream, "IDAT", compressed.ToArray());
        }

        WriteChunk(stream, "IEND", []);
    }

    private static bool HasTransparency(BgraFrame frame)
    {
        var p = frame.Pixels;
        for (var i = 3; i < p.Length; i += 4)
        {
            if (p[i] != 255) return true;
        }
        return false;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    /// <summary>PNG 规范要求的 CRC-32（IEEE 802.3，反射多项式 0xEDB88320）。</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(params byte[][] buffers)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var buffer in buffers)
            {
                foreach (var b in buffer)
                {
                    crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
                }
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
