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
    public const uint ManufacturerInfo = 0x000000A8;   // 32B ASCII（内存区）
    public const uint SerialNumber = 0x000000D8;       // 16B ASCII（内存区）
    public const uint UserDefinedName = 0x000000E8;    // 16B ASCII（内存区）

    // ===== GenICam XML 清单 =====
    public const uint XmlUrl = 0x00000200;             // 512B ASCII（内存区），local:///GigEForge.xml;&lt;地址 hex&gt;;&lt;大小 hex&gt;
    public const uint XmlUrl1 = 0x00000400;            // 512B ASCII（内存区），第二 XML URL，恒为空（客户端会探测读取）

    // ===== 网络接口配置 =====
    public const uint NetworkIfCount = 0x00000600;     // 网络接口数 = 1
    public const uint PersistentIp = 0x0000064C;       // 持久 IP（接口 0）
    public const uint PersistentSubnet = 0x0000065C;   // 持久子网掩码
    public const uint PersistentGateway = 0x0000066C;  // 持久网关
    public const uint LinkSpeed0 = 0x00000670;         // 链路速率 Mbps（RO，1000 = 千兆）

    // ===== 通道数量 / GenICam XML 清单 / GVCP 能力 / 心跳 / 时间戳 =====
    public const uint MessageChannelCount = 0x00000900; // 消息（事件）通道数 = 0
    public const uint StreamChannelCount = 0x00000904;  // 流通道数 = 1（Aravis create_stream 必读）
    public const uint ActionSignalCount = 0x00000908;   // Action 信号数 = 0
    public const uint ActiveLinkCount = 0x00000910;     // 活动链路数 = 1
    public const uint XmlFileCount = 0x00000920;        // GenICam XML 文件数 = 1（RO）
    public const uint XmlFileSelect = 0x00000924;       // 选中 XML 文件索引 = 0（RO）
    public const uint XmlFileSize = 0x00000928;         // XML 文件大小（字节，动态生成后回填，RO）
    public const uint XmlFileCrc = 0x0000092C;          // XML 文件 CRC32（zlib 兼容多项式，动态回填，RO）
    public const uint XmlFileVersion = 0x00000930;      // XML 文件版本 1.0（RO）
    public const uint GvcpCapability = 0x00000934;      // GVCP 能力位图（RO）：bit0 Concatenation |
                                                        // bit1 WriteMem | bit2 PacketResend → 0x00000007
                                                        // ★ 绝对不能放 XML URL 字符串（会被客户端当能力位解析）
    public const uint HeartbeatTimeout = 0x00000938;    // 心跳超时（毫秒，可写，下限 500）
    public const uint TickFreqHigh = 0x0000093C;        // 时间戳计数频率高 32 位（1e9 Hz → 0）
    public const uint TickFreqLow = 0x00000940;         // 时间戳计数频率低 32 位（0x3B9ACA00）
    public const uint TimestampControl = 0x00000944;    // 时间戳控制（bit0 锁存请求）
    public const uint TimestampLatchHigh = 0x00000948;  // 锁存时间戳高 32 位（RO）
    public const uint TimestampLatchLow = 0x0000094C;   // 锁存时间戳低 32 位（RO）
    public const uint DiscoveryAckDelay = 0x00000950;   // Discovery ACK 延迟（ms，0 = 立即）
    public const uint DeviceConfiguration = 0x00000954; // 设备配置（0 = 无持久配置）
    public const uint PendingAckTimeout = 0x00000958;   // ACK 待发超时（0 = 不支持 PendingAck）

    // ===== 控制通道 =====
    public const uint ControlPrivilege = 0x00000A00;   // 控制权寄存器（写 0 释放，非 0 申请控制）

    // ===== 流通道 0（GigE Vision 标准布局：基址 0x0D00、通道步进 0x40）=====
    // ★ 地址与语义逐字对齐 Aravis 注入节点（arvgvdevice.c）/ Wireshark GVCP dissector，勿回退旧布局：
    //   0x0D00 = GevSCPHostPort（RW）：客户端把它收流 socket 的 UDP 端口写进来（建流与测试包探测前都会写），
    //            设备据此决定 GVSP 目的端口——旧版误定义为 RO Capability 会导致 aravis 测试包探测全超时。
    //   0x0D04 = GevSCPSPacketSize（RW）：值 bit15:0 = 包大小（IP 包总字节数，4 对齐），
    //            bit31 = FireTestPacket（GenApi <Bit>0</Bit>，写 1 触发后自清零）、bit30 = DoNotFragment、
    //            bit29 = BigEndian。寄存器按客户端写入原始值存储（读回 == 写入，低 16 位），勿做 +28 夹紧。
    //   0x0D1C = GevSCSP（RO）：相机 GVSP 流源端口（写容忍）。
    //   0x0D20 = GevSCCA 能力（RO 恒 0）：不声明 multipart → arv-test Multipart 检查按预期 SUCCESS。
    public const uint Sc0HostPort = 0x00000D00;      // GevSCPHostPort：RW，值 bit15:0 = 客户端收流端口
    public const uint Sc0PacketSize = 0x00000D04;    // GevSCPSPacketSize：RW，值 bit15:0 = 包大小（IP 总长）；
                                                     // bit31=FireTestPacket（自清零）、bit30=DNF、bit29=BigEndian
    public const uint Sc0PacketDelay = 0x00000D08;   // GevSCPD：RW，包间延迟（微秒）
    public const uint Sc0DestIp = 0x00000D18;        // GevSCDA：RW，GVSP 目的地 IPv4（4 字节即网络序；0 回退控制者 IP）
    public const uint Sc0SourcePort = 0x00000D1C;    // GevSCSP：RO，相机流源端口（低 16 位；外部写容忍不生效）
    public const uint Sc0Capability = 0x00000D20;    // GevSCCA：RO，恒 0（无 multipart / 多区能力；写容忍）
    public const uint Sc0Configuration = 0x00000D24; // SC 配置存储（RW，仅存储；bit 无语义）

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

/// <summary>
/// GenICam 标准特性寄存器区（0x00010000 起，与 GenICam\GigEForge.xml v2 特性节点一一对应）。
/// 客户端（arv-test / VisionMaster / pylon）经 XML 映射访问本区：Float 型寄存器存 IEEE754 单精度位图。
/// </summary>
public static class GevFeature
{
    public const uint BlockFirst = 0x00010000;

    public const uint Width = 0x00010000;              // RW，1..SensorWidth
    public const uint Height = 0x00010004;             // RW，1..SensorHeight
    public const uint OffsetX = 0x00010008;            // RW，仅 0
    public const uint OffsetY = 0x0001000C;            // RW，仅 0
    public const uint SensorWidth = 0x00010010;        // RO，1280
    public const uint SensorHeight = 0x00010014;       // RO，720
    public const uint PixelFormat = 0x00010018;        // RW，0x01080001 = Mono8（XML 仅枚举 Mono8）
    public const uint AcquisitionMode = 0x0001001C;    // RW，0=Off 1=Continuous 2=SingleFrame
    public const uint AcquisitionCommand = 0x00010020; // WO 命令，1=AcquisitionStart 0=AcquisitionStop
    public const uint TriggerMode = 0x00010024;        // RW，0=Off 1=On
    public const uint TriggerSource = 0x00010028;      // RW，0=Line0 1=Software
    public const uint TriggerSoftware = 0x0001002C;    // WO 命令，1=触发一帧
    public const uint ExposureTime = 0x00010030;       // RW，IEEE754 单精度（µs）
    public const uint Gain = 0x00010034;               // RW，IEEE754 单精度（dB）
    public const uint FrameRate = 0x00010038;          // RW，IEEE754 单精度（Hz）
    public const uint TriggerActivation = 0x0001003C;  // RW，0=RisingEdge
    public const uint ChunkModeActive = 0x00010040;    // RW，0/1
    public const uint ChunkEnableWidth = 0x00010044;   // RW，0/1（ChunkSelector=Width 时 ChunkEnable 写入处）
    public const uint ChunkEnableHeight = 0x00010048;  // RW，0/1（ChunkSelector=Height 时）
    public const uint ChunkSelector = 0x0001004C;      // RW，0=Width 1=Height
    public const uint TriggerSelector = 0x00010050;    // RW，0=FrameStart

    public const uint BlockLast = 0x00010050;
}
