using System.Reflection;
using System.Resources;
using System.Text;
using GigEVision.Core.Camera;

namespace GigEVision.Core.GenICam;

/// <summary>
/// GenICam XML 提供器：加载嵌入资源 <c>GenICam/GigEForge.xml</c>（v2 — arv-test 对齐版，含
/// DeviceControl / ImageFormatControl / AcquisitionControl / AnalogControl / ChunkControl /
/// TransportLayerControl 全部标准特性），并把相机身份（厂商 / 型号）注入 RegisterDescription 属性。
/// <para>URL 与 XML 清单寄存器（0x0200 / 0x0400 URL 区、0x0928 大小、0x092C CRC32）全部按实际字节
/// 动态计算，改模板后无需手工同步；<see cref="Crc32Hash"/> 为 zlib 兼容标准 CRC32。</para>
/// <para>注意：XML 末尾必须包含 <c>&lt;Port Name="Device"/&gt;</c> 节点（模板已含）——缺失时 GenApi
/// 报 "Port not found for node"，客户端无法打开设备。</para>
/// </summary>
public static class GenICamXml
{
    /// <summary>嵌入资源全名（RootNamespace = GigEVision.Core）。</summary>
    private const string ResourceName = "GigEVision.Core.GenICam.GigEForge.xml";

    /// <summary>XML 本地 URL 文件名（URL 格式：local:///&lt;文件名&gt;;&lt;地址 hex&gt;;&lt;大小 hex&gt;）。</summary>
    public const string FileName = "GigEForge.xml";

    /// <summary>
    /// 构建当前相机的 GenICam XML 字节（UTF-8）：嵌入模板 + 身份注入。
    /// RegisterDescription 的 VendorName / ModelName 属性替换为 identity 值（XML 转义），
    /// 其余内容与模板逐字节一致。
    /// </summary>
    public static byte[] BuildXmlBytes(CameraIdentity id)
    {
        ArgumentNullException.ThrowIfNull(id);
        string template = LoadTemplate();
        string xml = template
            .Replace("VendorName=\"GigEForge\"", $"VendorName=\"{Esc(id.ManufacturerName)}\"")
            .Replace("ModelName=\"VirtualCam-01\"", $"ModelName=\"{Esc(id.ModelName)}\"");
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// GenICam XML 的 local URL（写入 0x0200 / 0x0400 URL 寄存器区）：
    /// <c>local:///GigEForge.xml;00100000;&lt;字节数 hex&gt;</c>
    /// （Aravis local URL 格式：地址在前、大小在后）。
    /// </summary>
    public static string BuildUrl(int xmlSize) =>
        $"local:///{FileName};{0x00100000:X8};{xmlSize:X8}";

    /// <summary>加载嵌入 XML 模板；资源缺失时抛出明确异常（部署损坏，快速失败）。</summary>
    private static string LoadTemplate()
    {
        var assembly = typeof(GenICamXml).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new MissingManifestResourceException(
                $"嵌入资源 {ResourceName} 不存在——请确认 GigEVision.Core.csproj 含 " +
                "<EmbeddedResource Include=\"GenICam\\GigEForge.xml\" />");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>XML 属性值转义：&lt; &gt; &amp; " ' 及控制字符（替换为 '?'）。</summary>
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

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

/// <summary>
/// 标准 CRC32（zlib 兼容，反射多项式 0xEDB88320，初值/终值 0xFFFFFFFF）——
/// GenICam XML 清单寄存器 0x092C 与打包工具使用同一算法。
/// </summary>
public static class Crc32Hash
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>计算 CRC32（zlib / gzip / GigE Vision XML 清单一致）。</summary>
    public static uint Compute(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
