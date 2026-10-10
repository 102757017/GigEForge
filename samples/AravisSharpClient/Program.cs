// ——————————————————————————————————————————————————————————————————————————————
// AravisSharpClient — 从第三方客户端视角端到端验证 GigEForge 虚拟相机。
//
// 本样例不引用 GigEVision.Core，仅依赖 NuGet 包 AravisSharp（MPL-2.0，Aravis 0.8.36
// 原生库的 C# 绑定）。Windows 上 `dotnet add package AravisSharp` 后原生 DLL 随构建
// 全部打包到输出目录，零系统安装，即可作为标准 GenICam/GigE Vision 客户端运行。
//
// 用法（两段式）：
//   dotnet run --project samples/AravisSharpClient                 # 列表模式：发现并打印所有 GigE Vision 设备
//   dotnet run --project samples/AravisSharpClient -- <DeviceId>   # 采集模式：采一帧写为 frame.pgm
//
// 注意：本样例只使用 AravisSharp README 公开的 API 面；若实际 API 与此有出入，
// 以 AravisSharp 官方文档为准：https://github.com/aboccag/AravisSharp
// ——————————————————————————————————————————————————————————————————————————————
using System.Text;
using AravisSharp;

if (args.Length == 0)
{
    return ListDevices();
}
return AcquireOnce(args[0]);

// ———————————————————————————— 列表模式 ————————————————————————————

// 无参数运行：发现局域网内所有 GigE Vision 设备并逐行打印，用于拿到 GigEForge 的 DeviceId。
static int ListDevices()
{
    Console.WriteLine("=== AravisSharp 客户端 · 设备列表模式 ===");

    AravisLibrary.RegisterResolver();       // 初始化客户端：注册 GVCP/GenICam 解析器
    CameraDiscovery.UpdateDeviceList();     // 广播 Discovery，刷新内部设备列表
    var devices = CameraDiscovery.DiscoverCameras();

    if (!devices.Any())
    {
        Console.WriteLine("未发现任何 GigE Vision 设备。请确认：");
        Console.WriteLine("  1. GigEForge 已启动（MinimalPush / RoboDKFeedServer / GigEVision.App）");
        Console.WriteLine("  2. Windows 防火墙已放行 UDP 3956（专用网络），且客户端与相机在同一网段");
        return 1;
    }

    Console.WriteLine($"发现 {devices.Count()} 台设备：");
    foreach (var device in devices)
    {
        Console.WriteLine(device.ToString());   // 每行含设备完整描述（含 DeviceId）
    }

    Console.WriteLine();
    Console.WriteLine("若列表中出现 GigEForge / VirtualCam-01 设备，复制其 DeviceId 后运行采集模式：");
    Console.WriteLine("  dotnet run --project samples/AravisSharpClient -- <DeviceId>");
    return 0;
}

// ———————————————————————————— 采集模式 ————————————————————————————

// 带参数运行：连接指定设备，设 Mono8 后采一帧，把原始像素落盘为 PGM（P5 二进制灰度图）。
static int AcquireOnce(string deviceId)
{
    Console.WriteLine($"=== AravisSharp 客户端 · 采集模式 ===");
    Console.WriteLine($"目标设备: {deviceId}");

    AravisLibrary.RegisterResolver();
    CameraDiscovery.UpdateDeviceList();

    using var camera = new Camera(deviceId);

    // 1. NodeMap 配置：目标像素格式 Mono8（PGM 可直接落盘，无需解码）
    var nodeMap = camera.GetDevice().NodeMap;
    nodeMap.SetStringFeature("PixelFormat", "Mono8");

    // 2. 宽高尽力设置：各设备取值范围不同，失败只警告不中断（沿用设备当前值）
    const int WantWidth = 1280, WantHeight = 720;
    try
    {
        nodeMap.SetIntegerFeature("Width", WantWidth);
        nodeMap.SetIntegerFeature("Height", WantHeight);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"警告：设置宽高 {WantWidth}x{WantHeight} 失败（{ex.Message}），沿用设备当前值继续");
    }

    // 3. 采集区域：从 (0,0) 起的 WantWidth x WantHeight
    camera.SetRegion(0, 0, WantWidth, WantHeight);

    // 4. 单帧采集：超时 5 秒（微秒单位）
    var buffer = camera.AcquireSingleFrame(5_000_000);
    if (buffer is null)
    {
        Console.WriteLine("错误：5 秒内未收到帧。确认 GigEForge 已启动并开始采集（GVSP 按需发流），");
        Console.WriteLine("      且防火墙放行 UDP 3956 与流端口（客户端侧监听端口）。");
        return 1;
    }

    // 5. 校验缓冲区状态与数据长度（Mono8 = 1 字节/像素）
    if (buffer.Status != ArvBufferStatus.Success)
    {
        Console.WriteLine($"错误：帧状态异常 {buffer.Status}（期待 ArvBufferStatus.Success）");
        return 1;
    }

    Span<byte> data = buffer.GetDataSpan();
    int expected = buffer.Width * buffer.Height;
    if (data.Length != expected)
    {
        Console.WriteLine($"错误：数据长度不符（GetDataSpan={data.Length}B，期望 W*H={expected}B）——" +
                          "当前像素格式可能不是 Mono8");
        return 1;
    }

    // 6. 写 frame.pgm：P5 头（magic/宽高/最大灰度）+ 原始 Mono8 字节
    using var pgm = new FileStream("frame.pgm", FileMode.Create, FileAccess.Write);
    pgm.Write(Encoding.ASCII.GetBytes($"P5\n{buffer.Width} {buffer.Height}\n255\n"));
    pgm.Write(data);

    Console.WriteLine($"采集成功：{buffer.Width}x{buffer.Height} Mono8，{data.Length} 字节，FrameId={buffer.FrameId}");
    Console.WriteLine("已写入 frame.pgm（P5 二进制灰度图）。查看建议：IrfanView / GIMP / VSCode 图片插件；");
    Console.WriteLine("或转换格式：magick frame.pgm frame.png");
    return 0;
}
