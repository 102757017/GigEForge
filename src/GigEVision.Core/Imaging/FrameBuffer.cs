using System;

namespace GigEVision.Core.Imaging;

/// <summary>
/// 一帧图像缓冲：像素按 Format 紧密排列（无行对齐填充），尺寸 = W*H*BytesPerPixel。
/// </summary>
public sealed class FrameBuffer
{
    /// <summary>图像宽（像素）。</summary>
    public int Width { get; init; }

    /// <summary>图像高（像素）。</summary>
    public int Height { get; init; }

    /// <summary>像素格式（PFNC）。</summary>
    public PixelFormatCode Format { get; init; }

    /// <summary>像素数据（紧凑排列，长度 = PayloadSize）。</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>采集时间戳（纳秒，设备时钟，u48 截断后进 GVSP Leader）。</summary>
    public ulong TimestampNs { get; set; }

    /// <summary>GigE Vision block id（1..65535 循环）。</summary>
    public ushort BlockId { get; set; }

    /// <summary>负载字节数（= Data.Length）。</summary>
    public int PayloadSize => Data.Length;
}
