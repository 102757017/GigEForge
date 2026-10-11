using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GigEVision.Core.Imaging;

namespace GigEVision.Core.Protocol.Gvsp;

/// <summary>
/// GVSP 流通道发送器：把 FrameBuffer 切分为 Leader / Data / Trailer 包经 UDP 发出；
/// 整帧包列表写入 <see cref="PacketCache"/>（最近 4 帧）供重发；统计进 <see cref="StreamStats"/>。
/// SendFrame 为同步顺序发送：保证同一 block 内包序严格有序，且天然被采集线程的出帧节奏限速。
/// </summary>
public sealed class GvspTransmitter : IDisposable
{
    /// <summary>packetSize 非法时的兜底分片大小（字节）。</summary>
    private const int FallbackChunkSize = 1400;

    /// <summary>重发请求中 last_packet_id = 0xFFFF 表示“直到帧尾”。</summary>
    private const ushort ResendLastAll = 0xFFFF;

    private readonly StreamStats _stats;
    private readonly PacketCache _cache = new();
    private readonly object _sendLock = new();

    private UdpClient? _udp;
    private int _boundPort;

    /// <summary>日志事件（绑定端口、发送失败、重发未命中等）。</summary>
    public event Action<string>? Log;

    /// <summary>创建发送器（不绑定端口，<see cref="Start"/> 后可发送）。</summary>
    public GvspTransmitter(StreamStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _stats = stats;
    }

    /// <summary>本机流源 UDP 端口（Start 后有效，未启动为 0）。</summary>
    public int BoundPort => Volatile.Read(ref _boundPort);

    /// <summary>启动流通道：绑定 bindAddress 上的系统随机端口（port=0）。</summary>
    public void Start(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        lock (_sendLock)
        {
            if (_udp is not null)
            {
                return;
            }

            var udp = new UdpClient(new IPEndPoint(bindAddress, 0));
            _udp = udp;
            _boundPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        }

        Log?.Invoke($"GVSP 流通道已绑定 {bindAddress}:{BoundPort}");
    }

    /// <summary>停止流通道并关闭端口。</summary>
    public void Stop()
    {
        lock (_sendLock)
        {
            var udp = _udp;
            _udp = null;
            _boundPort = 0;
            try
            {
                udp?.Close();
            }
            catch
            {
                // 关闭失败可忽略
            }
        }

        Log?.Invoke("GVSP 流通道已停止");
    }

    /// <summary>
    /// 发送一帧：Leader(packet_id=0) → Data(1..N，每片 ≤ packetSize-8 字节) → Trailer(末包+1)。
    /// 整帧（含 Leader/Trailer）先写入 PacketCache 再逐包发送；packetDelayUs &gt; 0 时包间限速。
    /// <paramref name="leaderPayloadType"/>：Chunk 模式传 0x4001（image | chunk 位）；
    /// <paramref name="chunkBlock"/>：Chunk 模式时追加在<b>数据流末尾</b>的原始块
    /// （如 [Width][Height][ChunkID][Size] 16 字节），null = 普通图像帧。
    /// <para>Chunk 块装载策略（Aravis 兼容关键点）：Aravis 从 received_size-8 反向扫描
    /// [ChunkID][Size] 块，而 received_size 只统计数据包负载字节（Trailer 包不计入），
    /// 因此 Chunk 字节必须落在数据流内：优先并入最后一个数据包（不超分片上限时），
    /// 否则作为独立数据包（packet_id=N+1）发送。两种布局均与 Aravis 按负载总量
    /// ceil((payload)/block)+2 推导的包数、Trailer 位置约束一致。</para>
    /// </summary>
    public void SendFrame(FrameBuffer frame, IPAddress destIp, int destPort, ushort blockId, ushort packetSize,
        uint packetDelayUs, ushort leaderPayloadType = GvspPacket.PayloadTypeImage, byte[]? chunkBlock = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (destIp is null)
        {
            Log?.Invoke("GVSP 发送跳过：目的地 IP 为空");
            return;
        }

        UdpClient? udp;
        lock (_sendLock)
        {
            udp = _udp;
        }

        if (udp is null)
        {
            Log?.Invoke("GVSP 发送跳过：流通道未启动");
            return;
        }

        int chunkSize = packetSize > GvspPacket.HeaderSize ? packetSize - GvspPacket.HeaderSize : FallbackChunkSize;

        // 组包：[0]=Leader，[1..N]=Data（packet_id 1..N），[N+1]=Trailer
        ReadOnlySpan<byte> pixels = frame.Data;
        int dataCount = (pixels.Length + chunkSize - 1) / chunkSize;

        // Chunk 块装载：末包剩余空间足够则并入末包，否则独立成包（见方法注释）
        bool chunkExtraPacket = false;
        if (chunkBlock is { Length: > 0 })
        {
            int lastDataLen = dataCount > 0 ? pixels.Length - (dataCount - 1) * chunkSize : 0;
            chunkExtraPacket = dataCount == 0 || lastDataLen + chunkBlock.Length > chunkSize;
        }

        int totalDataPackets = dataCount + (chunkExtraPacket ? 1 : 0);
        var packets = new byte[totalDataPackets + 2][];
        packets[0] = GvspPacket.BuildLeader(blockId, frame.TimestampNs, (uint)frame.Format, (uint)frame.Width,
            (uint)frame.Height, leaderPayloadType);
        ushort packetId = 1;
        for (int offset = 0; offset < pixels.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, pixels.Length - offset);
            if (chunkBlock is { Length: > 0 } && !chunkExtraPacket && offset + len >= pixels.Length)
            {
                // Chunk 块并入最后一个数据包（数据包负载 = 图像尾片 + Chunk 块）
                var merged = new byte[len + chunkBlock.Length];
                pixels.Slice(offset, len).CopyTo(merged);
                chunkBlock.CopyTo(merged, len);
                packets[packetId] = GvspPacket.BuildData(packetId, blockId, merged);
            }
            else
            {
                packets[packetId] = GvspPacket.BuildData(packetId, blockId, pixels.Slice(offset, len));
            }

            packetId++;
        }

        if (chunkExtraPacket && chunkBlock is { Length: > 0 })
        {
            // 末包已满（或并入超限）：Chunk 块独立成包，packet_id 紧随图像数据包
            packets[packetId] = GvspPacket.BuildData(packetId, blockId, chunkBlock);
            packetId++;
        }

        var trailer = GvspPacket.BuildTrailer(blockId, (uint)frame.Height, leaderPayloadType);
        GvspPacket.SetPacketId(trailer, (ushort)(totalDataPackets + 1));   // Trailer 的 packet_id = N+1
        packets[^1] = trailer;

        _cache.Store(blockId, packets);   // 整帧入缓存（重发窗口最近 4 帧）

        long frameBytes = 0;
        foreach (var packet in packets)
        {
            frameBytes += packet.Length;
        }

        var destination = new IPEndPoint(destIp, destPort);
        lock (_sendLock)
        {
            for (int i = 0; i < packets.Length; i++)
            {
                var packet = packets[i];
                try
                {
                    udp.Send(packet, packet.Length, destination);
                }
                catch (SocketException ex)
                {
                    Log?.Invoke($"GVSP 发送失败（packet_id={i}）: {ex.SocketErrorCode}");
                }
                catch (ObjectDisposedException)
                {
                    return;   // 流通道已停止，放弃剩余包
                }

                if (packetDelayUs > 0 && i < packets.Length - 1)
                {
                    DelayMicroseconds(packetDelayUs);
                }
            }
        }

        _stats.OnFrameSent(packets.Length, (int)frameBytes);
    }

    /// <summary>
    /// 发送 GVSP 测试包（FireTestPacket 路径 MTU 探测用）：单个 UDP 报文，
    /// <b>UDP 载荷长度必须精确等于调用方给定的 payloadBytes</b>——按 v2 语义，
    /// 客户端（Aravis auto_packet_size）写 0x0D04 后以此校验：载荷 == 0x0D04 低 16 位 - 28（IP+UDP 头），
    /// 且必须发到 (0x0D18, 0x0D00 低 16 位) 的 socket，一个字节不差。内容不参与校验
    /// （前两字节置伪 GVSP status=0 便于抓包辨识）。不写入缓存、不计入统计。
    /// </summary>
    public void SendTestPacket(IPAddress destIp, int destPort, int payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(destIp);

        UdpClient? udp;
        lock (_sendLock)
        {
            udp = _udp;
        }

        if (udp is null)
        {
            Log?.Invoke("测试包发送跳过：流通道未启动");
            return;
        }

        int chunk = Math.Max(0, payloadBytes - GvspPacket.HeaderSize);
        var packet = GvspPacket.BuildData(1, blockId: 0, new byte[chunk]);
        try
        {
            udp.Send(packet, packet.Length, new IPEndPoint(destIp, destPort));
        }
        catch (SocketException ex)
        {
            Log?.Invoke($"GVSP 测试包发送失败: {ex.SocketErrorCode}");
        }
        catch (ObjectDisposedException)
        {
            // 流通道已停止，忽略
        }
    }

    /// <summary>
    /// 处理重发请求：从 PacketCache 取 blockId 的包列表，
    /// 重发 [firstId..lastId]（lastId=0xFFFF 表示直到帧尾）。
    /// packet_id 与缓存下标一致：0=Leader，1..N=Data，N+1=Trailer。
    /// </summary>
    public void HandleResend(IPEndPoint requester, ushort blockId, ushort firstId, ushort lastId)
    {
        ArgumentNullException.ThrowIfNull(requester);

        var packets = _cache.TryGet(blockId);
        int sent = 0;
        if (packets is null)
        {
            Log?.Invoke($"重发请求未命中：block {blockId} 不在缓存（{requester}）");
        }
        else
        {
            UdpClient? udp;
            lock (_sendLock)
            {
                udp = _udp;
            }

            if (udp is null)
            {
                Log?.Invoke("重发跳过：流通道未启动");
                _stats.OnResend(0, 1);
                return;
            }

            int maxId = packets.Length - 1;                              // 末包 id = N+1
            int first = firstId;
            int last = lastId == ResendLastAll ? maxId : Math.Min(lastId, maxId);
            for (int id = first; id <= last; id++)
            {
                var packet = packets[id];
                try
                {
                    udp.Send(packet, packet.Length, requester);
                    sent++;
                }
                catch (SocketException ex)
                {
                    Log?.Invoke($"GVSP 重发失败（block={blockId} id={id}）: {ex.SocketErrorCode}");
                }
                catch (ObjectDisposedException)
                {
                    break;   // 流通道已停止
                }
            }
        }

        _stats.OnResend(sent, 1);   // 每个重发请求计 1 次，包数按实际发出计
    }

    /// <summary>包间限速：大于 2ms 的部分用 Thread.Sleep(1) 分片等待，尾程用 SpinWait 忙等到点。</summary>
    private static void DelayMicroseconds(uint delayUs)
    {
        long targetTicks = Stopwatch.Frequency * delayUs / 1_000_000;
        if (targetTicks <= 0)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            long remaining = targetTicks - (Stopwatch.GetTimestamp() - start);
            if (remaining <= 0)
            {
                return;
            }

            long remainingUs = remaining * 1_000_000 / Stopwatch.Frequency;
            if (remainingUs > 2000)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(32);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
