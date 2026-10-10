using System.Globalization;
using System.Text;
using GigEVision.Core.Camera;

namespace GigEVision.Core.GenICam;

/// <summary>
/// GenICam GenApi XML 生成器（GenICam 1.1 RegisterDescription，命名空间
/// http://www.genicam.org/GenApi/Version_1_1）。
/// 结构按契约第 8 节：Root → DeviceControl / ImageFormatControl / AcquisitionControl / TransportLayer；
/// 设备信息 StringReg 对齐 GigE Vision 标准引导区地址（0x48/0x68/0x88/0xA8/0xD8/0xE8），
/// 传输层特性（GevSCPHostPort 等）与 <see cref="GigEVision.Core.Protocol.Gvcp.GevRegisters"/> 流通道寄存器一一对应，
/// 缺失时 Aravis 会注入同名默认定义，显式声明可让 pylon / VisionMaster 等客户端直接使用。
/// 元素顺序遵循 GenApi 1.1 schema（NodeBase 元素在前，寄存器元素在后）。
/// 注意：文件末尾必须包含 <c>&lt;Port Name="Device"/&gt;</c> 节点——所有寄存器节点通过
/// <c>&lt;pPort&gt;Device&lt;/pPort&gt;</c> 引用它，缺失时 GenApi 报 "Port not found for node"，
/// 客户端将无法打开设备（Aravis 官方 fake camera XML 亦包含该节点）。
/// </summary>
public static class GenICamXml
{
    private const string XmlNs = "http://www.genicam.org/GenApi/Version_1_1";
    private const string SchemaLocation = "http://www.genicam.org/GenApi/GenApiSchema_Version_1_1.xsd";

    // 固定 GUID（设备身份固定，不随身份字段变化）
    private const string ProductGuid = "8e2f3a64-9c1b-4d47-b3a8-5f6e2d1c0a9b";
    private const string VersionGuid = "3d7c81a2-4b6e-4f59-9a0d-1c2e3f4a5b6c";

    /// <summary>
    /// 构建默认 GenICam XML（local 模式 blob，写入寄存器 0x00100000 内存区）。
    /// VendorName / 设备字符串节点从 identity 注入并做 XML 转义。
    /// </summary>
    /// <param name="id">相机身份。</param>
    /// <param name="width">初始宽（寄存器 0x140000 的当前值，客户端连接后实读）。</param>
    /// <param name="height">初始高（参数保留以匹配契约签名；Height 节点值由寄存器实读给出）。</param>
    public static string BuildDefault(CameraIdentity id, uint width, uint height)
    {
        _ = height; // 显式标记暂未内联到 XML（Height 走寄存器实读），避免"未使用参数"歧义
        var sb = new StringBuilder(16 * 1024);

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<RegisterDescription\n");
        sb.Append("    ModelName=\"GigEForge-VirtualCam\"\n");
        sb.Append("    VendorName=\"").Append(Esc(id.ManufacturerName)).Append("\"\n");
        sb.Append("    StandardNameSpace=\"None\"\n");   // 与 Aravis fake camera 一致（已实测可解析）；勿改 "Yes"
        sb.Append("    SchemaMajorVersion=\"1\"\n");
        sb.Append("    SchemaMinorVersion=\"1\"\n");
        sb.Append("    SchemaSubMinorVersion=\"0\"\n");
        sb.Append("    MajorVersion=\"1\"\n");
        sb.Append("    MinorVersion=\"1\"\n");
        sb.Append("    SubMinorVersion=\"0\"\n");
        sb.Append("    ProductGuid=\"").Append(ProductGuid).Append("\"\n");
        sb.Append("    VersionGuid=\"").Append(VersionGuid).Append("\"\n");
        sb.Append("    xmlns=\"").Append(XmlNs).Append("\"\n");
        sb.Append("    xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"\n");
        sb.Append("    xsi:schemaLocation=\"").Append(XmlNs).Append(' ').Append(SchemaLocation).Append("\">\n");

        // ============ Root ============
        Category(sb, "Root", "DeviceControl", "ImageFormatControl", "AcquisitionControl", "TransportLayer");

        // ============ DeviceControl（StringReg，RO；地址 = GigE Vision 标准引导区） ============
        Category(sb, "DeviceControl",
            "DeviceVendorName", "DeviceModelName", "DeviceVersion", "DeviceFirmwareVersion",
            "DeviceManufacturerInfo", "DeviceID", "DeviceUserID");
        StringReg(sb, "DeviceVendorName", 0x00000048, 32);
        StringReg(sb, "DeviceModelName", 0x00000068, 32);
        StringReg(sb, "DeviceVersion", 0x00000088, 32);
        StringReg(sb, "DeviceFirmwareVersion", 0x00000088, 32);      // 与 DeviceVersion 同区（别名）
        StringReg(sb, "DeviceManufacturerInfo", 0x000000A8, 48);
        StringReg(sb, "DeviceID", 0x000000D8, 16);
        StringReg(sb, "DeviceUserID", 0x000000E8, 64);

        // ============ ImageFormatControl ============
        Category(sb, "ImageFormatControl", "Width", "Height", "PixelFormat", "PayloadSize", "TestPattern");
        Integer(sb, "Width", 16, 8192, 2, "WidthReg");              // Min16 Max8192 Inc2
        IntReg(sb, "WidthReg", 0x00140000, "RW");
        Integer(sb, "Height", 16, 8192, 2, "HeightReg");            // 同 Width（Max8192）
        IntReg(sb, "HeightReg", 0x00140004, "RW");
        Enumeration(sb, "PixelFormat",
            new[] { ("Mono8", 17301505u), ("BayerRG8", 17301513u), ("RGB8", 35127316u), ("BGR8", 35127317u) },
            "PixelFormatReg");
        IntReg(sb, "PixelFormatReg", 0x00140008, "RW", invisible: true); // 隐藏寄存器（pValue 目标）
        Integer(sb, "PayloadSize", null, null, null, "PayloadSizeReg");
        IntReg(sb, "PayloadSizeReg", 0x00140018, "RO");
        Enumeration(sb, "TestPattern",
            new[] { ("Off", 0u), ("ColorBars", 1u), ("Gradient", 2u), ("Grid", 3u), ("MovingCrosshair", 4u) },
            "TestPatternReg");
        IntReg(sb, "TestPatternReg", 0x0014001C, "RW");

        // ============ AcquisitionControl ============
        Category(sb, "AcquisitionControl", "AcquisitionStart", "AcquisitionStop", "AcquisitionFrameRate");
        Command(sb, "AcquisitionStart", "AcquisitionStartReg");     // CommandValue=1
        CommandReg(sb, "AcquisitionStartReg", 0x0014000C);          // WO
        Command(sb, "AcquisitionStop", "AcquisitionStopReg");
        CommandReg(sb, "AcquisitionStopReg", 0x00140010);           // WO
        Float(sb, "AcquisitionFrameRate", 1, 120, "AcquisitionFrameRateReg");
        FloatReg(sb, "AcquisitionFrameRateReg", 0x00140014);        // RW

        // ============ TransportLayer（GigE Vision 传输层特性，地址 = 流通道 0 引导寄存器） ============
        Category(sb, "TransportLayer",
            "GevSCPHostPort", "GevSCPSPacketSize", "GevSCPSFireTestPacket", "GevSCPSDoNotFragment",
            "GevSCPD", "GevSCDA", "GevSCSP");
        // TLParamsLocked：流参数锁（客户端流配置期间置 1）；虚拟节点恒 0 = 不锁
        sb.Append("  <Integer Name=\"TLParamsLocked\" NameSpace=\"Custom\">\n");
        sb.Append("    <Visibility>Invisible</Visibility>\n");
        sb.Append("    <Value>0</Value>\n");
        sb.Append("    <Min>0</Min>\n");
        sb.Append("    <Max>1</Max>\n");
        sb.Append("  </Integer>\n");
        // GevSCPHostPort：GVSP 目的端口（0x0D00 低 16 位）
        Integer(sb, "GevSCPHostPort", null, null, null, "GevSCPHostPortReg", locked: true);
        MaskedIntReg(sb, "GevSCPHostPortReg", 0x00000D00, "RW", lsb: 31, msb: 16);
        // GevSCPSPacketSize：GVSP 包大小（0x0D04 低 16 位，含 IP/UDP 28B 头；设备侧会夹紧并回写）
        Integer(sb, "GevSCPSPacketSize", 604, 16028, 4, "GevSCPSPacketSizeReg", locked: true);
        MaskedIntReg(sb, "GevSCPSPacketSizeReg", 0x00000D04, "RW", lsb: 31, msb: 16);
        // GevSCPSFireTestPacket：命令（写 0x0D04 bit31，设备回发测试包供路径 MTU 探测，自清零）
        Command(sb, "GevSCPSFireTestPacket", "GevSCPSFireTestPacketReg", locked: true);
        MaskedIntReg(sb, "GevSCPSFireTestPacketReg", 0x00000D04, "RW", bit: 0);
        // GevSCPSDoNotFragment：布尔（0x0D04 bit30，本设备不分片，仅保留位状态）
        sb.Append("  <Boolean Name=\"GevSCPSDoNotFragment\" NameSpace=\"Standard\">\n");
        sb.Append("    <pIsLocked>TLParamsLocked</pIsLocked>\n");
        sb.Append("    <pValue>GevSCPSDoNotFragmentReg</pValue>\n");
        sb.Append("  </Boolean>\n");
        MaskedIntReg(sb, "GevSCPSDoNotFragmentReg", 0x00000D04, "RW", bit: 1);
        // GevSCPD：包间延迟（微秒，0x0D08 全 32 位）
        Integer(sb, "GevSCPD", null, null, null, "GevSCPDReg", locked: true);
        IntReg(sb, "GevSCPDReg", 0x00000D08, "RW");
        // GevSCDA：GVSP 目的地 IP（0x0D18 全 32 位大端；0 = 使用控制者 IP）
        Integer(sb, "GevSCDA", null, null, null, "GevSCDAReg", locked: true);
        IntReg(sb, "GevSCDAReg", 0x00000D18, "RW");
        // GevSCSP：GVSP 源端口（0x0D1C 低 16 位，RO）
        Integer(sb, "GevSCSP", null, null, null, "GevSCSPReg", locked: true);
        MaskedIntReg(sb, "GevSCSPReg", 0x00000D1C, "RO", lsb: 31, msb: 16);

        // ============ 设备端口（所有寄存器节点的 pPort 目标，缺失会导致 "Port not found for node"） ============
        sb.Append("  <Port Name=\"Device\" NameSpace=\"Standard\">\n");
        sb.Append("  </Port>\n");

        sb.Append("</RegisterDescription>\n");
        return sb.ToString();
    }

    // ================= 节点构建辅助 =================

    /// <summary>Category 节点（含 pFeature 列表）。</summary>
    private static void Category(StringBuilder sb, string name, params string[] features)
    {
        sb.Append("  <Category Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        foreach (var f in features)
            sb.Append("    <pFeature>").Append(f).Append("</pFeature>\n");
        sb.Append("  </Category>\n");
    }

    /// <summary>StringReg 节点：直接作为特性暴露（RO）。</summary>
    private static void StringReg(StringBuilder sb, string name, uint address, int length)
    {
        sb.Append("  <StringReg Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        AppendAddress(sb, address);
        sb.Append("    <Length>").Append(length).Append("</Length>\n");
        sb.Append("    <AccessMode>RO</AccessMode>\n");
        sb.Append("    <pPort>Device</pPort>\n");
        sb.Append("  </StringReg>\n");
    }

    /// <summary>Integer 特性节点：pValue 指向寄存器节点，可带 Min/Max/Inc 与 pIsLocked。</summary>
    private static void Integer(StringBuilder sb, string name, int? min, int? max, int? inc, string pValue, bool locked = false)
    {
        sb.Append("  <Integer Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        if (locked) sb.Append("    <pIsLocked>TLParamsLocked</pIsLocked>\n");
        sb.Append("    <pValue>").Append(pValue).Append("</pValue>\n");
        if (min.HasValue) sb.Append("    <Min>").Append(min.Value).Append("</Min>\n");
        if (max.HasValue) sb.Append("    <Max>").Append(max.Value).Append("</Max>\n");
        if (inc.HasValue) sb.Append("    <Inc>").Append(inc.Value).Append("</Inc>\n");
        sb.Append("  </Integer>\n");
    }

    /// <summary>
    /// MaskedIntReg 节点：Bit（单 bit）或 LSB/MSB（位域）二选一。
    /// 位序遵循 GenApi 标准（BigEndian 寄存器 bit0 = 最高有效位）：
    /// LSB=31/MSB=16 即 32 位寄存器的低 16 位，Bit 0 即最高位（0x80000000）。
    /// </summary>
    private static void MaskedIntReg(StringBuilder sb, string name, uint address, string access, int lsb = -1, int msb = -1, int bit = -1)
    {
        sb.Append("  <MaskedIntReg Name=\"").Append(name).Append("\" NameSpace=\"Custom\">\n");
        AppendAddress(sb, address);
        sb.Append("    <Length>4</Length>\n");
        sb.Append("    <AccessMode>").Append(access).Append("</AccessMode>\n");
        sb.Append("    <pPort>Device</pPort>\n");
        sb.Append("    <Cachable>NoCache</Cachable>\n");
        if (bit >= 0)
        {
            sb.Append("    <Bit>").Append(bit).Append("</Bit>\n");
        }
        else
        {
            sb.Append("    <LSB>").Append(lsb).Append("</LSB>\n");
            sb.Append("    <MSB>").Append(msb).Append("</MSB>\n");
        }

        sb.Append("    <Sign>Unsigned</Sign>\n");
        sb.Append("    <Endianess>BigEndian</Endianess>\n");
        sb.Append("  </MaskedIntReg>\n");
    }

    /// <summary>IntReg 节点：Sign=Unsigned / Endianess=BigEndian（GenICam 1.1 拼写），可 Invisible。</summary>
    private static void IntReg(StringBuilder sb, string name, uint address, string access, bool invisible = false)
    {
        sb.Append("  <IntReg Name=\"").Append(name).Append("\" NameSpace=\"Custom\">\n");
        if (invisible) sb.Append("    <Visibility>Invisible</Visibility>\n"); // NodeBase 元素须在寄存器元素之前
        AppendAddress(sb, address);
        sb.Append("    <Length>4</Length>\n");
        sb.Append("    <AccessMode>").Append(access).Append("</AccessMode>\n");
        sb.Append("    <pPort>Device</pPort>\n");
        sb.Append("    <Sign>Unsigned</Sign>\n");
        sb.Append("    <Endianess>BigEndian</Endianess>\n");
        sb.Append("  </IntReg>\n");
    }

    /// <summary>Enumeration 特性节点：EnumEntry 列表 + pValue 指向隐藏 IntReg。</summary>
    private static void Enumeration(StringBuilder sb, string name, (string Name, uint Value)[] entries, string pValue)
    {
        sb.Append("  <Enumeration Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        foreach (var e in entries)
        {
            sb.Append("    <EnumEntry Name=\"").Append(e.Name).Append("\" NameSpace=\"Standard\">\n");
            sb.Append("      <Value>").Append(e.Value.ToString(CultureInfo.InvariantCulture)).Append("</Value>\n");
            sb.Append("    </EnumEntry>\n");
        }
        sb.Append("    <pValue>").Append(pValue).Append("</pValue>\n");
        sb.Append("  </Enumeration>\n");
    }

    /// <summary>Command 特性节点：写 CommandValue 到 CommandReg。</summary>
    private static void Command(StringBuilder sb, string name, string pValue, bool locked = false)
    {
        sb.Append("  <Command Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        if (locked) sb.Append("    <pIsLocked>TLParamsLocked</pIsLocked>\n");
        sb.Append("    <pValue>").Append(pValue).Append("</pValue>\n");
        sb.Append("    <CommandValue>1</CommandValue>\n");
        sb.Append("  </Command>\n");
    }

    /// <summary>CommandReg 节点：WO 命令寄存器。</summary>
    private static void CommandReg(StringBuilder sb, string name, uint address)
    {
        sb.Append("  <CommandReg Name=\"").Append(name).Append("\" NameSpace=\"Custom\">\n");
        AppendAddress(sb, address);
        sb.Append("    <Length>4</Length>\n");
        sb.Append("    <AccessMode>WO</AccessMode>\n");
        sb.Append("    <pPort>Device</pPort>\n");
        sb.Append("    <Sign>Unsigned</Sign>\n");
        sb.Append("    <Endianess>BigEndian</Endianess>\n");
        sb.Append("  </CommandReg>\n");
    }

    /// <summary>Float 特性节点：pValue 指向 FloatReg，带 Min/Max。</summary>
    private static void Float(StringBuilder sb, string name, double min, double max, string pValue)
    {
        sb.Append("  <Float Name=\"").Append(name).Append("\" NameSpace=\"Standard\">\n");
        sb.Append("    <pValue>").Append(pValue).Append("</pValue>\n");
        sb.Append("    <Min>").Append(min.ToString(CultureInfo.InvariantCulture)).Append("</Min>\n");
        sb.Append("    <Max>").Append(max.ToString(CultureInfo.InvariantCulture)).Append("</Max>\n");
        sb.Append("  </Float>\n");
    }

    /// <summary>
    /// FloatReg 节点：仅带 Endianess（GenApi 1.1 schema 中 FloatReg 没有 Sign 元素，
    /// 补 Sign 反而会导致 schema 校验失败）。
    /// </summary>
    private static void FloatReg(StringBuilder sb, string name, uint address)
    {
        sb.Append("  <FloatReg Name=\"").Append(name).Append("\" NameSpace=\"Custom\">\n");
        AppendAddress(sb, address);
        sb.Append("    <Length>4</Length>\n");
        sb.Append("    <AccessMode>RW</AccessMode>\n");
        sb.Append("    <pPort>Device</pPort>\n");
        sb.Append("    <Endianess>BigEndian</Endianess>\n");
        sb.Append("  </FloatReg>\n");
    }

    private static void AppendAddress(StringBuilder sb, uint address)
    {
        sb.Append("    <Address>0x").Append(address.ToString("X8", CultureInfo.InvariantCulture)).Append("</Address>\n");
    }

    /// <summary>XML 转义：&lt; &gt; &amp; " ' 及控制字符（替换为 '?'）。</summary>
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '&': sb.Append("&amp;"); break;
                default:
                    sb.Append(c < 32 ? '?' : c);
                    break;
            }
        }
        return sb.ToString();
    }
}
