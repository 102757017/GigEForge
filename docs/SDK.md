# GigEForge Server SDK 文档

`GigEVision.Core.Sdk.GigECameraServer` 把整套虚拟相机（GVCP 3956 应答 + GVSP 发流 + GenICam XML + 可选
UDP 喂图）封装为一个类：**一行代码上线虚拟相机，一行代码推流**。底层可经 `Camera` 属性访问
`VirtualCamera`（Registers / Stats / Identity / 事件），满足更细粒度的定制需求。

- NuGet/工程引用：`src/GigEVision.Core/GigEVision.Core.csproj`（net8.0-windows）
- 命名空间：`GigEVision.Core.Sdk`；相关类型在 `GigEVision.Core.Imaging`（FrameBuffer/PixelFormatCode）

---

## 1. CameraOptions 字段表

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Width` | `int` | `1280` | 图像宽（像素）；超出 16..8192 或奇数时归一到合法值 |
| `Height` | `int` | `720` | 图像高（像素）；超出 16..8192 时夹紧 |
| `Format` | `PixelFormatCode` | `Mono8` | 像素格式（PFNC：Mono8 / BayerRG8 / RGB8 / BGR8）；不受支持时保留 Mono8 并记警告 |
| `Fps` | `int` | `30` | 采集帧率；超出 1..120 时夹紧 |
| `SerialNumber` | `string` | `"GF-0000001"` | 序列号（覆写相机身份，Discovery DeviceID） |
| `UserDefinedName` | `string` | `"GigEForgeCam"` | 用户自定义名（客户端列表显示名） |
| `FeedPort` | `int` | `5010` | UDP 喂图端口（GVF1 协议）；**0 = 关闭 UDP 喂图**（纯进程内推流模式） |
| `BindAddress` | `IPAddress?` | `null` | GVSP 流通道绑定网卡；null = 任意适配器 |

## 2. GigECameraServer API

| 成员 | 签名 | 说明 |
|------|------|------|
| 构造 | `GigECameraServer(CameraOptions? options = null)` | 构建底层 `VirtualCamera` 并经寄存器写入应用选项；尚未上线 |
| `Camera` | `VirtualCamera Camera { get; }` | 底层虚拟相机（Registers / Stats / Config / 事件等） |
| `FeedPort` | `int FeedPort { get; }` | 构造选项原值；0 = 喂图关闭 |
| `Start()` | `void Start()` | 上线相机（GVCP 3956 应答 Discovery）；`FeedPort > 0` 时额外绑定 UDP 喂图端口。端口被占用抛 `SocketException`（相机回滚离线） |
| `Stop()` | `void Stop()` | 停喂图线程 → 下线相机（不销毁，可再次 `Start`）。可重复调用 |
| `PushFrame(frame)` | `void PushFrame(FrameBuffer frame)` | 推入一帧（像素格式须与 `Camera.Config.Format` 一致）；由相机采集线程按帧率节拍发出 |
| `PushJpeg(bytes)` | `void PushJpeg(byte[] jpegBytes)` | 推入一帧 JPEG（任意分辨率，内部高质量缩放到配置宽高后转目标格式）；解码失败仅节流日志，不抛异常 |
| `Log` | `event Action<string>?` | SDK 日志（含底层相机日志转发与 GVF1 丢包节流提示） |
| `FrameDecoded` | `event Action<FrameBuffer>?` | 一帧 JPEG/字节流解码完成并已推入相机（在解码线程上触发） |
| `Dispose()` | `void IDisposable.Dispose()` | 停止并销毁（销毁后不可重启） |

> 推流与采集解耦：`PushFrame`/`PushJpeg`/UDP 喂图都只是把最新帧放入缓冲（`LatestFrameSource`），
> 真正按 `Fps` 节拍打包发送的是相机采集线程——客户端写 `AcquisitionStart`（0x0014000C=1）后开始收流。

---

## 3. 示例一：进程内推流（PushJpeg / PushFrame）

```csharp
using GigEVision.Core.Imaging;
using GigEVision.Core.Sdk;

// 1. 创建并上线一台 GigE Vision 虚拟相机
var server = new GigECameraServer(new CameraOptions {
    Width = 1920, Height = 1080,
    Format = PixelFormatCode.RGB8,
    Fps = 30,
    SerialNumber = "GF-0000001",
    UserDefinedName = "RoboDK-Cam",
    FeedPort = 0,            // 关闭 UDP 喂图（进程内推流模式）
});
server.Log += msg => Console.WriteLine($"[GigEForge] {msg}");
server.Start();              // GVCP:3956 开始应答 Discovery

// 2. 把仿真/算法生成的图像实时喂给虚拟相机
server.PushJpeg(File.ReadAllBytes("render.jpg"));   // 或 PushFrame(FrameBuffer)

// 3. 关闭
server.Stop();
```

自产 `FrameBuffer` 的最低要求（`PushFrame` 路径）：

```csharp
var frame = new FrameBuffer {
    Width = 1920, Height = 1080,
    Format = PixelFormatCode.RGB8,
    Data = rgbBytes,                 // 长度 = W*H*3，紧凑排列（R,G,B 逐像素）
    TimestampNs = 0,                 // 0 = 由采集线程统一填时间戳
    BlockId = 0,                     // block_id 由相机采集线程统一分配
};
server.PushFrame(frame);
```

---

## 4. 示例二：UDP 喂图（GVF1 协议）

任何语言都能喂图：向 `CameraOptions.FeedPort`（默认 5010）发**单条 UDP 数据报**：

```
 0                   1                   2                   3
 +-----------------------------------+-----------------------------------+
 |           "GVF1" (4B ASCII)       |            seq (u32 BE)           |
 +-----------------------------------+-----------------+-----------------+
 |          jpegLen (u32 BE)         |  JPEG 字节 ... (≤ 60000B)         |
 +-----------------------------------+-----------------+-----------------+
```

| 字段 | 偏移 | 长度 | 说明 |
|------|------|------|------|
| magic | 0 | 4B | ASCII `"GVF1"` |
| seq | 4 | 4B | 发送端帧序号（u32 大端；接收端仅用于排序/诊断，当前忽略） |
| jpegLen | 8 | 4B | JPEG 载荷字节数（u32 大端），**必须 ≤ 60000** |
| jpeg | 12 | jpegLen | JPEG 编码图像（任意分辨率） |

接收管线：JPEG 解码 → 高质量缩放到配置宽高（24bppRgb，BGR 行序）→ 行交换为紧凑 RGB8 →
`PixelConverter` 转 `CameraOptions.Format` → 推入相机采集线程。

Python 发送端（`samples/robodk_feed.py` 的核心 6 行）：

```python
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
img = Image.open("render.jpg").convert("RGB")
buf = io.BytesIO(); img.save(buf, "JPEG", quality=85)
jpg = buf.getvalue()
sock.sendto(b"GVF1" + seq.to_bytes(4, "big")
            + len(jpg).to_bytes(4, "big") + jpg, ("127.0.0.1", 5010))
```

完整可运行示例见 `samples/RoboDKFeedServer`（服务端）与 `samples/robodk_feed.py`（发送端）。

---

## 5. IFrameSource 扩展指南（CameraLink / 模拟相机等）

GigEForge 的图像源统一抽象为 `GigEVision.Core.Imaging.IFrameSource`：

```csharp
public interface IFrameSource
{
    FrameBuffer? AcquireFrame(long tickMs);   // 返回 null = 本拍无新帧（采集线程自动跳过）
}
```

内置实现：`TestPatternSource`（测试图案）、`ImageFileSource`（单图静态 / 多图 5s 轮播）、
`LatestFrameSource`（最新帧缓冲，SDK 推流与转换器共用）。

### 方式 A：实现 IFrameSource（采集线程拉模式，推荐给硬件采集卡）

以 CameraLink 采集卡 / 模拟相机为例：厂商 SDK 通常提供「等待一帧」的阻塞或回调 API，
包一层适配器即可：

```csharp
using GigEVision.Core.Camera;
using GigEVision.Core.Imaging;

// 1. 实现拉帧接口（构造参数：目标宽高与像素格式）
sealed class CameraLinkSource : IFrameSource
{
    private readonly int _w, _h;
    private readonly PixelFormatCode _fmt;

    public CameraLinkSource(int w, int h, PixelFormatCode fmt) { _w = w; _h = h; _fmt = fmt; }

    public FrameBuffer? AcquireFrame(long tickMs)
    {
        // 调用厂商 SDK 取一帧（例如 Euresys/Silicon Software 的 Grab API）
        byte[] raw = CameraLinkSdk.Grab();                  // 原始像素
        var converted = PixelConverter.Convert(raw, PixelFormatCode.Mono8, _fmt, _w, _h);
        return new FrameBuffer { Width = _w, Height = _h, Format = _fmt, Data = converted };
    }
}

// 2. 挂到虚拟相机：采集线程按 Fps 节拍调用 AcquireFrame
var camera = new VirtualCamera();
camera.Config.Width = 1280; camera.Config.Height = 1024;
camera.Config.Format = PixelFormatCode.Mono8; camera.Config.Fps = 60;
camera.Source = new CameraLinkSource(1280, 1024, PixelFormatCode.Mono8);
camera.Start();             // 上线（GVCP 3956）
camera.StartAcquisition();  // 开始按帧率发流
```

要点：

- `AcquireFrame` 返回 `null` 时该拍跳过发送（不会发出空帧）；
- `Format` 必须是 4 种受支持 PFNC 之一，否则用 `PixelConverter.Convert` 先转；
- 该模式下**不要**再设置 `Source` 为 `LatestFrameSource` 之外的实现后又调用 `PushExternalFrame`（两者互斥使用）。

### 方式 B：直接 PushExternalFrame（推模式）

事件驱动的硬件（帧回调线程）不必实现接口，直接把现成 `FrameBuffer` 推给相机的最新帧缓冲：

```csharp
camera.PushExternalFrame(frame);   // 内部等价 LatestFrameSource.Push，采集线程按节拍取走
```

SDK 场景用 `GigECameraServer.PushFrame(frame)`（等价封装）。适用：USB 相机桥接（见 App 转换器页）、
算法产物推流、跨进程图像共享。

---

## 6. 性能建议

- **巨型帧**：千兆网环境建议把 `SC0PacketSize`（0x0D04，客户端经 GenICam `GevSCPSPacketSize` 写入）设为
  **9000**，可显著降低包率与 CPU 占用（交换机/网卡需开启 Jumbo Frame；写入自动 clamp [576,16000]）；
- **降低帧率**：`Fps`（0x00140014）按需下调；1280×720 Mono8 30fps 约 27 MB/s，1920×1080 RGB8 30fps
  约 186 MB/s，已接近千兆线速，降帧率比丢包重发划算；
- **打包尺寸与延迟**：`SC0PacketDelay`（0x0D08，微秒）可在弱交换机上防突发丢包，但会拉长单帧发送时间；
  高丢包场景优先开巨型帧，其次再考虑加延迟；
- **喂图侧**：GVF1 单报 ≤ 60000B，发送前先 `thumbnail` 缩放并适当降 quality（脚本已内置超限重压一次）；
  UDP 有丢包，帧率不宜超过服务端 `Fps`（多余的帧只会互相覆盖）；
- **预览开销**：客户端侧 4K 高帧率建议关闭实时预览缩放（如 pylon 中选择 off），让 CPU 资源留给收包线程。
