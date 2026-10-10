namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GVCP 包头（8 字节，全部大端）：
/// <c>[0]=0x42 魔数，[1]=flags，[2..3]=command，[4..5]=length（不含包头），[6..7]=req_id</c>。
/// </summary>
public readonly record struct GvcpHeader(byte Key, byte Flags, ushort Command, ushort Length, ushort ReqId);

/// <summary>GVCP 包头编解码：纯手工位移、统一大端，无序列化依赖。</summary>
public static class GvcpHeaderCodec
{
    /// <summary>包头固定长度（字节）。</summary>
    public const int Size = 8;

    /// <summary>GVCP 消息魔数（message key），首字节必须为 0x42。</summary>
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
    /// 构建 ACK 包：
    /// <c>[0]=0x42，[1]=AckRequired，[2..3]=ack 命令码，[4..5]=length(2+payload)，[6..7]=req_id（与请求一致），
    /// [8..9]=status u16 大端，其后为 payload</c>。status 永远位于 payload 之前。
    /// </summary>
    public static byte[] BuildAck(GvcpCommand ack, ushort reqId, ushort status, byte[] payload)
    {
        int payloadLen = payload?.Length ?? 0;
        var packet = new byte[Size + 2 + payloadLen];

        packet[0] = GvcpKey;
        packet[1] = (byte)GvcpFlags.AckRequired;
        packet[2] = (byte)((ushort)ack >> 8);
        packet[3] = (byte)ack;
        ushort length = (ushort)(2 + payloadLen);          // length = status(2) + payload
        packet[4] = (byte)(length >> 8);
        packet[5] = (byte)length;
        packet[6] = (byte)(reqId >> 8);
        packet[7] = (byte)reqId;
        packet[8] = (byte)(status >> 8);
        packet[9] = (byte)status;
        if (payloadLen > 0)
        {
            payload!.AsSpan(0, payloadLen).CopyTo(packet.AsSpan(Size + 2));
        }

        return packet;
    }
}
