using System;

namespace GigEVision.Core.Imaging;

/// <summary>
/// 像素格式转换器：仅覆盖项目支持的组合，其余组合抛 NotSupportedException。
/// 支持：RGB8↔BGR8（通道交换）、RGB8→Mono8（BT.601 亮度）、
/// RGB8→BayerRG8（RGGB 采样）、Mono8→RGB8（灰度复制）、BayerRG8→RGB8（最近邻取单通道）。
/// 同格式转换 = 原样拷贝。
/// </summary>
public static class PixelConverter
{
    /// <summary>
    /// 转换像素数据。
    /// </summary>
    /// <param name="src">源像素（紧凑排列，长度 ≥ W*H*BytesPerPixel(from)）。</param>
    /// <param name="from">源格式。</param>
    /// <param name="to">目标格式。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <returns>转换后的新字节数组。</returns>
    public static byte[] Convert(ReadOnlySpan<byte> src, PixelFormatCode from, PixelFormatCode to, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "宽高必须为正");
        int need = width * height * PixelFormatEx.BytesPerPixel(from);
        if (src.Length < need)
            throw new ArgumentException($"源缓冲不足：需要 {need} 字节，实际 {src.Length} 字节", nameof(src));
        if (from == to) return src.ToArray(); // 同格式：拷贝透传

        int pixels = width * height;
        switch (from, to)
        {
            case (PixelFormatCode.RGB8, PixelFormatCode.BGR8):
            case (PixelFormatCode.BGR8, PixelFormatCode.RGB8):
                return SwapRgbBgr(src, pixels);
            case (PixelFormatCode.RGB8, PixelFormatCode.Mono8):
                return RgbToMono(src, pixels);
            case (PixelFormatCode.Mono8, PixelFormatCode.RGB8):
                return MonoToRgb(src, pixels);
            case (PixelFormatCode.RGB8, PixelFormatCode.BayerRG8):
                return RgbToBayerRggb(src, width, height);
            case (PixelFormatCode.BayerRG8, PixelFormatCode.RGB8):
                return BayerToRgbNearest(src, width, height);
            default:
                throw new NotSupportedException(
                    $"不支持的像素格式转换: {PixelFormatEx.PName(from)} → {PixelFormatEx.PName(to)}");
        }
    }

    /// <summary>RGB8 ↔ BGR8：交换每像素第 1、3 字节。</summary>
    private static byte[] SwapRgbBgr(ReadOnlySpan<byte> src, int pixels)
    {
        var dst = new byte[pixels * 3];
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 3;
            dst[o] = src[o + 2];
            dst[o + 1] = src[o + 1];
            dst[o + 2] = src[o];
        }
        return dst;
    }

    /// <summary>RGB8 → Mono8：Y = (299R + 587G + 114B) / 1000（BT.601 亮度）。</summary>
    private static byte[] RgbToMono(ReadOnlySpan<byte> src, int pixels)
    {
        var dst = new byte[pixels];
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 3;
            dst[i] = (byte)((299 * src[o] + 587 * src[o + 1] + 114 * src[o + 2]) / 1000);
        }
        return dst;
    }

    /// <summary>Mono8 → RGB8：灰度复制到三个通道。</summary>
    private static byte[] MonoToRgb(ReadOnlySpan<byte> src, int pixels)
    {
        var dst = new byte[pixels * 3];
        for (int i = 0; i < pixels; i++)
        {
            byte g = src[i];
            int o = i * 3;
            dst[o] = g;
            dst[o + 1] = g;
            dst[o + 2] = g;
        }
        return dst;
    }

    /// <summary>
    /// RGB8 → BayerRG8（RGGB 排布）：
    /// 偶行偶列取 R、偶行奇列取 G、奇行偶列取 G、奇行奇列取 B。
    /// </summary>
    private static byte[] RgbToBayerRggb(ReadOnlySpan<byte> src, int width, int height)
    {
        var dst = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * 3;
            bool oddRow = (y & 1) != 0;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 3;
                bool oddCol = (x & 1) != 0;
                dst[y * width + x] = oddRow
                    ? (oddCol ? src[s + 2] : src[s + 1]) // 奇行：奇列 B / 偶列 G
                    : (oddCol ? src[s + 1] : src[s]);    // 偶行：奇列 G / 偶列 R
            }
        }
        return dst;
    }

    /// <summary>
    /// BayerRG8 → RGB8（最近邻、直接取对应马赛克通道填充，不做插值）：
    /// R 位点输出 (v,0,0)，G 位点输出 (0,v,0)，B 位点输出 (0,0,v)。
    /// </summary>
    private static byte[] BayerToRgbNearest(ReadOnlySpan<byte> src, int width, int height)
    {
        var dst = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width;
            bool oddRow = (y & 1) != 0;
            for (int x = 0; x < width; x++)
            {
                byte v = src[srcRow + x];
                bool oddCol = (x & 1) != 0;
                int d = (y * width + x) * 3;
                if (oddRow)
                {
                    if (oddCol) { dst[d] = 0; dst[d + 1] = 0; dst[d + 2] = v; } // B 位点
                    else { dst[d] = 0; dst[d + 1] = v; dst[d + 2] = 0; }        // G 位点
                }
                else
                {
                    if (oddCol) { dst[d] = 0; dst[d + 1] = v; dst[d + 2] = 0; } // G 位点
                    else { dst[d] = v; dst[d + 1] = 0; dst[d + 2] = 0; }        // R 位点
                }
            }
        }
        return dst;
    }
}
