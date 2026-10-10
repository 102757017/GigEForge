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
    /// 发送一帧：Leader(packet_id=0) → Data(1..N，每片 ≤ packetSize-8 字节) → Trailer(N+1)。
    /// 整帧（含 Leader/Trailer）先写入 PacketCache 再逐包发送；packetDelayUs &gt; 0 时包间限速。
    /// </summary>
    public void SendFrame(FrameBuffer frame, IPAddress destIp, int destPort, ushort blockId, ushort packetSize, uint packetDelayUs)
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
        var packets = new byte[dataCount + 2][];
        packets[0] = GvspPacket.BuildLeader(blockId, frame.TimestampNs, (uint)frame.Format, (uint)frame.Width, (uint)frame.Height);
        ushort packetId = 1;
        for (int offset = 0; offset < pixels.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, pixels.Length - offset);
            packets[packetId] = GvspPacket.BuildData(packetId, blockId, pixels.Slice(offset, len));
            packetId++;
        }

        var trailer = GvspPacket.BuildTrailer(blockId, (uint)frame.Height);
        GvspPacket.SetPacketId(trailer, (ushort)(dataCount + 1));   // Trailer 的 packet_id = N+1
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
