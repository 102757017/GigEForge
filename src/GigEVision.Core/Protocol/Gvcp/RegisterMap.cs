namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>寄存器/内存区访问权限。</summary>
public enum RegisterAccess
{
    /// <summary>只读：外部 WriteReg/WriteMem 被拒绝（回 WriteProtect）。</summary>
    ReadOnly,

    /// <summary>可读可写。</summary>
    ReadWrite,
}

/// <summary>寄存器/内存区定义描述（地址、大小、名称、权限）。</summary>
public sealed record RegisterInfo(uint Address, uint Size, string Name, RegisterAccess Access);

/// <summary>寄存器表内部条目：4 字节寄存器（Value）或连续内存区（Data）。</summary>
internal sealed class RegisterEntry
{
    public uint Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public RegisterAccess Access { get; init; }
    public bool IsMemory { get; init; }
    public uint Size { get; init; }                 // 寄存器固定 4；内存区为定义字节数
    public uint Value;                              // 寄存器当前值（按地址解释大小端）
    public byte[]? Data;                            // 内存区内容
    public Func<uint, GevStatus>? Validate;         // 寄存器写入前校验，返回非 Success 则拒绝
    public Action<uint>? OnWrite;                   // 寄存器写入成功后回调
}

/// <summary>
/// 相机寄存器表：GVCP ReadReg/WriteReg/ReadMem/WriteMem 的存储后端。
/// 内部用 <see cref="SortedDictionary{TKey,TValue}"/> 按地址组织：寄存器 = 4 字节值，内存区 = 字节数组。
/// 未定义地址回 InvalidAddress(0x800B)；只读被写回 WriteProtect(0x800C)；
/// 内存区访问未 4 字节对齐回 BadAlignment(0x800D)。所有公开成员线程安全。
/// </summary>
public sealed class RegisterMap
{
    private readonly object _lock = new();
    private readonly SortedDictionary<uint, RegisterEntry> _entries = new();

    /// <summary>寄存器写入完成事件（addr, value）。仅 4 字节寄存器触发，内存区不触发。</summary>
    public event Action<uint, uint>? RegisterWritten;

    /// <summary>
    /// 定义一个 4 字节寄存器。address 须 4 字节对齐；与其他区域部分重叠抛 ArgumentException，
    /// 同地址重复定义视为更新（重设初值/校验/回调）。
    /// </summary>
    public void DefineRegister(
        uint address,
        uint init,
        string name,
        RegisterAccess access = RegisterAccess.ReadWrite,
        Func<uint, GevStatus>? validate = null,
        Action<uint>? onWrite = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("寄存器名称不能为空", nameof(name));
        }

        if ((address & 0x3u) != 0)
        {
            throw new ArgumentException($"寄存器地址 0x{address:X8} 未 4 字节对齐", nameof(address));
        }

        lock (_lock)
        {
            var conflict = FindOverlappingNoLock(address, 4);
            if (conflict is not null && conflict.Address != address)
            {
                throw new ArgumentException(
                    $"寄存器 0x{address:X8} 与区域 0x{conflict.Address:X8}({conflict.Name}) 重叠", nameof(address));
            }

            _entries[address] = new RegisterEntry
            {
                Address = address,
                Name = name,
                Access = access,
                IsMemory = false,
                Size = 4,
                Value = init,
                Validate = validate,
                OnWrite = onWrite,
            };
        }
    }

    /// <summary>定义一段内存区（名称区 / XML blob 等）。address 与 size 均须 4 字节对齐且 size &gt; 0。</summary>
    public void DefineMemory(uint address, uint size, string name, RegisterAccess access = RegisterAccess.ReadWrite)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("内存区名称不能为空", nameof(name));
        }

        if (size == 0 || (size & 0x3u) != 0)
        {
            throw new ArgumentException($"内存区大小 {size} 须为 4 的倍数", nameof(size));
        }

        if ((address & 0x3u) != 0)
        {
            throw new ArgumentException($"内存区地址 0x{address:X8} 未 4 字节对齐", nameof(address));
        }

        lock (_lock)
        {
            var conflict = FindOverlappingNoLock(address, size);
            if (conflict is not null && conflict.Address != address)
            {
                throw new ArgumentException(
                    $"内存区 0x{address:X8}(len={size}) 与区域 0x{conflict.Address:X8}({conflict.Name}) 重叠", nameof(address));
            }

            _entries[address] = new RegisterEntry
            {
                Address = address,
                Name = name,
                Access = access,
                IsMemory = true,
                Size = size,
                Data = new byte[size],
            };
        }
    }

    /// <summary>读 4 字节寄存器（地址必须精确命中寄存器基址）。</summary>
    public GevStatus ReadRegister(uint address, out uint value)
    {
        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || entry.IsMemory || entry.Address != address)
            {
                value = 0;
                return GevStatus.InvalidAddress;
            }

            value = entry.Value;
            return GevStatus.Success;
        }
    }

    /// <summary>
    /// 写 4 字节寄存器：只读回 WriteProtect；validate 拒绝则回其状态；
    /// 成功后触发 onWrite 回调与 <see cref="RegisterWritten"/> 事件（均在锁外触发，回调中可再访问寄存器表）。
    /// </summary>
    public GevStatus WriteRegister(uint address, uint value)
    {
        Action<uint>? onWrite = null;
        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || entry.IsMemory || entry.Address != address)
            {
                return GevStatus.InvalidAddress;
            }

            if (entry.Access == RegisterAccess.ReadOnly)
            {
                return GevStatus.WriteProtect;
            }

            if (entry.Validate is not null)
            {
                var status = entry.Validate(value);
                if (status != GevStatus.Success)
                {
                    return status;
                }
            }

            entry.Value = value;
            onWrite = entry.OnWrite;
        }

        onWrite?.Invoke(value);
        RegisterWritten?.Invoke(address, value);
        return GevStatus.Success;
    }

    /// <summary>读内存区（address 与 dest.Length 均须 4 字节对齐，区间必须完整落在同一内存区内）。</summary>
    public GevStatus ReadMemory(uint address, Span<byte> dest)
    {
        if (dest.Length == 0)
        {
            return GevStatus.Success;
        }

        if ((address & 0x3u) != 0 || (dest.Length & 0x3) != 0)
        {
            return GevStatus.BadAlignment;
        }

        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || !entry.IsMemory ||
                (ulong)address + (uint)dest.Length > (ulong)entry.Address + entry.Size)
            {
                return GevStatus.InvalidAddress;
            }

            entry.Data!.AsSpan((int)(address - entry.Address), dest.Length).CopyTo(dest);
            return GevStatus.Success;
        }
    }

    /// <summary>写内存区（address 与 data.Length 均须 4 字节对齐；只读区回 WriteProtect）。</summary>
    public GevStatus WriteMemory(uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return GevStatus.Success;
        }

        if ((address & 0x3u) != 0 || (data.Length & 0x3) != 0)
        {
            return GevStatus.BadAlignment;
        }

        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || !entry.IsMemory ||
                (ulong)address + (uint)data.Length > (ulong)entry.Address + entry.Size)
            {
                return GevStatus.InvalidAddress;
            }

            if (entry.Access == RegisterAccess.ReadOnly)
            {
                return GevStatus.WriteProtect;
            }

            data.CopyTo(entry.Data!.AsSpan((int)(address - entry.Address)));
            return GevStatus.Success;
        }
    }

    /// <summary>内部写入：绕过只读保护（初始化名称区 / 灌入 XML blob 用）。
    /// 区域未定义时静默忽略；数据越过区域末尾时截断，不抛异常。</summary>
    public void WriteMemoryDirect(uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || !entry.IsMemory)
            {
                return;
            }

            int offset = (int)(address - entry.Address);
            if (offset < 0 || offset >= (int)entry.Size)
            {
                return;
            }

            int len = Math.Min(data.Length, (int)entry.Size - offset);
            data.Slice(0, len).CopyTo(entry.Data!.AsSpan(offset));
        }
    }

    /// <summary>内部读取：不校验对齐与权限；未定义区域以 0 填充（用于读取 XML blob 等）。</summary>
    public byte[] ReadMemoryDirect(uint address, int count)
    {
        var result = new byte[Math.Max(0, count)];
        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is not null && entry.IsMemory)
            {
                int offset = (int)(address - entry.Address);
                if (offset >= 0 && offset < (int)entry.Size)
                {
                    int len = Math.Min(result.Length, (int)entry.Size - offset);
                    if (len > 0)
                    {
                        entry.Data!.AsSpan(offset, len).CopyTo(result);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>查找覆盖起始地址 address 的条目（floor 查找 + 覆盖判断；调用方须持锁）。</summary>
    private RegisterEntry? FindContainingNoLock(uint address)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        // floor 查找：<= address 的最近键（SortedDictionary 有序，反向遍历首个 <= 即 floor；
        // 前向遍历遇首个 > address 的键即可提前结束，因其后键更大）。
        KeyValuePair<uint, RegisterEntry>? floor = null;
        foreach (var pair in _entries)
        {
            if (pair.Key <= address)
            {
                floor = pair;
            }
            else
            {
                break;
            }
        }

        if (floor is null)
        {
            return null;
        }

        var entry = floor.Value.Value;
        return (ulong)address < (ulong)floor.Value.Key + entry.Size ? entry : null;
    }

    /// <summary>查找与 [address, address+size) 有任意重叠的既有条目（调用方须持锁）。</summary>
    private RegisterEntry? FindOverlappingNoLock(uint address, uint size)
    {
        ulong begin = address;
        ulong end = (ulong)address + size;
        foreach (var pair in _entries)
        {
            ulong entryBegin = pair.Key;
            ulong entryEnd = (ulong)pair.Key + pair.Value.Size;
            if (entryBegin < end && begin < entryEnd)
            {
                return pair.Value;
            }
        }

        return null;
    }
}
