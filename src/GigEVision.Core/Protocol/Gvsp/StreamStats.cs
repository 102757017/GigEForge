using System.Globalization;

namespace GigEVision.Core.Protocol.Gvsp;

/// <summary>
/// 流发送统计（线程安全）：帧/包/字节/重发计数用 Interlocked 维护，
/// 实时帧率用 1 秒滑动窗口（Environment.TickCount64 毫秒时间戳的队列）。
/// </summary>
public sealed class StreamStats
{
    /// <summary>帧率滑动窗口宽度（毫秒）。</summary>
    private const int FpsWindowMs = 1000;

    /// <summary>窗口最短换算跨度：压制突发帧导致的帧率过估计。</summary>
    private const int FpsMinSpanMs = 250;

    private long _framesSent;
    private long _packetsSent;
    private long _bytesSent;
    private long _resendPackets;
    private long _resendRequests;
    private readonly object _fpsLock = new();
    private readonly Queue<long> _frameTimestampsMs = new();

    /// <summary>累计已发帧数。</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>累计已发包数（含 Leader/Trailer，不含重发）。</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>累计已发字节数（线上字节数，不含重发）。</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>累计重发包数。</summary>
    public long ResendPackets => Interlocked.Read(ref _resendPackets);

    /// <summary>累计重发请求数。</summary>
    public long ResendRequests => Interlocked.Read(ref _resendRequests);

    /// <summary>当前帧率（1 秒滑动窗口内的帧数按时间跨度折算）。</summary>
    public double CurrentFps
    {
        get
        {
            lock (_fpsLock)
            {
                long now = Environment.TickCount64;
                while (_frameTimestampsMs.Count > 0 && now - _frameTimestampsMs.Peek() >= FpsWindowMs)
                {
                    _frameTimestampsMs.Dequeue();
                }

                if (_frameTimestampsMs.Count == 0)
                {
                    return 0d;
                }

                long spanMs = now - _frameTimestampsMs.Peek();
                if (spanMs < FpsMinSpanMs)
                {
                    spanMs = FpsMinSpanMs;
                }

                if (spanMs > FpsWindowMs)
                {
                    spanMs = FpsWindowMs;
                }

                return _frameTimestampsMs.Count * 1000.0 / spanMs;
            }
        }
    }

    /// <summary>记录一帧发送完成（packets = 帧内包数，bytes = 线上总字节数）。</summary>
    public void OnFrameSent(int packets, int bytes)
    {
        Interlocked.Increment(ref _framesSent);
        Interlocked.Add(ref _packetsSent, packets);
        Interlocked.Add(ref _bytesSent, bytes);
        lock (_fpsLock)
        {
            _frameTimestampsMs.Enqueue(Environment.TickCount64);
        }
    }

    /// <summary>记录重发活动（packets = 本次重发包数，requests = 本次请求数）。</summary>
    public void OnResend(int packets, int requests)
    {
        Interlocked.Add(ref _resendPackets, packets);
        Interlocked.Add(ref _resendRequests, requests);
    }

    /// <summary>清零全部统计。</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _packetsSent, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _resendPackets, 0);
        Interlocked.Exchange(ref _resendRequests, 0);
        lock (_fpsLock)
        {
            _frameTimestampsMs.Clear();
        }
    }

    /// <summary>摘要行，例如 "12.3 fps | 370 pkts | 4.2 MB"。</summary>
    public string Snapshot()
    {
        double mb = BytesSent / 1048576.0;
        return string.Create(CultureInfo.InvariantCulture, $"{CurrentFps:F1} fps | {PacketsSent} pkts | {mb:F1} MB");
    }
}
