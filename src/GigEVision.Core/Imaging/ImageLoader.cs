using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GigEVision.Core.Imaging;

/// <summary>
/// 图像文件加载与预览位图转换（基于 GDI+ / System.Drawing，仅 Windows）。
/// 支持格式：BMP / JPG / PNG / TIF（GDI+ 内建解码器）。
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// 加载图片文件并缩放到指定尺寸，转换为目标像素格式。
    /// 流程：Bitmap 加载 → 高质量 Graphics 缩放（Format24bppRgb）→ BitmapData 读 BGR 行（注意 stride）
    /// → 交换为紧凑 RGB → PixelConverter 转目标格式。
    /// </summary>
    /// <param name="path">图片路径（BMP/JPG/PNG/TIF）。</param>
    /// <param name="target">目标像素格式。</param>
    /// <param name="resizeWidth">目标宽（像素）。</param>
    /// <param name="resizeHeight">目标高（像素）。</param>
    public static FrameBuffer Load(string path, PixelFormatCode target, int resizeWidth, int resizeHeight)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("图片路径为空", nameof(path));
        if (resizeWidth <= 0 || resizeHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(resizeWidth), "缩放尺寸必须为正");
        if (!PixelFormatEx.IsSupported(target))
            throw new NotSupportedException($"不支持的像素格式: 0x{(uint)target:X8}");

        using var source = new Bitmap(path);              // GDI+ 按扩展名/内容自动选择解码器
        using var scaled = ResizeTo24Bpp(source, resizeWidth, resizeHeight);
        byte[] rgb = BitmapToRgb24(scaled, resizeWidth, resizeHeight);
        byte[] data = target == PixelFormatCode.RGB8
            ? rgb
            : PixelConverter.Convert(rgb, PixelFormatCode.RGB8, target, resizeWidth, resizeHeight);

        return new FrameBuffer { Width = resizeWidth, Height = resizeHeight, Format = target, Data = data };
    }

    /// <summary>
    /// FrameBuffer → System.Drawing.Bitmap（UI 预览用）。
    /// 彩色格式统一转 BGR8 后写入 Format24bppRgb（stride 4 字节对齐）；
    /// Mono8 使用 Format8bppIndexed + 256 级灰度调色板。不支持/空帧返回 null。
    /// </summary>
    public static Bitmap? ToPreviewBitmap(FrameBuffer? frame)
    {
        if (frame is null || frame.Data.Length == 0) return null;
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0) return null;

        if (frame.Format == PixelFormatCode.Mono8)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i); // 灰度调色板
            bmp.Palette = pal; // 重新赋值才会生效

            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                var row = new byte[bd.Stride]; // stride 对齐 4，行尾补 0
                for (int y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(frame.Data, y * w, row, 0, w);
                    Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), bd.Stride);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // 彩色格式：统一转 BGR8（= Format24bppRgb 内存序 B,G,R）
        byte[] bgr = frame.Format switch
        {
            PixelFormatCode.BGR8 => frame.Data,
            PixelFormatCode.RGB8 => PixelConverter.Convert(frame.Data, PixelFormatCode.RGB8, PixelFormatCode.BGR8, w, h),
            PixelFormatCode.BayerRG8 => PixelConverter.Convert(
                PixelConverter.Convert(frame.Data, PixelFormatCode.BayerRG8, PixelFormatCode.RGB8, w, h),
                PixelFormatCode.RGB8, PixelFormatCode.BGR8, w, h),
            _ => throw new NotSupportedException($"预览不支持的像素格式: {PixelFormatEx.PName(frame.Format)}"),
        };

        var bmp24 = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var bd24 = bmp24.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = w * 3;
            var row = new byte[bd24.Stride]; // stride 对齐 4，行尾补 0
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(bgr, y * rowBytes, row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(bd24.Scan0, y * bd24.Stride), bd24.Stride);
            }
        }
        finally { bmp24.UnlockBits(bd24); }
        return bmp24;
    }

    /// <summary>
    /// 高质量缩放到 Format24bppRgb 位图（内部共享：ImageLoader 与 SDK 的 JPEG 解码路径复用）。
    /// </summary>
    internal static Bitmap ResizeTo24Bpp(Image source, int width, int height)
    {
        var dst = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        dst.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height),
            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return dst;
    }

    /// <summary>
    /// 读取 24bpp 位图为紧凑 RGB 字节数组：GDI+ 行序为 BGR 且 stride 对齐 4，
    /// 逐行读取后交换为 R,G,B。
    /// </summary>
    internal static byte[] BitmapToRgb24(Bitmap source, int width, int height)
    {
        var rect = new Rectangle(0, 0, width, height);
        var bd = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rgb = new byte[width * height * 3];
            var row = new byte[bd.Stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, bd.Stride);
                int srcOff = 0, dstOff = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    rgb[dstOff] = row[srcOff + 2];     // R
                    rgb[dstOff + 1] = row[srcOff + 1]; // G
                    rgb[dstOff + 2] = row[srcOff];     // B
                    srcOff += 3;
                    dstOff += 3;
                }
            }
            return rgb;
        }
        finally { source.UnlockBits(bd); }
    }
}
