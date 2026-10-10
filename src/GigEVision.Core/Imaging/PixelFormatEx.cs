using System;
using System.Collections.Generic;

namespace GigEVision.Core.Imaging;

/// <summary>
/// PFNC 像素格式代码（GigE Vision PFNC 标准子集，契约 5.5：仅支持这四种）。
/// </summary>
public enum PixelFormatCode : uint
{
    /// <summary>8 位灰度。</summary>
    Mono8 = 0x01080001,

    /// <summary>8 位 Bayer RGGB 原始图。</summary>
    BayerRG8 = 0x01080009,

    /// <summary>24 位 RGB 打包（字节序 R,G,B）。</summary>
    RGB8 = 0x02180014,

    /// <summary>24 位 BGR 打包（字节序 B,G,R，与 GDI+ Format24bppRgb 内存序一致）。</summary>
    BGR8 = 0x02180015,
}

/// <summary>
/// 像素格式辅助：位深 / 字节深 / 支持列表 / PFNC 名称。
/// PayloadSize = W * H * BitsPerPixel / 8。
/// </summary>
public static class PixelFormatEx
{
    /// <summary>全部受支持的格式（供 UI 枚举与 GenICam XML 生成使用）。</summary>
    public static IReadOnlyList<PixelFormatCode> Supported { get; } = new[]
    {
        PixelFormatCode.Mono8,
        PixelFormatCode.BayerRG8,
        PixelFormatCode.RGB8,
        PixelFormatCode.BGR8,
    };

    /// <summary>每像素位数：Mono8/BayerRG8 = 8；RGB8/BGR8 = 24。未知格式抛 NotSupportedException。</summary>
    public static int BitsPerPixel(PixelFormatCode fmt) => fmt switch
    {
        PixelFormatCode.Mono8 or PixelFormatCode.BayerRG8 => 8,
        PixelFormatCode.RGB8 or PixelFormatCode.BGR8 => 24,
        _ => throw new NotSupportedException($"未知的 PFNC 像素格式: 0x{(uint)fmt:X8}"),
    };

    /// <summary>每像素字节数（均为整字节格式）。</summary>
    public static int BytesPerPixel(PixelFormatCode fmt) => BitsPerPixel(fmt) / 8;

    /// <summary>是否为受支持的 PFNC 格式（未知代码返回 false，不抛异常）。</summary>
    public static bool IsSupported(PixelFormatCode fmt) =>
        fmt is PixelFormatCode.Mono8 or PixelFormatCode.BayerRG8
             or PixelFormatCode.RGB8 or PixelFormatCode.BGR8;

    /// <summary>PFNC 标准名称（GenICam EnumEntry / 日志显示用）。</summary>
    public static string PName(PixelFormatCode fmt) => fmt switch
    {
        PixelFormatCode.Mono8 => "Mono8",
        PixelFormatCode.BayerRG8 => "BayerRG8",
        PixelFormatCode.RGB8 => "RGB8",
        PixelFormatCode.BGR8 => "BGR8",
        _ => $"Unknown_0x{(uint)fmt:X8}",
    };
}
