namespace LiquidGlass.Core;

/// <summary>
/// 一张 32 位 BGRA 图像，行主序、自上而下、straight（非预乘）Alpha。
///
/// 选 BGRA 而不是 RGBA 是为了零拷贝喂给 Windows 的
/// <c>CreateDIBSection</c> + <c>UpdateLayeredWindow</c>；
/// 内部所有光学计算都会先把它当作 sRGB 数据读入。
/// </summary>
public sealed class BgraFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public BgraFrame(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new byte[Width * Height * 4];
    }

    public int Stride => Width * 4;

    public int PixelCount => Width * Height;

    public void Fill(byte b, byte g, byte r, byte a = 255)
    {
        var p = Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = a;
        }
    }

    public BgraFrame Clone()
    {
        var copy = new BgraFrame(Width, Height);
        Array.Copy(Pixels, copy.Pixels, Pixels.Length);
        return copy;
    }

    /// <summary>最近邻缩放。玻璃只需要低频背景，最近邻足够且零成本。</summary>
    public BgraFrame Scaled(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var dst = new BgraFrame(width, height);
        for (var ty = 0; ty < height; ty++)
        {
            var sy = (int)((long)ty * Height / height);
            var srcRow = sy * Stride;
            var dstRow = ty * dst.Stride;
            for (var tx = 0; tx < width; tx++)
            {
                var sx = (int)((long)tx * Width / width);
                var si = srcRow + sx * 4;
                var di = dstRow + tx * 4;
                dst.Pixels[di] = Pixels[si];
                dst.Pixels[di + 1] = Pixels[si + 1];
                dst.Pixels[di + 2] = Pixels[si + 2];
                dst.Pixels[di + 3] = Pixels[si + 3];
            }
        }
        return dst;
    }

    /// <summary>把另一帧合成到本帧的指定位置（直接覆盖，不含混合）。</summary>
    public void Blit(BgraFrame source, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
        {
            var dy = y + row;
            if (dy < 0 || dy >= Height) continue;
            var copyWidth = Math.Min(source.Width, Width - x);
            if (copyWidth <= 0) continue;
            Array.Copy(
                source.Pixels, row * source.Stride,
                Pixels, dy * Stride + x * 4,
                copyWidth * 4);
        }
    }
}
