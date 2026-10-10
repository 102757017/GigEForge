namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GigE Vision 引导寄存器地址常量（GigE Vision 1.2 标准引导区布局 + 设备自定义区）。
/// 引导区地址与 Aravis 0.8.x / pylon 等客户端硬编码（或注入默认 GenICam 特性）的布局一致，
/// 客户端不经过 XML 即可直接访问这些地址，因此不可随意挪动。
/// 注释标为“内存区”的地址须用 <see cref="RegisterMap.DefineMemory"/> 定义，
/// 其余为 4 字节寄存器，用 <see cref="RegisterMap.DefineRegister"/> 定义。
/// </summary>
public static class GevRegisters
{
    // ===== 设备信息（标准引导区 0x0000-0x00E8）=====
    public const uint Version = 0x00000000;            // 协议版本 major<<16|minor（v1.2 → 0x00010002）
    public const uint DeviceMode = 0x00000004;         // 设备模式：bit31=大端设备（本设备小端 → 0）
    public const uint MacHigh = 0x00000008;            // MAC 高 32 位（仅低 16 位有效）
    public const uint MacLow = 0x0000000C;             // MAC 低 32 位
    public const uint SupportedIpConfig = 0x00000010;  // 支持的 IP 配置方式（bit0 持久 / bit1 DHCP / bit2 LLA）
    public const uint CurrentIpConfig = 0x00000014;    // 当前生效的 IP 配置方式
    public const uint CurrentIp = 0x00000024;          // 当前 IP（接口 0）
    public const uint CurrentSubnet = 0x00000034;      // 当前子网掩码
    public const uint CurrentGateway = 0x00000044;     // 当前网关
    public const uint ManufacturerName = 0x00000048;   // 32B ASCII（内存区）
    public const uint ModelName = 0x00000068;          // 32B ASCII（内存区）
    public const uint DeviceVersion = 0x00000088;      // 32B ASCII（内存区）
    public const uint ManufacturerInfo = 0x000000A8;   // 48B ASCII（内存区）
    public const uint SerialNumber = 0x000000D8;       // 16B ASCII（内存区）
    public const uint UserDefinedName = 0x000000E8;    // 64B ASCII（内存区）

    // ===== GenICam XML 清单 =====
    public const uint XmlUrl = 0x00000200;             // 512B ASCII（内存区），local:///GigEForge.xml;&lt;地址 hex&gt;;&lt;大小 hex&gt;
    public const uint XmlUrl1 = 0x00000400;            // 512B ASCII（内存区），第二 XML URL，恒为空（客户端会探测读取）

    // ===== 网络接口配置 =====
    public const uint NetworkIfCount = 0x00000600;     // 网络接口数 = 1
    public const uint PersistentIp = 0x0000064C;       // 持久 IP（接口 0）
    public const uint PersistentSubnet = 0x0000065C;   // 持久子网掩码
    public const uint PersistentGateway = 0x0000066C;  // 持久网关

    // ===== 通道数量 / GVCP 能力 / 心跳 / 时间戳 =====
    public const uint MessageChannelCount = 0x00000900; // 消息（事件）通道数 = 0
    public const uint StreamChannelCount = 0x00000904;  // 流通道数 = 1（Aravis create_stream 必读）
    public const uint GvcpCapability = 0x00000934;      // GVCP 能力位图（RO）：bit1=WriteMem | bit2=PacketResend → 0x6
    public const uint HeartbeatTimeout = 0x00000938;    // 心跳超时（毫秒，可写，下限 500）
    public const uint TickFreqHigh = 0x0000093C;        // 时间戳计数频率高 32 位（1e9 Hz → 0）
    public const uint TickFreqLow = 0x00000940;         // 时间戳计数频率低 32 位（0x3B9ACA00）
    public const uint TimestampControl = 0x00000944;    // 时间戳控制（bit0 锁存请求）
    public const uint TimestampLatchHigh = 0x00000948;  // 锁存时间戳高 32 位（RO）
    public const uint TimestampLatchLow = 0x0000094C;   // 锁存时间戳低 32 位（RO）

    // ===== 控制通道 =====
    public const uint ControlPrivilege = 0x00000A00;   // 控制权寄存器（写 0 释放，非 0 申请控制）

    // ===== 流通道 0 =====
    public const uint Sc0Port = 0x00000D00;            // GVSP 目的端口（低 16 位有效；GenICam GevSCPHostPort）
    public const uint Sc0PacketSize = 0x00000D04;      // 低 16 位 = GVSP 包大小（含 IP/UDP 28B 头）；
                                                       // bit31=FireTestPacket（自清零）、bit30=DoNotFragment、bit29=BigEndian
    public const uint Sc0PacketDelay = 0x00000D08;     // 包间延迟（微秒；GenICam GevSCPD）
    public const uint Sc0DestIp = 0x00000D18;          // GVSP 目的地 IP（0 = 回退使用控制者 IP；GenICam GevSCDA）
    public const uint Sc0SourcePort = 0x00000D1C;      // GVSP 源端口（低 16 位，RO，设备自维护；GenICam GevSCSP）

    // ===== 设备自定义寄存器区 =====
    public const uint RegWidth = 0x00140000;           // 图像宽度（16..8192 且偶数）
    public const uint RegHeight = 0x00140004;          // 图像高度（16..8192）
    public const uint RegPixelFormat = 0x00140008;     // 像素格式（PFNC 码）
    public const uint RegAcqStart = 0x0014000C;        // 写 1 启动采集
    public const uint RegAcqStop = 0x00140010;         // 写 1 停止采集
    public const uint RegAcqFrameRate = 0x00140014;    // 采集帧率（u32 fps，1..120）
    public const uint RegPayloadSize = 0x00140018;     // 负载大小（RO，随宽高/格式自动维护）
    public const uint RegTestPattern = 0x0014001C;     // 测试图案（0 关，1..4）
    public const uint XmlBlobBase = 0x00100000;        // GenICam XML 内存区（256KB 上限）
}
