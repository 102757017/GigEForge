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

    /// <summary>
    /// 读内存区（address 与 dest.Length 均须 4 字节对齐）。优先整段命中同一内存区直接拷贝；
    /// 跨区或含未定义空洞的区间按 4 字节字逐字组装：命中内存区取实值、精确命中寄存器取其大端值、
    /// 其余以 0 填充（Aravis 等客户端会探测标准引导区中本设备未实现的位置，回 0 即可）。
    /// </summary>
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
            if (entry is not null && entry.IsMemory &&
                (ulong)address + (uint)dest.Length <= (ulong)entry.Address + entry.Size)
            {
                entry.Data!.AsSpan((int)(address - entry.Address), dest.Length).CopyTo(dest);
                return GevStatus.Success;
            }

            // 逐字组装回退（dest 初始为 0，未命中的字保持 0）
            for (int offset = 0; offset < dest.Length; offset += 4)
            {
                uint wordAddress = address + (uint)offset;
                var wordEntry = FindContainingNoLock(wordAddress);
                if (wordEntry is null)
                {
                    continue;
                }

                Span<byte> target = dest.Slice(offset, 4);
                if (wordEntry.IsMemory)
                {
                    if ((ulong)wordAddress + 4 <= (ulong)wordEntry.Address + wordEntry.Size)
                    {
                        wordEntry.Data!.AsSpan((int)(wordAddress - wordEntry.Address), 4).CopyTo(target);
                    }
                }
                else if (wordEntry.Address == wordAddress)
                {
                    WriteU32BigEndian(target, wordEntry.Value);
                }
            }

            return GevStatus.Success;
        }
    }

    /// <summary>
    /// 写内存区（address 与 data.Length 均须 4 字节对齐；只读区回 WriteProtect）。
    /// 整段命中单一内存区时直接拷贝；否则（命中 4 字节寄存器 / 混合区域）按 4 字节字逐字写入：
    /// 寄存器走 <see cref="WriteRegister"/> 语义（validate / onWrite / RegisterWritten 照常触发），
    /// 内存区按字拷贝。Aravis 非 legacy schema（≥1.1.0）的 GenICam 端口写全部走 WriteMem，
    /// 因此对寄存器地址的 WriteMem 必须被支持。
    /// </summary>
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

        // 快路径：整段落在同一内存区
        var direct = WriteMemoryRegion(address, data);
        if (direct.HasValue)
        {
            return direct.Value;
        }

        // 慢路径：逐 4 字节字写入（寄存器 / 内存区混合；不持锁调用公开方法，避免重入死锁）
        for (int offset = 0; offset < data.Length; offset += 4)
        {
            uint wordAddress = address + (uint)offset;
            RegisterEntry? entry;
            bool fits;
            lock (_lock)
            {
                entry = FindContainingNoLock(wordAddress);
                fits = entry is not null && (entry.IsMemory
                    ? (ulong)wordAddress + 4 <= (ulong)entry.Address + entry.Size
                    : entry.Address == wordAddress);
            }

            if (entry is null || !fits)
            {
                return GevStatus.InvalidAddress;
            }

            GevStatus st = entry.IsMemory
                ? WriteMemory(wordAddress, data.Slice(offset, 4))
                : WriteRegister(wordAddress, ReadU32BigEndian(data.Slice(offset, 4)));
            if (st != GevStatus.Success)
            {
                return st;
            }
        }

        return GevStatus.Success;
    }

    /// <summary>
    /// 整段写入单一内存区：命中返回写入状态（Success / WriteProtect），未命中或不完整覆盖返回 null（走逐字路径）。
    /// </summary>
    private GevStatus? WriteMemoryRegion(uint address, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var entry = FindContainingNoLock(address);
            if (entry is null || !entry.IsMemory ||
                (ulong)address + (uint)data.Length > (ulong)entry.Address + entry.Size)
            {
                return null;
            }

            if (entry.Access == RegisterAccess.ReadOnly)
            {
                return GevStatus.WriteProtect;
            }

            data.CopyTo(entry.Data!.AsSpan((int)(address - entry.Address)));
            return GevStatus.Success;
        }
    }

    /// <summary>大端读 u32（WriteMem 逐字路径的寄存器取值）。</summary>
    private static uint ReadU32BigEndian(ReadOnlySpan<byte> src)
        => (uint)((src[0] << 24) | (src[1] << 16) | (src[2] << 8) | src[3]);

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

        // SortedDictionary 无 GetViewBetween；按排序顺序取最后一个 key <= address 的条目（floor）。
        RegisterEntry? floor = null;
        foreach (var pair in _entries)
        {
            if (pair.Key <= address)
            {
                floor = pair.Value;
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

        return (ulong)address < (ulong)floor.Address + floor.Size ? floor : null;
    }

    /// <summary>大端写 u32 到目标字节片（内存读路径的寄存器取值）。</summary>
    private static void WriteU32BigEndian(Span<byte> dest, uint value)
    {
        dest[0] = (byte)(value >> 24);
        dest[1] = (byte)(value >> 16);
        dest[2] = (byte)(value >> 8);
        dest[3] = (byte)value;
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
