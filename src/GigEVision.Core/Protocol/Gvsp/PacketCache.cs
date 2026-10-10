namespace GigEVision.Core.Protocol.Gvsp;

/// <summary>
/// GVSP 包缓存：按 block_id 保存整帧包列表（[Leader, Data..., Trailer]），
/// 环形保留最近 <see cref="Capacity"/>（=4）帧，供 ResendCmd 重发查询。线程安全（lock）。
/// </summary>
public sealed class PacketCache
{
    /// <summary>缓存帧数上限（契约 5.4：缓存最近 4 帧）。</summary>
    public const int Capacity = 4;

    private readonly object _lock = new();
    private readonly Dictionary<ushort, byte[][]> _frames = new();
    private readonly Queue<ushort> _order = new();   // 入帧顺序，用于环形淘汰

    /// <summary>存入一帧的包列表；同一 block_id 重复到达（回绕撞上未淘汰旧帧）时整体替换。</summary>
    public void Store(ushort blockId, byte[][] packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        lock (_lock)
        {
            if (_frames.ContainsKey(blockId))
            {
                // block_id 回绕后与未淘汰的旧帧撞号：替换内容，保持队列位置不变
                _frames[blockId] = packets;
                return;
            }

            _frames[blockId] = packets;
            _order.Enqueue(blockId);
            while (_order.Count > Capacity)
            {
                var oldest = _order.Dequeue();
                _frames.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 取出 block_id 对应的包列表；未命中返回 null。
    /// 返回的是缓存内的共享引用，调用方只读，不得修改数组内容。
    /// </summary>
    public byte[][]? TryGet(ushort blockId)
    {
        lock (_lock)
        {
            return _frames.TryGetValue(blockId, out var packets) ? packets : null;
        }
    }
}
