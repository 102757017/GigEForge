namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GVCP 包头（8 字节，全部大端）：
/// 命令包 <c>[0]=0x42 魔数，[1]=flags，[2..3]=command，[4..5]=length（不含包头），[6..7]=req_id</c>；
/// 应答包前两字节即 status u16（成功 0x0000 → [0]=0x00；错误 0x8xxx → [0]=0x80、[1]=错误码），
/// Aravis 等客户端据此把应答识别为 ACK / ERROR 两种包类型。
/// </summary>
public readonly record struct GvcpHeader(byte Key, byte Flags, ushort Command, ushort Length, ushort ReqId);

/// <summary>GVCP 包头编解码：纯手工位移、统一大端，无序列化依赖。</summary>
public static class GvcpHeaderCodec
{
    /// <summary>包头固定长度（字节）。</summary>
    public const int Size = 8;

    /// <summary>GVCP 命令包魔数（message key），命令包首字节必须为 0x42。</summary>
    public const byte GvcpKey = 0x42;

    /// <summary>从数据报前 8 字节解析包头（调用方需保证 b.Length ≥ 8）。</summary>
    public static GvcpHeader Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
        {
            throw new ArgumentException("GVCP 包头不足 8 字节", nameof(b));
        }

        return new GvcpHeader(
            Key: b[0],
            Flags: b[1],
            Command: (ushort)((b[2] << 8) | b[3]),
            Length: (ushort)((b[4] << 8) | b[5]),
            ReqId: (ushort)((b[6] << 8) | b[7]));
    }

    /// <summary>
    /// 构建成功 ACK 包：status=0x0000 → <c>[0]=0x00（ACK 包类型），[1]=0x00</c>，
    /// [2..3]=ack 命令码，[4..5]=payload 长度，[6..7]=req_id（与请求一致），[8..]=payload。
    /// </summary>
    public static byte[] BuildAck(GvcpCommand ack, ushort reqId, byte[] payload)
    {
        int payloadLen = payload?.Length ?? 0;
        var packet = new byte[Size + payloadLen];

        packet[0] = 0x00;                       // ACK 包类型（status 高字节 = 0x00）
        packet[1] = 0x00;                       // status 低字节 = 0x00（成功）
        packet[2] = (byte)((ushort)ack >> 8);
        packet[3] = (byte)ack;
        packet[4] = (byte)(payloadLen >> 8);
        packet[5] = (byte)payloadLen;
        packet[6] = (byte)(reqId >> 8);
        packet[7] = (byte)reqId;
        if (payloadLen > 0)
        {
            payload!.AsSpan(0, payloadLen).CopyTo(packet.AsSpan(Size));
        }

        return packet;
    }

    /// <summary>
    /// 构建错误 ACK 包：status=0x8xxx → <c>[0]=0x80（ERROR 包类型），[1]=错误码低字节</c>，
    /// [2..3]=客户端期望的 ack 命令码，[4..5]=0，[6..7]=req_id，无 payload。
    /// Aravis 在 ERROR 分支校验 ack 命令码与 req_id，并把 [1] 作为 GVCP 错误码。
    /// </summary>
    public static byte[] BuildError(GvcpCommand ack, ushort reqId, GevStatus status)
    {
        var packet = new byte[Size];

        packet[0] = (byte)((ushort)status >> 8);   // 0x80 → ERROR 包类型
        packet[1] = (byte)status;                  // GVCP 错误码
        packet[2] = (byte)((ushort)ack >> 8);
        packet[3] = (byte)ack;
        packet[4] = 0x00;                          // length = 0（无 payload）
        packet[5] = 0x00;
        packet[6] = (byte)(reqId >> 8);
        packet[7] = (byte)reqId;

        return packet;
    }
}
