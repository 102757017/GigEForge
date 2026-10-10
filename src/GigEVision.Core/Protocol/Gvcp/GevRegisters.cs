namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GigE Vision 设备寄存器地址常量（契约 5.3）。
/// 所有地址 4 字节对齐；注释标为“内存区”的地址须用 <see cref="RegisterMap.DefineMemory"/> 定义，
/// 其余为 4 字节寄存器，用 <see cref="RegisterMap.DefineRegister"/> 定义。
/// </summary>
public static class GevRegisters
{
    public const uint Version = 0x00000004;            // GVCP 协议版本，固定 0x01020000
    public const uint ManufacturerName = 0x00000008;   // 32B ASCII（内存区）
    public const uint ModelName = 0x00000048;          // 32B ASCII（内存区）
    public const uint DeviceVersion = 0x00000088;      // 32B ASCII（内存区）
    public const uint ManufacturerInfo = 0x000000C8;   // 48B ASCII（内存区）
    public const uint SerialNumber = 0x000000F8;       // 16B ASCII（内存区）
    public const uint UserDefinedName = 0x00000108;    // 64B ASCII（内存区）
    public const uint XmlUrl = 0x00000200;             // 512B ASCII（内存区），如 local;GigEForge.xml;1048576;12345
    public const uint DeviceMode = 0x00000600;         // 设备模式，固定 0（经典 GVSP，非 GenDC）
    public const uint NetworkIfCount = 0x000006A4;     // 网络接口数 = 1
    public const uint PersistentIp = 0x00000640;       // 持久 IP
    public const uint PersistentSubnet = 0x00000644;   // 持久子网掩码
    public const uint PersistentGateway = 0x00000648;  // 持久网关
    public const uint CurrentIp = 0x000006C8;          // 当前 IP
    public const uint CurrentSubnet = 0x000006CC;      // 当前子网掩码
    public const uint CurrentGateway = 0x000006D0;     // 当前网关
    public const uint HeartbeatTimeout = 0x00000938;   // 心跳超时（毫秒，可写，下限 500）
    public const uint ControlPrivilege = 0x00000A00;   // 控制权寄存器（写 0 释放，非 0 申请控制）
    public const uint StreamChannelCount = 0x00000B00; // 流通道数 = 1
    public const uint TickFreqHigh = 0x00000BC8;       // 时间戳计数频率高 32 位（1e9 Hz → 0x3B9ACA00）
    public const uint TickFreqLow = 0x00000BCC;        // 时间戳计数频率低 32 位（0）
    public const uint Sc0Port = 0x00000D00;            // 流通道 0：主机端监听端口
    public const uint Sc0PacketSize = 0x00000D04;      // 流通道 0：GVSP 包大小（576..16000，默认 1500）
    public const uint Sc0PacketDelay = 0x00000D08;     // 流通道 0：包间延迟（微秒）
    public const uint Sc0DestIp = 0x00000D10;          // 流通道 0：目的地 IP（0 表示回退使用控制者 IP）
    public const uint Sc0PayloadSize = 0x00000D18;     // 流通道 0：负载大小（RO，随宽高自动维护）

    // —— 设备自定义寄存器区 ——
    public const uint RegWidth = 0x00140000;           // 图像宽度（16..8192 且偶数）
    public const uint RegHeight = 0x00140004;          // 图像高度（16..8192）
    public const uint RegPixelFormat = 0x00140008;     // 像素格式（PFNC 码）
    public const uint RegAcqStart = 0x0014000C;        // 写 1 启动采集
    public const uint RegAcqStop = 0x00140010;         // 写 1 停止采集
    public const uint RegAcqFrameRate = 0x00140014;    // 采集帧率（u32 fps，1..120）
    public const uint RegPayloadSize = 0x00140018;     // 负载大小（RO，0x0D18 的镜像）
    public const uint RegTestPattern = 0x0014001C;     // 测试图案（0 关，1..4）
    public const uint XmlBlobBase = 0x00100000;        // GenICam XML 内存区（256KB 上限）
}
