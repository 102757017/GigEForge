using System;

namespace GigEVision.Core.Imaging;

/// <summary>内置测试图案类型（与寄存器 0x14001C 的取值一一对应，0 = Off 不在此列）。</summary>
public enum PatternType
{
    /// <summary>8 竖条彩条（RGB 场景）或 8 灰阶（Mono8）。</summary>
    ColorBars = 1,

    /// <summary>对角线渐变，随 tick 平移。</summary>
    Gradient = 2,

    /// <summary>10% 间隔网格白线。</summary>
    Grid = 3,

    /// <summary>渐变背景 + 十字线随 tick 平移。</summary>
    MovingCrosshair = 4,
}

/// <summary>
/// 内置测试图案生成器。约定：Mono8 直接生成灰度；其余格式先生成 RGB 场景，
/// 再经 PixelConverter 派生（BayerRG8 = RGB 采样，BGR8 = 通道交换）。
/// 返回的 FrameBuffer.TimestampNs 由调用方（采集线程）填写。
/// </summary>
public static class TestPatternGenerator
{
    /// <summary>SMPTE 风格 8 条饱和色（白/黄/青/绿/品红/红/蓝/黑）。</summary>
    private static readonly (byte R, byte G, byte B)[] BarColors =
    {
        (255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
        (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0),
    };

    /// <summary>生成一帧测试图案。</summary>
    /// <param name="w">宽（像素，须为正）。</param>
    /// <param name="h">高（像素，须为正）。</param>
    /// <param name="fmt">目标像素格式（须受支持）。</param>
    /// <param name="p">图案类型。</param>
    /// <param name="tickMs">单调毫秒时钟（Environment.TickCount64），驱动移动效果。</param>
    public static FrameBuffer Generate(int w, int h, PixelFormatCode fmt, PatternType p, long tickMs)
    {
        if (w <= 0) throw new ArgumentOutOfRangeException(nameof(w), "宽度必须为正");
        if (h <= 0) throw new ArgumentOutOfRangeException(nameof(h), "高度必须为正");
        if (!PixelFormatEx.IsSupported(fmt)) throw new NotSupportedException($"不支持的像素格式: 0x{(uint)fmt:X8}");

        byte[] data;
        if (fmt == PixelFormatCode.Mono8)
        {
            // Mono8：直接生成单通道灰度
            var mono = new byte[w * h];
            FillMono(mono, w, h, p, tickMs);
            data = mono;
        }
        else
        {
            // 彩色格式：先生成 RGB 场景，再按目标格式派生（BayerRG8 走 PixelConverter）
            var rgb = new byte[w * h * 3];
            FillRgb(rgb, w, h, p, tickMs);
            data = fmt == PixelFormatCode.RGB8
                ? rgb
                : PixelConverter.Convert(rgb, PixelFormatCode.RGB8, fmt, w, h);
        }

        return new FrameBuffer { Width = w, Height = h, Format = fmt, Data = data };
    }

    // ================= 单通道（Mono8） =================

    private static void FillMono(byte[] buf, int w, int h, PatternType p, long tickMs)
    {
        int shift = (int)(tickMs / 16) & 0xFF;             // 渐变平移量（约 16ms 走一格）
        int cx = CrossX(tickMs, w), cy = CrossY(tickMs, h); // 十字位置
        int gx = Math.Max(1, w / 10), gy = Math.Max(1, h / 10); // 网格间距 = 10%

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                buf[row + x] = p switch
                {
                    PatternType.ColorBars => (byte)(Math.Min(7, x * 8 / w) * 255 / 7), // 8 灰阶竖条
                    PatternType.Gradient => (byte)(((x + y) * 255 / Math.Max(1, w + h - 2) + shift) & 0xFF),
                    PatternType.Grid => (x % gx == 0 || y % gy == 0) ? (byte)255 : (byte)24,
                    _ => IsCross(x, y, cx, cy) ? (byte)255 : (byte)Diag(x, y, w, h),   // 白十字 + 静态渐变背景
                };
            }
        }
    }

    // ================= 三通道（RGB 场景） =================

    private static void FillRgb(byte[] buf, int w, int h, PatternType p, long tickMs)
    {
        int shift = (int)(tickMs / 16) & 0xFF;
        int cx = CrossX(tickMs, w), cy = CrossY(tickMs, h);
        int gx = Math.Max(1, w / 10), gy = Math.Max(1, h / 10);

        for (int y = 0; y < h; y++)
        {
            int row = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 3;
                switch (p)
                {
                    case PatternType.ColorBars:
                    {
                        var c = BarColors[Math.Min(7, x * 8 / w)]; // 8 饱和色竖条
                        buf[i] = c.R;
                        buf[i + 1] = c.G;
                        buf[i + 2] = c.B;
                        break;
                    }
                    case PatternType.Gradient:
                    {
                        // 对角线渐变 + tick 平移；RGB 三通道相位错开 85/170 形成移动彩带
                        int d = (x + y) * 255 / Math.Max(1, w + h - 2);
                        buf[i] = (byte)((d + shift) & 0xFF);
                        buf[i + 1] = (byte)((d + shift + 85) & 0xFF);
                        buf[i + 2] = (byte)((d + shift + 170) & 0xFF);
                        break;
                    }
                    case PatternType.Grid:
                    {
                        // 10% 网格白线 + 深灰底
                        byte v = (x % gx == 0 || y % gy == 0) ? (byte)255 : (byte)24;
                        buf[i] = v;
                        buf[i + 1] = v;
                        buf[i + 2] = v;
                        break;
                    }
                    default:
                    {
                        // MovingCrosshair：静态对角渐变背景 + 红色十字随 tick 平移
                        if (IsCross(x, y, cx, cy))
                        {
                            buf[i] = 255;
                            buf[i + 1] = 32;
                            buf[i + 2] = 32;
                        }
                        else
                        {
                            byte d = (byte)Diag(x, y, w, h);
                            buf[i] = d;
                            buf[i + 1] = d;
                            buf[i + 2] = d;
                        }
                        break;
                    }
                }
            }
        }
    }

    // ================= 公共小工具 =================

    /// <summary>静态对角线亮度（0..255）。</summary>
    private static int Diag(int x, int y, int w, int h) => (x + y) * 255 / Math.Max(1, w + h - 2);

    /// <summary>十字横向位置：4 秒从左到右循环。</summary>
    private static int CrossX(long tickMs, int w) => (int)(tickMs % 4000) * w / 4000;

    /// <summary>十字纵向位置：3 秒从上到下循环（与横向周期不同，轨迹更丰富）。</summary>
    private static int CrossY(long tickMs, int h) => (int)(tickMs % 3000) * h / 3000;

    /// <summary>是否落在 2px 宽十字上（竖线 cx..cx+1 或横线 cy..cy+1）。</summary>
    private static bool IsCross(int x, int y, int cx, int cy) =>
        x == cx || x == cx + 1 || y == cy || y == cy + 1;
}
