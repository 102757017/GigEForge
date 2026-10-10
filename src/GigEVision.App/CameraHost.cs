using System.Net;
using System.Text;
using GigEVision.Core.Camera;
using GigEVision.Core.Imaging;
using GigEVision.Core.Protocol.Gvcp;

namespace GigEVision.App;

/// <summary>
/// 相机宿主：进程内唯一的 <see cref="VirtualCamera"/> 单例封装。
/// <para>职责：</para>
/// <list type="bullet">
/// <item>懒创建相机（<see cref="Instance"/>），<see cref="EnsureStarted"/> 幂等上线并记录绑定的网卡；</item>
/// <item>汇聚协议栈日志到线程安全环形缓冲（最近 200 条），经 <see cref="LogCommitted"/> 广播给 UI；</item>
/// <item>订阅 <see cref="VirtualCamera.FrameStreamed"/> 缓存最新帧（<see cref="LatestFrame"/>，供预览定时器轮询）；</item>
/// <item>跟踪当前 GVCP 控制者（<see cref="CurrentController"/>，供状态栏显示）。</item>
/// </list>
/// <para>注意：本类不 Dispose 相机——由 MainForm 窗体关闭时统一 Stop（进程随即退出，单例无需释放托管资源）。</para>
/// </summary>
public static class CameraHost
{
    /// <summary>环形缓冲容量（最近 200 条）。</summary>
    private const int LogCapacity = 200;

    private static readonly Lazy<VirtualCamera> _camera = new(() => new VirtualCamera());
    private static readonly object _gate = new();
    private static readonly Queue<string> _logs = new(LogCapacity);

    private static volatile FrameBuffer? _latestFrame;   // 引用替换为原子，预览线程免锁读取
    private static IPEndPoint? _controller;              // 最近一次控制者状态（事件线程写入）
    private static IPAddress? _boundNic;                 // 已生效的流通道绑定地址（null = 任意适配器）

    static CameraHost()
    {
        // —— 单一订阅点 ——
        // camera.Log 只在此处订阅一次并进入环形缓冲，UI 层（MainForm）仅消费 LogCommitted，
        // 避免多处订阅造成同一行日志重复显示。事件在协议栈后台线程触发，Commit 本身线程安全。
        Instance.Log += Commit;

        Instance.ControllerChanged += endpoint =>
        {
            _controller = endpoint;
            Commit(endpoint is null ? "控制端已断开" : $"控制端已接入: {endpoint}");
        };

        // 帧事件只做一次引用替换，供预览定时器轮询（采集线程触发）
        Instance.FrameStreamed += frame => _latestFrame = frame;
    }

    /// <summary>相机单例（首次访问即构建；构建阶段不绑定任何端口，Start 才上线）。</summary>
    public static VirtualCamera Instance => _camera.Value;

    /// <summary>最近一帧已产出的图像（FrameStreamed 更新；尚无帧时为 null）。预览轮询读取。</summary>
    public static FrameBuffer? LatestFrame => _latestFrame;

    /// <summary>当前 GVCP 控制者（0x0A00 仲裁结果）；无控制者时为 null。</summary>
    public static IPEndPoint? CurrentController => _controller;

    /// <summary>相机单例是否已构建（不触发懒加载）。</summary>
    public static bool IsCreated => _camera.IsValueCreated;

    /// <summary>相机是否已上线（GVCP 服务运行中）。相机未创建时返回 false。</summary>
    public static bool IsRunning => _camera.IsValueCreated && Instance.IsRunning;

    /// <summary>一条日志已提交到环形缓冲（在提交线程上触发；UI 订阅方需自行 BeginInvoke 切线程）。</summary>
    public static event Action<string>? LogCommitted;

    /// <summary>
    /// 确保相机已上线（幂等）。未启动则 Start（先把流通道绑定到指定网卡）；已上线但网卡参数变化则重新绑定。
    /// </summary>
    /// <param name="nicIp">流通道绑定网卡 IPv4；null = 任意适配器。</param>
    public static void EnsureStarted(IPAddress? nicIp = null)
    {
        var camera = Instance;

        if (!camera.IsRunning)
        {
            camera.BindToInterface(nicIp);      // 未上线时仅记录目标地址，Start 时生效
            _boundNic = nicIp ?? IPAddress.Any;
            if (nicIp is not null)
            {
                Commit($"流通道绑定网卡: {nicIp}");
            }

            camera.Start();                     // 内部日志（GVCP 端口 / 流源端口）经 Log → Commit
            return;
        }

        IPAddress target = nicIp ?? IPAddress.Any;
        if (_boundNic is null || !_boundNic.Equals(target))
        {
            camera.BindToInterface(nicIp);      // 运行中切换：重启流通道（GVCP 不受影响）
            _boundNic = target;
        }
    }

    /// <summary>停止相机（含采集线程；幂等）。MainForm 窗体关闭时统一调用，本类不负责 Dispose。</summary>
    public static void StopCamera()
    {
        if (!_camera.IsValueCreated)
        {
            return;
        }

        Instance.Stop();
        _latestFrame = null;
    }

    /// <summary>
    /// 应用采集参数（宽/高/格式/帧率/图案）。统一走寄存器写入路径：
    /// onWrite 钩子同步 Config 并自动刷新 PayloadSize（0x140018），
    /// 保证 GenICam 寄存器、Config 与 GVSP 实际出帧三者一致（直接改 Config 不会更新寄存器表）。
    /// </summary>
    /// <param name="width">宽度（自动夹为偶数；16..8192）。</param>
    /// <param name="height">高度（16..8192）。</param>
    /// <param name="format">PFNC 像素格式。</param>
    /// <param name="fps">帧率（null = 保持当前值；1..120）。</param>
    /// <param name="pattern">测试图案（null = 不修改；仅模拟器图案模式传入）。</param>
    public static void ApplyCaptureSettings(
        int width, int height, PixelFormatCode format, int? fps = null, PatternType? pattern = null)
    {
        var registers = Instance.Registers;

        if ((width & 1) != 0)
        {
            width -= 1;     // 契约 7：宽度必须为偶数
        }

        EnsureSuccess(registers.WriteRegister(GevRegisters.RegWidth, (uint)width), "宽度");
        EnsureSuccess(registers.WriteRegister(GevRegisters.RegHeight, (uint)height), "高度");
        EnsureSuccess(registers.WriteRegister(GevRegisters.RegPixelFormat, (uint)format), "像素格式");

        if (fps.HasValue)
        {
            EnsureSuccess(registers.WriteRegister(GevRegisters.RegAcqFrameRate, (uint)Math.Clamp(fps.Value, 1, 120)), "帧率");
        }

        if (pattern.HasValue)
        {
            EnsureSuccess(registers.WriteRegister(GevRegisters.RegTestPattern, (uint)pattern.Value), "测试图案");
        }
    }

    /// <summary>应用相机身份（Discovery 载荷 + GenICam DeviceID 内存区同步）。</summary>
    public static void ApplyIdentity(string? serialNumber, string? userDefinedName)
    {
        var camera = Instance;

        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            string serial = serialNumber.Trim();
            if (serial.Length > 16)
            {
                serial = serial[..16];
            }

            camera.Identity.SerialNumber = serial;
            camera.Registers.WriteMemoryDirect(GevRegisters.SerialNumber, ToAsciiPadded(serial, 16));
        }

        if (!string.IsNullOrWhiteSpace(userDefinedName))
        {
            string name = userDefinedName.Trim();
            if (name.Length > 64)
            {
                name = name[..64];
            }

            camera.Identity.UserDefinedName = name;
            camera.Registers.WriteMemoryDirect(GevRegisters.UserDefinedName, ToAsciiPadded(name, 64));
        }
    }

    /// <summary>寄存器写入被拒（参数非法）时抛出带语义的异常。</summary>
    private static void EnsureSuccess(GevStatus status, string what)
    {
        if (status != GevStatus.Success)
        {
            throw new ArgumentException($"{what}参数被相机拒绝（GevStatus 0x{(uint)status:X4}）");
        }
    }

    /// <summary>ASCII 定长补零编码（与 Core 名称区初始化一致）。</summary>
    private static byte[] ToAsciiPadded(string text, int size)
    {
        var buffer = new byte[size];
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        Array.Copy(ascii, buffer, Math.Min(ascii.Length, size));
        return buffer;
    }

    /// <summary>写入一条日志（协议栈回调或 UI 侧动作）到环形缓冲并广播。线程安全；锁外触发事件防重入死锁。</summary>
    public static void Commit(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        Action<string>? handlers;
        lock (_gate)
        {
            _logs.Enqueue(line);
            while (_logs.Count > LogCapacity)
            {
                _logs.Dequeue();
            }

            handlers = LogCommitted;
        }

        handlers?.Invoke(line);
    }

    /// <summary>环形缓冲快照（UI 初始化时回放历史日志）。</summary>
    public static string[] SnapshotLogs()
    {
        lock (_gate)
        {
            return _logs.ToArray();
        }
    }
}
