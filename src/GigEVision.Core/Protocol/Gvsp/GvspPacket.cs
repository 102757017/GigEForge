namespace GigEVision.Core.Protocol.Gvsp;

/// <summary>
/// GVSP 包构建器（经典流协议，包头 8 字节，全部大端）：
/// <c>[0]=0x37 魔数，[1]=flags=0，[2..3]=format，[4..5]=packet_id，[6..7]=block_id</c>。
/// format：Leader=0x0001 / Trailer=0x0002 / Data=0x0003；
/// packet_id：Leader=0，Data=1..N，Trailer=N+1；block_id 从 1 自增，65535 后回 1。
/// </summary>
public static class GvspPacket
{
    /// <summary>GVSP 消息魔数（0x37）。</summary>
    public const byte Key = 0x37;

    /// <summary>包头固定长度（字节）。</summary>
    public const int HeaderSize = 8;

    /// <summary>format：帧头包。</summary>
    public const ushort FormatLeader = 0x0001;

    /// <summary>format：帧尾包。</summary>
    public const ushort FormatTrailer = 0x0002;

    /// <summary>format：数据包。</summary>
    public const ushort FormatData = 0x0003;

    /// <summary>payload_type：图像（1）。</summary>
    public const ushort PayloadTypeImage = 0x0001;

    /// <summary>Leader 负载长度（字节）。</summary>
    public const int LeaderPayloadSize = 34;

    /// <summary>Trailer 负载长度（字节）。</summary>
    public const int TrailerPayloadSize = 8;

    /// <summary>
    /// 构建帧头包（8+34 字节）。packet_id 固定 0。
    /// 负载布局：[0..1] 保留=0，[2..3] payload_type=1，
    /// [4..9] 时间戳（u48 纳秒，超出 48 位部分截断），
    /// [10..13] 像素格式（PFNC），[14..17] size_x，[18..21] size_y，
    /// [22..25] offset_x=0，[26..29] offset_y=0，[30..31]/[32..33] padding=0。
    /// </summary>
    public static byte[] BuildLeader(ushort blockId, ulong timestampNs, uint pixelFormat, uint width, uint height)
    {
        var packet = new byte[HeaderSize + LeaderPayloadSize];
        FillHeader(packet, FormatLeader, packetId: 0, blockId);

        Span<byte> p = packet.AsSpan(HeaderSize);
        WriteU16(p, 2, PayloadTypeImage);
        WriteU48(p, 4, timestampNs);       // 截取低 48 位
        WriteU32(p, 10, pixelFormat);
        WriteU32(p, 14, width);
        WriteU32(p, 18, height);
        // [22..33] 的 offset/padding 字段保持 0（new 数组已清零）
        return packet;
    }

    /// <summary>
    /// 构建帧尾包（8+8 字节）。负载布局：[0..1] 保留=0，[2..3] payload_type=1，[4..7] size_y（负载高度）。
    /// 注意：Trailer 的 packet_id = N+1，本方法先填 0，由发送器用 <see cref="SetPacketId"/> 修正。
    /// </summary>
    public static byte[] BuildTrailer(ushort blockId, uint payloadHeight)
    {
        var packet = new byte[HeaderSize + TrailerPayloadSize];
        FillHeader(packet, FormatTrailer, packetId: 0, blockId);

        Span<byte> p = packet.AsSpan(HeaderSize);
        WriteU16(p, 2, PayloadTypeImage);
        WriteU32(p, 4, payloadHeight);
        return packet;
    }

    /// <summary>构建数据包（8+chunk.Length 字节），负载即像素分片原样拷贝。</summary>
    public static byte[] BuildData(ushort packetId, ushort blockId, ReadOnlySpan<byte> chunk)
    {
        var packet = new byte[HeaderSize + chunk.Length];
        FillHeader(packet, FormatData, packetId, blockId);
        chunk.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>修正包头中的 packet_id 字段（[4..5]），供发送器把 Trailer 填成 N+1。</summary>
    public static void SetPacketId(byte[] packet, ushort packetId)
    {
        packet[4] = (byte)(packetId >> 8);
        packet[5] = (byte)packetId;
    }

    /// <summary>填充 8 字节包头（大端手工位移）。</summary>
    private static void FillHeader(byte[] packet, ushort format, ushort packetId, ushort blockId)
    {
        packet[0] = Key;
        packet[1] = 0;                                   // flags 恒为 0
        packet[2] = (byte)(format >> 8);
        packet[3] = (byte)format;
        packet[4] = (byte)(packetId >> 8);
        packet[5] = (byte)packetId;
        packet[6] = (byte)(blockId >> 8);
        packet[7] = (byte)blockId;
    }

    private static void WriteU16(Span<byte> b, int offset, ushort value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
    }

    private static void WriteU32(Span<byte> b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    /// <summary>写 48 位无符号整数（6 字节，高字节在前；超出 48 位的高位丢弃）。</summary>
    private static void WriteU48(Span<byte> b, int offset, ulong value)
    {
        for (int i = 0; i < 6; i++)
        {
            b[offset + i] = (byte)(value >> (8 * (5 - i)));
        }
    }
}
