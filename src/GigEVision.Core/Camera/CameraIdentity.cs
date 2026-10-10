using System;
using System.Net;

namespace GigEVision.Core.Camera;

/// <summary>
/// 相机身份：GigE Vision Discovery 报文（契约 5.2）与 GenICam XML（第 8 节）的身份注入源。
/// </summary>
public sealed class CameraIdentity
{
    /// <summary>厂商名（Discovery 32B ASCII 区，超长截断 + 0 填充）。</summary>
    public string ManufacturerName { get; set; } = "GigEForge";

    /// <summary>型号名（32B）。</summary>
    public string ModelName { get; set; } = "VirtualCam-01";

    /// <summary>设备版本（32B）。</summary>
    public string DeviceVersion { get; set; } = "1.0.0";

    /// <summary>厂商附加信息（48B）。</summary>
    public string ManufacturerInfo { get; set; } = "GigE Vision Virtual Camera Suite";

    /// <summary>序列号（16B，同时充当 GenICam DeviceID）。</summary>
    public string SerialNumber { get; set; } = "GF-0000001";

    /// <summary>用户自定义名（64B，客户端搜索/显示用）。</summary>
    public string UserDefinedName { get; set; } = "GigEForgeCam";

    /// <summary>MAC 地址（6 字节；02 前缀 = 本地管理位，虚拟网卡惯用）。</summary>
    public byte[] MacAddress { get; set; } = { 0x02, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E };

    /// <summary>当前 IP（默认 0.0.0.0 = LLA 生效）。</summary>
    public IPAddress CurrentIp { get; set; } = IPAddress.Any;

    /// <summary>当前子网掩码。</summary>
    public IPAddress CurrentSubnetMask { get; set; } = IPAddress.Any;

    /// <summary>当前网关。</summary>
    public IPAddress CurrentGateway { get; set; } = IPAddress.Any;

    /// <summary>MAC 字符串，格式 "02:1A:2B:3C:4D:5E"。</summary>
    public string MacString
    {
        get
        {
            var m = NormalizeMac();
            return $"{m[0]:X2}:{m[1]:X2}:{m[2]:X2}:{m[3]:X2}:{m[4]:X2}:{m[5]:X2}";
        }
    }

    /// <summary>
    /// 构建 250 字节 DiscoveryAck 负载（契约 5.2，全部大端）：
    /// [0..1] spec 1.2；[2..3] MAC 高 16 位；[4..7] MAC 低 32 位；[8..19] IP/子网/网关；
    /// [20..21] ip_options=0；[22..245] 五段 ASCII 名称区；[246]=0x05；[247]=0x01；[248..249]=0。
    /// </summary>
    public byte[] BuildDiscoveryPayload()
    {
        var p = new byte[250];
        p[0] = 0x01; // spec_major = 1
        p[1] = 0x02; // spec_minor = 2

        var mac = NormalizeMac();
        p[2] = mac[0];                                              // MAC 高 16 位（大端拆分）
        p[3] = mac[1];
        p[4] = mac[2];                                              // MAC 低 32 位
        p[5] = mac[3];
        p[6] = mac[4];
        p[7] = mac[5];

        WriteIp(p, 8, CurrentIp);          // 当前 IP（网络序 = 大端）
        WriteIp(p, 12, CurrentSubnetMask); // 当前子网
        WriteIp(p, 16, CurrentGateway);    // 当前网关
        // [20..21] ip_options = 0（保持零）

        WriteAscii(p, 22, 32, ManufacturerName);   // ManufacturerName
        WriteAscii(p, 54, 32, ModelName);          // ModelName
        WriteAscii(p, 86, 32, DeviceVersion);      // DeviceVersion
        WriteAscii(p, 118, 48, ManufacturerInfo);  // ManufacturerInfo
        WriteAscii(p, 166, 16, SerialNumber);      // SerialNumber
        WriteAscii(p, 182, 64, UserDefinedName);   // UserDefinedName

        p[246] = 0x05; // ip_config_options：LLA | Persistent 可配置
        p[247] = 0x01; // ip_config_current：LLA 生效
        // [248..249] 保留 = 0
        return p;
    }

    /// <summary>MAC 规范化：不足 6 字节补 0，超出截断。</summary>
    private byte[] NormalizeMac()
    {
        var mac = new byte[6];
        var src = MacAddress;
        if (src != null) Array.Copy(src, mac, Math.Min(6, src.Length));
        return mac;
    }

    /// <summary>写 IPv4 大端 4 字节（非 IPv4 地址按 0.0.0.0 处理）。</summary>
    private static void WriteIp(byte[] buf, int off, IPAddress? ip)
    {
        byte[] b = ip != null && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ip.GetAddressBytes() // GetAddressBytes 本身即网络序（大端）
            : new byte[4];
        buf[off] = b[0];
        buf[off + 1] = b[1];
        buf[off + 2] = b[2];
        buf[off + 3] = b[3];
    }

    /// <summary>
    /// 写 ASCII 名称区：超长截断（保留末位 0 结尾），剩余 0 填充；
    /// 非 ASCII 可打印字符替换为 '?'。
    /// </summary>
    private static void WriteAscii(byte[] buf, int off, int size, string? s)
    {
        Array.Clear(buf, off, size);
        if (string.IsNullOrEmpty(s)) return;
        int n = Math.Min(size - 1, s.Length);
        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            buf[off + i] = c >= 32 && c <= 126 ? (byte)c : (byte)'?';
        }
    }
}
