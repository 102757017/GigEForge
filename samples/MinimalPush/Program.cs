// MinimalPush — GigEForge Server SDK 最小示例（进程内推流模式）。
// 运行后在本机上线一台 GigE Vision 虚拟相机：GVCP 3956 应答 Discovery，
// 客户端（Basler pylon / Harvesters / VisionMaster / AravisSharp）搜索 "GigEForge"
// 相机即可按帧率采集内置彩条测试图案，无需手动推送任何图像。
using System.Net.NetworkInformation;
using GigEVision.Core.Imaging;
using GigEVision.Core.Sdk;

// 1. 配置相机：宽高 / 像素格式 / 帧率 / 身份；FeedPort=0 表示关闭 UDP 喂图（默认 5010）
var server = new GigECameraServer(new CameraOptions
{
    Width = 1280,
    Height = 720,
    Format = PixelFormatCode.Mono8,
    Fps = 30,
    SerialNumber = "GF-0000001",
    UserDefinedName = "GigEForgeCam",
    FeedPort = 0,                    // 关闭 UDP 喂图：纯内置图案推流（进程内推流用 PushFrame/PushJpeg）
});

// 2. 订阅日志（含底层相机日志转发：上线/控制权/寄存器/流统计）
server.Log += msg => Console.WriteLine($"[GigEForge] {msg}");

// 3. 上线相机（GVCP 3956 开始应答 Discovery，端口占用会抛 SocketException）
server.Start();

// 4. 打印本机所有 IPv4，提示客户端连哪个网段
Console.WriteLine("本机 IPv4 地址：");
foreach (var ip in GetLocalIpv4Addresses())
{
    Console.WriteLine($"  {ip}");
}

Console.WriteLine();
Console.WriteLine("在 Basler pylon / Harvesters / VisionMaster / AravisSharp 客户端中");
Console.WriteLine("搜索 GigE 相机，应能发现名为 \"GigEForgeCam\" 的虚拟相机；");
Console.WriteLine("内置彩条测试图案以 30 fps 持续流出。按回车键退出…");
Console.ReadLine();

// 5. 下线相机并释放资源
server.Stop();

// 枚举所有已连接网卡的 IPv4 单播地址（排除回环），仅作连接性提示
static IEnumerable<string> GetLocalIpv4Addresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString());
