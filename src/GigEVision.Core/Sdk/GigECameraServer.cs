using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GigEVision.Core.Camera;
using GigEVision.Core.Imaging;
using GigEVision.Core.Protocol.Gvcp;

namespace GigEVision.Core.Sdk;

/// <summary>
/// SDK 服务器选项：图像参数与相机身份覆写 + UDP 喂图端口。
/// 宽/高/格式/帧率在构造时经寄存器写入生效（范围校验与 Config 同步一次完成）。
/// </summary>
public sealed class CameraOptions
{
    /// <summary>图像宽（像素；超出 16..8192 或奇数时归一到合法值）。</summary>
    public int Width { get; set; } = 1280;

    /// <summary>图像高（像素；超出 16..8192 时归一到合法值）。</summary>
    public int Height { get; set; } = 720;

    /// <summary>像素格式（PFNC；不受支持时保留默认 Mono8）。</summary>
    public PixelFormatCode Format { get; set; } = PixelFormatCode.Mono8;

    /// <summary>采集帧率（fps；超出 1..120 时夹紧）。</summary>
    public int Fps { get; set; } = 30;

    /// <summary>序列号（覆写相机身份，Discovery DeviceID）。</summary>
    public string SerialNumber { get; set; } = "GF-0000001";

    /// <summary>用户自定义名（覆写相机身份，客户端列表显示名）。</summary>
    public string UserDefinedName { get; set; } = "GigEForgeCam";

    /// <summary>UDP 喂图端口（GVF1 协议，契约 5.6）；0 = 关闭 UDP 喂图。</summary>
    public int FeedPort { get; set; } = 5010;

    /// <summary>GVSP 流通道绑定网卡；null = 任意适配器。</summary>
    public IPAddress? BindAddress { get; set; }
}

/// <summary>
/// GigE Vision 虚拟相机 Server SDK：一行代码创建并上线虚拟相机，
/// 支持 <see cref="PushFrame"/> / <see cref="PushJpeg"/> 进程内推流与 GVF1 UDP 喂图（RoboDK 等外部源）。
/// <para>图像管线：JPEG 解码 → 高质量缩放到配置宽高（Format24bppRgb，BGR 行序）→ 取行交换为紧凑 RGB8
/// → <see cref="PixelConverter"/> 转目标格式 → FrameBuffer（时间戳 = 单调时钟纳秒）
/// → <see cref="VirtualCamera.PushExternalFrame"/>，由相机采集线程按帧率节拍发送。</para>
/// 客户端（Pylon/VM/Aravis）正常连接 GVCP 后写 AcquisitionStart 即开始收流。
/// </summary>
public sealed class GigECameraServer : IDisposable
{
    /// <summary>GVF1 包头长度："GVF1"(4) + seq(u32) + jpegLen(u32)。</summary>
    private const int Gvf1HeaderSize = 12;

    /// <summary>单条喂图 JPEG 字节数上限（契约 5.6）。</summary>
    private const int MaxJpegBytes = 60000;

    /// <summary>喂图线程退出等待上限（毫秒）。</summary>
    private const int ThreadJoinTimeoutMs = 1500;

    /// <summary>丢弃/解码失败日志的节流窗口（毫秒）。</summary>
    private const int DropLogIntervalMs = 3000;

    private static readonly byte[] Gvf1Magic = Encoding.ASCII.GetBytes("GVF1");

    private readonly CameraOptions _options;
    private readonly VirtualCamera _camera;
    private readonly object _stateLock = new();

    private UdpClient? _feedUdp;
    private CancellationTokenSource? _feedCts;
    private Thread? _feedThread;
    private bool _started;
    private long _receivedFrames;                       // 累计解码帧数（UDP 喂图 + PushJpeg）
    private bool _disposed;

    private readonly object _dropLogLock = new();       // 节流日志状态
    private int _droppedSinceLog;
    private long _nextDropLogAtMs;

    /// <summary>创建 SDK 服务器（构建底层 <see cref="VirtualCamera"/> 并应用选项；尚未上线）。</summary>
    public GigECameraServer(CameraOptions? options = null)
    {
        _options = options ?? new CameraOptions();
        _camera = new VirtualCamera(new CameraIdentity
        {
            SerialNumber = _options.SerialNumber,
            UserDefinedName = _options.UserDefinedName,
        });
        _camera.Log += message => Log?.Invoke(message);
        _camera.BindToInterface(_options.BindAddress);
        ApplyOptions();
    }

    /// <summary>底层虚拟相机（可进一步访问 Registers / Stats / 事件等）。</summary>
    public VirtualCamera Camera => _camera;

    /// <summary>UDP 喂图端口（构造选项原值；0 = 关闭）。</summary>
    public int FeedPort => _options.FeedPort;

    /// <summary>SDK 日志（含底层相机日志转发）。</summary>
    public event Action<string>? Log;

    /// <summary>一帧 JPEG/字节流解码完成并已推入相机（在解码线程上触发）。</summary>
    public event Action<FrameBuffer>? FrameDecoded;

    // ———————————————————————————— 启停 ————————————————————————————

    /// <summary>
    /// 启动服务：上线相机（GVCP 3956 应答 Discovery）；FeedPort &gt; 0 时额外绑定 UDP 喂图端口。
    /// 端口被占用时抛 SocketException（相机随之回滚为离线）。
    /// </summary>
    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _camera.Start();
            if (_options.FeedPort > 0)
            {
                try
                {
                    StartFeedListener();
                }
                catch
                {
                    _camera.Stop();             // 喂图端口绑定失败：回滚相机
                    throw;
                }
            }

            _started = true;
        }

        Log?.Invoke(_options.FeedPort > 0
            ? $"SDK 服务已启动：GVCP 3956 在线，UDP 喂图端口 {_options.FeedPort}（GVF1 协议）"
            : "SDK 服务已启动：GVCP 3956 在线，UDP 喂图已关闭（进程内推流模式）");
    }

    /// <summary>停止服务：停喂图线程 → 下线相机（不销毁，可再次 Start）。可重复调用。</summary>
    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        UdpClient? udp;
        lock (_stateLock)
        {
            thread = _feedThread;
            _feedThread = null;
            cts = _feedCts;
            _feedCts = null;
            udp = _feedUdp;
            _feedUdp = null;
            _started = false;
        }

        try { cts?.Cancel(); } catch { /* 忽略取消异常 */ }
        try { udp?.Dispose(); } catch { /* Dispose 内部 Close，解除 Receive 阻塞 */ }
        JoinThread(thread);
        try { cts?.Dispose(); } catch { /* 忽略释放异常 */ }

        _camera.Stop();
        Log?.Invoke($"SDK 服务已停止（本会话累计解码 {Interlocked.Read(ref _receivedFrames)} 帧）");
    }

    // ———————————————————————————— 推流入口 ————————————————————————————

    /// <summary>推入一帧（像素格式须与相机 Config.Format 一致）；由相机采集线程按帧率节拍发出。</summary>
    public void PushFrame(FrameBuffer frame) => _camera.PushExternalFrame(frame);

    /// <summary>推入一帧 JPEG（任意分辨率，内部高质量缩放到配置宽高后转目标格式）。解码失败仅节流日志，不抛异常。</summary>
    public void PushJpeg(byte[] jpegBytes)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);
        DecodeAndPush(jpegBytes, 0, jpegBytes.Length);
    }

    // ———————————————————————————— UDP 喂图（GVF1）————————————————————————————

    /// <summary>启动 UDP 喂图接收线程（绑定 0.0.0.0:FeedPort）。</summary>
    private void StartFeedListener()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _options.FeedPort));
        _feedUdp = udp;
        _feedCts = new CancellationTokenSource();
        _feedThread = new Thread(FeedLoop)
        {
            IsBackground = true,
            Name = "GigEForge-Feed",
        };
        _feedThread.Start();
    }

    /// <summary>
    /// 喂图接收循环：阻塞 Receive，socket 关闭（Stop）时以 ObjectDisposedException 退出；
    /// 单包处理异常吞掉并节流记日志，保证坏包不拖垮接收。
    /// </summary>
    private void FeedLoop()
    {
        var cts = _feedCts;
        var udp = _feedUdp;
        if (udp is null || cts is null)
        {
            return;
        }

        var anyEp = new IPEndPoint(IPAddress.Any, 0);
        while (!cts.IsCancellationRequested)
        {
            byte[] datagram;
            try
            {
                datagram = udp.Receive(ref anyEp);
            }
            catch (ObjectDisposedException)
            {
                break;                          // Stop() 关闭 socket 解除阻塞
            }
            catch (SocketException ex)
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }

                ThrottledLog($"UDP 喂图接收错误: {ex.SocketErrorCode}");
                continue;
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }

                ThrottledLog($"UDP 喂图接收异常: {ex.Message}");
                continue;
            }

            try
            {
                HandleFeedDatagram(datagram);
            }
            catch (Exception ex)
            {
                ThrottledLog($"GVF1 包处理异常: {ex.Message}");
            }
        }
    }

    /// <summary>解析 GVF1 包：magic(4B) + seq(u32 BE，忽略) + jpegLen(u32 BE) + JPEG；非法包丢弃并节流记日志。</summary>
    private void HandleFeedDatagram(byte[] datagram)
    {
        if (datagram.Length < Gvf1HeaderSize
            || datagram[0] != Gvf1Magic[0] || datagram[1] != Gvf1Magic[1]
            || datagram[2] != Gvf1Magic[2] || datagram[3] != Gvf1Magic[3])
        {
            ThrottledLog("GVF1 包头不合法（magic 不符或长度不足），已丢弃");
            return;
        }

        uint jpegLen = ReadU32(datagram, 8);    // [4..7] seq：仅用于发送端排序，接收端忽略
        if (jpegLen > MaxJpegBytes || datagram.Length < Gvf1HeaderSize + (int)jpegLen)
        {
            ThrottledLog($"GVF1 载荷不合法（len={jpegLen}，上限 {MaxJpegBytes}），已丢弃");
            return;
        }

        DecodeAndPush(datagram, Gvf1HeaderSize, (int)jpegLen);
    }

    // ———————————————————————————— 解码管线 ————————————————————————————

    /// <summary>
    /// JPEG → 缩放 → 转格式 → 推入相机：与 UDP 喂图 / PushJpeg 共用。
    /// 解码/缩放异常吞掉并节流记日志（喂图源不可靠属常态，不向上抛）。
    /// </summary>
    private void DecodeAndPush(byte[] buffer, int offset, int jpegLength)
    {
        int width = _camera.Config.Width;
        int height = _camera.Config.Height;
        var target = _camera.Config.Format;
        byte[] pixels;
        try
        {
            using var ms = new MemoryStream(buffer, offset, jpegLength, writable: false);
            using var source = new Bitmap(ms);                          // JPEG 解码（流生命周期覆盖位图）
            using var scaled = ImageLoader.ResizeTo24Bpp(source, width, height);    // 高质量缩放到 24bpp
            byte[] rgb = ImageLoader.BitmapToRgb24(scaled, width, height);          // BGR 行序 → 紧凑 RGB8
            pixels = target == PixelFormatCode.RGB8
                ? rgb
                : PixelConverter.Convert(rgb, PixelFormatCode.RGB8, target, width, height);
        }
        catch (Exception ex)
        {
            ThrottledLog($"JPEG 解码失败: {ex.Message}");
            return;
        }

        var frame = new FrameBuffer
        {
            Width = width,
            Height = height,
            Format = target,
            Data = pixels,
            TimestampNs = StopwatchToNs(Stopwatch.GetTimestamp()),
            BlockId = 0,                        // block_id 由相机采集线程统一分配
        };

        Interlocked.Increment(ref _receivedFrames);
        _camera.PushExternalFrame(frame);
        FrameDecoded?.Invoke(frame);
    }

    // ———————————————————————————— 工具 ————————————————————————————

    /// <summary>应用构造选项：经寄存器写入生效，让 validate/onWrite 钩子统一完成校验、Config 同步与 PayloadSize 刷新。</summary>
    private void ApplyOptions()
    {
        // 宽须偶数：先夹紧到 [16,8192] 再向下取偶
        int width = Math.Clamp(_options.Width, 16, 8192);
        width -= width & 1;
        int height = Math.Clamp(_options.Height, 16, 8192);
        int fps = Math.Clamp(_options.Fps, 1, 120);

        WriteBestEffort(GevRegisters.RegWidth, (uint)width, "Width");
        WriteBestEffort(GevRegisters.RegHeight, (uint)height, "Height");
        WriteBestEffort(GevRegisters.RegAcqFrameRate, (uint)fps, "Fps");

        if (PixelFormatEx.IsSupported(_options.Format))
        {
            WriteBestEffort(GevRegisters.RegPixelFormat, (uint)_options.Format, "Format");
        }
        else
        {
            Log?.Invoke($"警告：CameraOptions.Format 不受支持（{_options.Format}），保留默认 {PixelFormatEx.PName(_camera.Config.Format)}");
        }
    }

    /// <summary>寄存器写入兜底：失败仅记日志并保留当前值（选项非法时的归一逻辑保证通常不会发生）。</summary>
    private void WriteBestEffort(uint address, uint value, string optionName)
    {
        var status = _camera.Registers.WriteRegister(address, value);
        if (status != GevStatus.Success)
        {
            Log?.Invoke($"警告：CameraOptions.{optionName}={value} 写入寄存器失败（{status}），保留当前值");
        }
    }

    /// <summary>节流日志：同一窗口（3s）内的丢弃/失败只打一条，并附累计次数。</summary>
    private void ThrottledLog(string message)
    {
        lock (_dropLogLock)
        {
            _droppedSinceLog++;
            long now = Environment.TickCount64;
            if (now < _nextDropLogAtMs)
            {
                return;
            }

            _nextDropLogAtMs = now + DropLogIntervalMs;
            int dropped = _droppedSinceLog;
            _droppedSinceLog = 0;
            Log?.Invoke($"{message}（节流：{DropLogIntervalMs / 1000}s 内累计 {dropped} 次）");
        }
    }

    private static void JoinThread(Thread? thread)
    {
        if (thread is null || thread == Thread.CurrentThread)
        {
            return;
        }

        try
        {
            thread.Join(ThreadJoinTimeoutMs);
        }
        catch
        {
            // 线程可能已自行退出
        }
    }

    private static uint ReadU32(byte[] buffer, int offset) =>
        (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);

    /// <summary>Stopwatch 时间戳 → 纳秒（整数运算避免大数值乘 1e9 溢出：秒与余数分别换算）。</summary>
    private static ulong StopwatchToNs(long timestamp)
    {
        long seconds = Math.DivRem(timestamp, Stopwatch.Frequency, out long fraction);
        return (ulong)(seconds * 1_000_000_000L + fraction * 1_000_000_000L / Stopwatch.Frequency);
    }

    /// <summary>停机：停喂图线程并下线、销毁相机（销毁后不可重启）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _camera.Dispose();
        Log = null;
        FrameDecoded = null;
    }
}
