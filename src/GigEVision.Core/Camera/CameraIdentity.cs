using System;
using System.Net;

namespace GigEVision.Core.Camera;

/// <summary>
/// 相机身份：GenICam XML（契约第 8 节）与引导寄存器区初始化的身份注入源。
/// 注意：GVCP Discovery 应答负载不在此构建——它直接镜像寄存器表前 248 字节（引导区标准布局），
/// 由 VirtualCamera 把本身份写入寄存器后自动生效（避免两处维护导致线格式漂移）。
/// </summary>
public sealed class CameraIdentity
{
    /// <summary>厂商名（Discovery 32B ASCII 区，超长截断 + 0 填充）。</summary>
    public string ManufacturerName { get; set; } = "GigEForge";

    /// <summary>型号名（32B）。</summary>
    public string ModelName { get; set; } = "VirtualCam-01";

    /// <summary>设备版本（32B）。</summary>
    public string DeviceVersion { get; set; } = "1.0.0";

    /// <summary>厂商附加信息（32B）。</summary>
    public string ManufacturerInfo { get; set; } = "GigE Vision Virtual Camera Suite";

    /// <summary>序列号（16B，同时充当 GenICam DeviceID）。</summary>
    public string SerialNumber { get; set; } = "GF-0000001";

    /// <summary>用户自定义名（16B，客户端搜索/显示用）。</summary>
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

    /// <summary>MAC 规范化：不足 6 字节补 0，超出截断。</summary>
    private byte[] NormalizeMac()
    {
        var mac = new byte[6];
        var src = MacAddress;
        if (src != null) Array.Copy(src, mac, Math.Min(6, src.Length));
        return mac;
    }

}
