// RoboDKFeedServer — GigEForge UDP 喂图服务示例。
// 接收 robodk_feed.py 通过 UDP 发来的 GVF1 图像帧（默认端口 5010），解码后推入
// 虚拟相机，以 GigE Vision 流的形式供 VisionMaster / Pylon 等客户端采集。
// 用法：先 dotnet run 启动本服务，再运行 python samples/robodk_feed.py --host 127.0.0.1 --port 5010。
using GigEVision.Core.Sdk;

// 1. 默认 CameraOptions：1280x720 Mono8 30fps，UDP 喂图端口 5010（GVF1 协议）
using var server = new GigECameraServer();

// 2. 订阅日志（含相机日志与 GVF1 非法包/超限丢弃的节流提示）
server.Log += msg => Console.WriteLine($"[GigEForge] {msg}");

// 3. 解码计数：FrameDecoded 在每帧 JPEG 解码并推入相机后触发
long frames = 0;
server.FrameDecoded += _ => Interlocked.Increment(ref frames);

// 4. Ctrl+C 优雅退出：先取消循环，再走 Stop() 清理（停喂图线程 → 下线相机）
using var exitCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                 // 拦截默认强杀，保证资源优雅释放
    exitCts.Cancel();
};

// 5. 启动服务：GVCP 3956 上线 + 绑定 UDP 5010 接收喂图
server.Start();
Console.WriteLine("RoboDKFeedServer 已启动（GVCP 3956 · UDP 喂图 5010，GVF1 协议）。Ctrl+C 退出。");
Console.WriteLine("请运行: python samples/robodk_feed.py --host 127.0.0.1 --port 5010");
Console.WriteLine();

// 6. 每秒打印一次累计喂帧数与瞬时帧率
long last = 0;
while (!exitCts.IsCancellationRequested)
{
    try
    {
        await Task.Delay(1000, exitCts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    var total = Interlocked.Read(ref frames);
    Console.WriteLine($"已喂帧 {total} ({total - last} fps)");
    last = total;
}

Console.WriteLine("正在停止服务…");
server.Stop();
