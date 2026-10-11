namespace GigEVision.Core.Protocol.Gvsp;

/// <summary>
/// GVSP 包构建器（GigE Vision 流协议，包头 8 字节，全部大端）：
/// <c>[0..1]=status（0x0000 = 成功），[2..3]=block_id，[4]=content_type，[5..7]=packet_id（24 位）</c>。
/// content_type：Leader=1 / Trailer=2 / Data(Payload)=3；
/// packet_id：Leader=0，Data=1..N，Trailer=N+1；block_id 从 1 自增，65535 后回 1。
/// 线格式严格对齐 GigE Vision 规范（与 Wireshark GVSP dissector / Aravis arvgvsp 实现逐字节一致；
/// 注意包头没有 0x37 之类的魔数字节，status 恒为 0x0000 表示成功帧）。
/// </summary>
public static class GvspPacket
{
    /// <summary>包头固定长度（字节）。</summary>
    public const int HeaderSize = 8;

    /// <summary>status 成功值（错误帧由客户端按 GEV 状态码处理，本设备恒发成功帧）。</summary>
    public const ushort StatusSuccess = 0x0000;

    /// <summary>content_type：帧头包。</summary>
    public const byte ContentTypeLeader = 0x01;

    /// <summary>content_type：帧尾包。</summary>
    public const byte ContentTypeTrailer = 0x02;

    /// <summary>content_type：数据包。</summary>
    public const byte ContentTypePayload = 0x03;

    /// <summary>payload_type：图像（1）。</summary>
    public const ushort PayloadTypeImage = 0x0001;

    /// <summary>packet_id 字段位宽掩码（24 位，位于包头 [5..7]）。</summary>
    public const uint PacketIdMask = 0x00FFFFFF;

    /// <summary>Leader 负载长度（字节）：flags(2) + payload_type(2) + timestamp(8) + 图像信息(24)。</summary>
    public const int LeaderPayloadSize = 36;

    /// <summary>Trailer 负载长度（字节）：payload_type(4) + size_y(4)。</summary>
    public const int TrailerPayloadSize = 8;

    /// <summary>
    /// 构建帧头包（8+36 字节）。packet_id 固定 0。
    /// 负载布局：[0..1] flags=0，[2..3] payload_type=1，
    /// [4..7]/[8..11] 时间戳（u64 纳秒拆高/低 32 位），
    /// [12..15] 像素格式（PFNC），[16..19] size_x，[20..23] size_y，
    /// [24..27] offset_x=0，[28..31] offset_y=0，[32..33]/[34..35] padding=0。
    /// </summary>
    public static byte[] BuildLeader(ushort blockId, ulong timestampNs, uint pixelFormat, uint width, uint height)
    {
        var packet = new byte[HeaderSize + LeaderPayloadSize];
        FillHeader(packet, ContentTypeLeader, packetId: 0, blockId);

        Span<byte> p = packet.AsSpan(HeaderSize);
        WriteU16(p, 2, PayloadTypeImage);
        WriteU32(p, 4, (uint)(timestampNs >> 32));
        WriteU32(p, 8, (uint)timestampNs);
        WriteU32(p, 12, pixelFormat);
        WriteU32(p, 16, width);
        WriteU32(p, 20, height);
        // [24..35] 的 offset/padding 字段保持 0（new 数组已清零）
        return packet;
    }

    /// <summary>
    /// 构建帧尾包（8+8 字节）。负载布局：[0..3] payload_type=1（u32），[4..7] size_y（负载高度）。
    /// 注意：Trailer 的 packet_id = N+1，本方法先填 0，由发送器用 <see cref="SetPacketId"/> 修正。
    /// </summary>
    public static byte[] BuildTrailer(ushort blockId, uint payloadHeight)
    {
        var packet = new byte[HeaderSize + TrailerPayloadSize];
        FillHeader(packet, ContentTypeTrailer, packetId: 0, blockId);

        Span<byte> p = packet.AsSpan(HeaderSize);
        WriteU32(p, 0, PayloadTypeImage);
        WriteU32(p, 4, payloadHeight);
        return packet;
    }

    /// <summary>构建数据包（8+chunk.Length 字节），负载即像素分片原样拷贝。</summary>
    public static byte[] BuildData(ushort packetId, ushort blockId, ReadOnlySpan<byte> chunk)
    {
        var packet = new byte[HeaderSize + chunk.Length];
        FillHeader(packet, ContentTypePayload, packetId, blockId);
        chunk.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>修正包头中的 packet_id 字段（[5..7] 24 位），供发送器把 Trailer 填成 N+1。</summary>
    public static void SetPacketId(byte[] packet, ushort packetId)
    {
        packet[5] = 0;                       // packet_id 24 位：ushort 恒在高 8 位为 0
        packet[6] = (byte)(packetId >> 8);
        packet[7] = (byte)packetId;
    }

    /// <summary>填充 8 字节包头（大端手工位移）：status=成功、block_id、content_type、24 位 packet_id。</summary>
    private static void FillHeader(byte[] packet, byte contentType, ushort packetId, ushort blockId)
    {
        packet[0] = (byte)(StatusSuccess >> 8);   // status 高字节 = 0x00（成功）
        packet[1] = (byte)StatusSuccess;          // status 低字节
        packet[2] = (byte)(blockId >> 8);
        packet[3] = (byte)blockId;
        packet[4] = contentType;
        packet[5] = 0;                            // packet_id 高 8 位（24 位字段）
        packet[6] = (byte)(packetId >> 8);
        packet[7] = (byte)packetId;
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
}
