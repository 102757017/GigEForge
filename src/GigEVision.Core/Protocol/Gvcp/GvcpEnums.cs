namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GVCP 命令码（GigE Vision 1.2）。约定：ACK 命令码 = 对应命令码 + 1。
/// </summary>
public enum GvcpCommand : ushort
{
    /// <summary>发现命令（允许单播或广播，设备端都必须应答）。</summary>
    DiscoveryCmd = 0x0002,

    /// <summary>发现应答（payload 为 250 字节设备描述，见契约 5.2）。</summary>
    DiscoveryAck = 0x0003,

    /// <summary>强制 IP 命令（payload 含 MAC + IP/子网/网关）。</summary>
    ForceIpCmd = 0x0004,

    /// <summary>强制 IP 应答。</summary>
    ForceIpAck = 0x0005,

    /// <summary>应答待发（TVU 场景占位，本实现仅保留常量）。</summary>
    PendingAck = 0x0006,

    /// <summary>流重发请求（payload = block_id u32 + first u32 + last u32；到达 GVCP 端口，转交 GVSP 发送器处理）。</summary>
    ResendCmd = 0x0040,

    /// <summary>流重发应答（本实现不发送该 ACK，重发直接以数据包形式回应）。</summary>
    ResendAck = 0x0041,

    /// <summary>读寄存器（payload = N×u32 地址）。</summary>
    ReadRegCmd = 0x0080,

    /// <summary>读寄存器应答（payload = N×u32 值）。</summary>
    ReadRegAck = 0x0081,

    /// <summary>写寄存器（payload = N×(地址 u32 + 值 u32)）。</summary>
    WriteRegCmd = 0x0082,

    /// <summary>写寄存器应答（payload = index u32 = 成功写入个数）。</summary>
    WriteRegAck = 0x0083,

    /// <summary>读内存（payload = 地址 u32 + 数量 u32，数量按 4 字节对齐向上取整）。</summary>
    ReadMemCmd = 0x0084,

    /// <summary>读内存应答（payload = 地址 u32 回显 + 数据）。</summary>
    ReadMemAck = 0x0085,

    /// <summary>写内存（payload = 地址 u32 + 数据，数据按 4 字节对齐填充）。</summary>
    WriteMemCmd = 0x0086,

    /// <summary>写内存应答（payload = 地址 u32 回显）。</summary>
    WriteMemAck = 0x0087,
}

/// <summary>GVCP 全局状态码（应答包 status 字段，即包头前两字节：成功 0x0000，错误 0x8xxx）。</summary>
public enum GevStatus : ushort
{
    /// <summary>成功。</summary>
    Success = 0x0000,

    /// <summary>命令未实现（未知命令）。</summary>
    NotImplemented = 0x8001,

    /// <summary>通用错误。</summary>
    Error = 0x8002,

    /// <summary>访问被拒绝（控制权被他方占用）。</summary>
    AccessDenied = 0x8003,

    /// <summary>端口被占用。</summary>
    PortInUse = 0x8006,

    /// <summary>包头非法（如 ForceIP 的 MAC 不匹配）。</summary>
    InvalidHeader = 0x8009,

    /// <summary>地址未定义。</summary>
    InvalidAddress = 0x800B,

    /// <summary>写保护（只读寄存器/内存区被写）。</summary>
    WriteProtect = 0x800C,

    /// <summary>对齐错误（内存区访问未 4 字节对齐）。</summary>
    BadAlignment = 0x800D,

    /// <summary>功能当前不可用。</summary>
    NotAvailable = 0x8013,

    /// <summary>参数非法（如宽高超限、像素格式不支持）。</summary>
    InvalidParameter = 0x8015,
}

/// <summary>GVCP 包头 flags 字段（1 字节）。</summary>
[Flags]
public enum GvcpFlags : byte
{
    /// <summary>bit0：需要应答（命令包置位；ACK 包同样置位）。</summary>
    AckRequired = 0x01,

    /// <summary>bit1：广播请求（Discovery / ForceIP 常见）。</summary>
    Broadcast = 0x02,
}
