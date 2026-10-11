using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using GigEVision.Core.GenICam;
using GigEVision.Core.Imaging;
using GigEVision.Core.Protocol.Gvcp;
using GigEVision.Core.Protocol.Gvsp;

namespace GigEVision.Core.Camera;

/// <summary>
/// 相机采集配置：图像尺寸 / 像素格式 / 帧率 / 测试图案与 GVSP 分片参数。
/// 寄存器（0x140000 系列、0x0D04、0x0D08）写入时经 onWrite 钩子同步到本配置，
/// 因此客户端改参与本地改参最终一致；本地程序也可直接修改（下一次出帧即生效）。
/// </summary>
public sealed class CameraConfig
{
    /// <summary>图像宽（像素，16..8192 且偶数）。</summary>
    public int Width { get; set; } = 1280;

    /// <summary>图像高（像素，16..8192）。</summary>
    public int Height { get; set; } = 720;

    /// <summary>像素格式（PFNC，仅支持 Mono8/BayerRG8/RGB8/BGR8）。</summary>
    public PixelFormatCode Format { get; set; } = PixelFormatCode.Mono8;

    /// <summary>采集帧率（fps，1..120）。</summary>
    public int Fps { get; set; } = 30;

    /// <summary>内置测试图案（0 = Off：无帧源时不产帧；1..4 见 <see cref="PatternType"/>）。</summary>
    public PatternType Pattern { get; set; } = PatternType.ColorBars;

    /// <summary>GVSP 包大小（字节，576..16000，含 8 字节包头）。</summary>
    public ushort PacketSize { get; set; } = 1500;

    /// <summary>GVSP 包间延迟（微秒，0 = 不限速）。</summary>
    public uint PacketDelayUs { get; set; } = 0;
}

/// <summary>
/// 虚拟相机门面：组装寄存器表（<see cref="RegisterMap"/>）、GVCP 控制服务（<see cref="GvcpServer"/>）、
/// GVSP 流发送器（<see cref="GvspTransmitter"/>）与采集线程，对上层（App UI / SDK）提供单一入口。
/// <para>分工约定（与协议层的两段式写入流程配合）：</para>
/// <list type="bullet">
/// <item>寄存器写入：GvcpServer 先回调 <see cref="IGvcpCallbacks.OnWriteRegister"/> 做语义校验，
/// 返回 Success 后由 GvcpServer 统一落表；本类的联动动作（同步 <see cref="Config"/>、刷新 PayloadSize、
/// 启停采集）全部挂在 RegisterMap 的 onWrite 钩子上，每次写入恰好触发一次，互不重复。</item>
/// <item>心跳：HeartbeatMonitor 由 GvcpServer 内部持有（获得控制权时自动启动），
/// 0x0938 的下限 clamp 与生效由 GvcpServer 的 RegisterWritten 钩子完成，本类不重复处理。</item>
/// <item>控制权：订阅 RegisterWritten(0x0A00) 映射为 <see cref="ControllerChanged"/>；
/// 写入 0（主动释放 / 心跳超时自动释放 / 服务停止）时联动停止采集并通知 null。</item>
/// </list>
/// </summary>
public sealed class VirtualCamera : IDisposable, IGvcpCallbacks
{
    /// <summary>GVSP 包大小下限（字节，含 8B GVSP 包头，不含 IP/UDP；契约 5.3）。</summary>
    private const int MinPacketSize = 576;

    /// <summary>GVSP 包大小上限（字节，含 8B GVSP 包头，不含 IP/UDP；契约 5.3）。</summary>
    private const int MaxPacketSize = 16000;

    /// <summary>宽/高下限（契约 7）。</summary>
    private const int MinDimension = 16;

    /// <summary>宽/高上限（契约 7）。</summary>
    private const int MaxDimension = 8192;

    /// <summary>帧率下限（契约 7）。</summary>
    private const int MinFps = 1;

    /// <summary>帧率上限（契约 7）。</summary>
    private const int MaxFps = 120;

    /// <summary>测试图案最大取值（0..4，0 = Off）。</summary>
    private const uint MaxTestPattern = 4;

    /// <summary>XmlUrl 内存区大小（契约 5.3）。</summary>
    private const int XmlUrlMemorySize = 512;

    /// <summary>GenICam XML blob 内存区大小（256KB 上限，契约 7）。</summary>
    private const int XmlBlobMemorySize = 256 * 1024;

    /// <summary>GVSP 包的 IP/UDP 头开销（IP 20 + UDP 8）：0x0D04 寄存器低 16 位 = GVSP 包大小 + 28。</summary>
    private const int UdpOverhead = 28;

    /// <summary>0x0D04 寄存器 bit31：FireTestPacket（自清零，与 0x0D0C bit0 同义，兼容新旧客户端）。</summary>
    private const uint ScPsFireTestBit = 0x80000000u;

    /// <summary>0x0D04 寄存器回写时保留的标志位掩码（bit30=DoNotFragment、bit29=BigEndian）。</summary>
    private const uint ScPsFlagsKeepMask = 0x60000000u;

    /// <summary>0x0D0C 标准测试包寄存器 bit0：FireTestPacket（自清零，GigE Vision 标准地址）。</summary>
    private const uint ScControlFireTestBit = 0x00000001u;

    /// <summary>采集/喂图线程退出等待上限（毫秒）。</summary>
    private const int ThreadJoinTimeoutMs = 1500;

    /// <summary>block_id 回绕点：65535 后回到 1（0 保留不使用）。</summary>
    private const ushort LastBlockId = 65535;

    private readonly CameraIdentity _identity;
    private readonly CameraConfig _config = new();
    // lenientBootstrap：引导区（<0x00100000）未定义地址读 0/写忽略——
    // VisionMaster/pylon 打开相机会探测标准保留寄存器（0x0D20/0x0A04 等），报错即断开
    private readonly RegisterMap _registers = new(lenientBootstrap: true);
    private readonly StreamStats _stats = new();
    private readonly GvcpServer _server;
    private readonly GvspTransmitter _transmitter;
    private readonly object _stateLock = new();

    private IPAddress _bindAddress = IPAddress.Any;     // 流通道绑定网卡（null/Any = 任意适配器）
    private LatestFrameSource? _pushSource;             // PushExternalFrame 的内部帧源（懒创建）
    private volatile IFrameSource? _source;             // 采集线程的当前帧源
    private Thread? _acquisitionThread;
    private volatile bool _running;
    private volatile bool _streaming;
    private ushort _blockId = 1;                        // block_id 循环计数（仅采集线程访问）
    private bool _disposed;

    /// <summary>创建虚拟相机：构建寄存器表 / GenICam XML / GVCP 服务与 GVSP 发送器（identity 为空时用默认身份）。</summary>
    public VirtualCamera(CameraIdentity? identity = null)
    {
        _identity = identity ?? new CameraIdentity();
        _server = new GvcpServer(_identity, _registers, this);
        _transmitter = new GvspTransmitter(_stats);
        _transmitter.Log += message => Log?.Invoke(message);

        BuildRegisters();
        GenICamUrl = BuildGenICamBlob();
        _registers.RegisterWritten += OnRegisterWritten;
    }

    // ———————————————————————————— 属性与事件 ————————————————————————————

    /// <summary>相机身份（Discovery / GenICam XML 的注入源；GvcpServer 持同一实例）。</summary>
    public CameraIdentity Identity => _identity;

    /// <summary>采集配置（与寄存器表双向同步）。</summary>
    public CameraConfig Config => _config;

    /// <summary>寄存器表（UI 可直接读写寄存器，语义钩子照常生效）。</summary>
    public RegisterMap Registers => _registers;

    /// <summary>流发送统计（帧/包/字节/重发/实时帧率）。</summary>
    public StreamStats Stats => _stats;

    /// <summary>GenICam XML 的 URL 字符串："local:///GigEForge.xml;00100000;&lt;字节数hex&gt;"
    /// （Aravis local URL 格式：local:///&lt;文件名&gt;;&lt;地址 hex&gt;;&lt;大小 hex&gt;，地址在前、大小在后）。</summary>
    public string GenICamUrl { get; }

    /// <summary>
    /// 帧源：采集线程每周期调用 AcquireFrame。null 时用 TestPatternGenerator 兜底
    /// （图案取 Config.Pattern，0 = Off 时无帧源则不产帧）。
    /// </summary>
    public IFrameSource? Source
    {
        get => _source;
        set => _source = value;
    }

    /// <summary>GVCP 控制服务是否已启动。</summary>
    public bool IsRunning => _running;

    /// <summary>采集线程是否正在出帧。</summary>
    public bool IsStreaming => _streaming;

    /// <summary>一帧已产出（无论是否实际发出——目的地无效时仅跳过发送，契约 7）。在采集线程上触发。</summary>
    public event Action<FrameBuffer>? FrameStreamed;

    /// <summary>协议栈 / 门面日志。</summary>
    public event Action<string>? Log;

    /// <summary>控制者变化：获得控制 = 控制者端点；释放（含心跳超时）= null。</summary>
    public event Action<IPEndPoint?>? ControllerChanged;

    /// <summary>采集启停变化（StartAcquisition / StopAcquisition / 客户端写采集命令寄存器）。</summary>
    public event Action? AcquisitionChanged;

    // ———————————————————————————— 启停控制 ————————————————————————————

    /// <summary>
    /// 启动相机：绑定 GVSP 流通道 → 启动 GVCP 服务。
    /// 心跳监视器由 GvcpServer 内部管理（客户端写 0x0A00 获得控制权时启动），此处无需干预。
    /// </summary>
    /// <param name="gvcpPort">GVCP 监听端口（默认 3956）。</param>
    public void Start(int gvcpPort = 3956)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _transmitter.Start(_bindAddress);   // 绑定失败抛 SocketException，状态不变
            try
            {
                _server.Start(gvcpPort);
            }
            catch
            {
                _transmitter.Stop();            // 回滚流通道绑定
                throw;
            }

            _running = true;
            ApplyNetworkIdentity();   // Discovery 负载镜像引导寄存器，IP/掩码/网关必须在启动前就位
        }

        // 流源端口回填引导寄存器（GevSCPHostPort，0x0D14 低 16 位，供客户端回读；外部写由回调拦截）
        _registers.WriteRegister(GevRegisters.Sc0SourcePort, (uint)_transmitter.BoundPort);
        Log?.Invoke($"虚拟相机已启动：GVCP UDP {gvcpPort}，流源端口 {_transmitter.BoundPort}，绑定 {_bindAddress}");
    }

    /// <summary>停止相机：停采集 → 停 GVCP 服务（自动释放控制权）→ 停流通道。可重复调用。</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        StopAcquisition();
        _server.Stop();         // 内部 ReleaseControl：写 0x0A00=0 → 本类收到 ControllerChanged(null)
        _transmitter.Stop();
        Log?.Invoke("虚拟相机已停止");
    }

    /// <summary>启动采集线程：按 Config.Fps 节拍从 Source（或测试图案兜底）取帧并发送。可重复调用。</summary>
    public void StartAcquisition()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_streaming)
            {
                return;
            }

            _streaming = true;
            _acquisitionThread = new Thread(AcquisitionLoop)
            {
                IsBackground = true,
                Name = "GigEForge-Acquisition",
            };
            _acquisitionThread.Start();
        }

        Log?.Invoke("采集已启动");
        AcquisitionChanged?.Invoke();
    }

    /// <summary>停止采集线程（等待退出，上限 1500ms）。可重复调用。</summary>
    public void StopAcquisition()
    {
        Thread? thread;
        lock (_stateLock)
        {
            if (!_streaming)
            {
                return;
            }

            _streaming = false;
            thread = _acquisitionThread;
            _acquisitionThread = null;
        }

        JoinThread(thread);
        Log?.Invoke("采集已停止");
        AcquisitionChanged?.Invoke();
    }

    /// <summary>切换流通道绑定网卡；相机运行中则立即重启流通道（GVCP 服务不受影响）。null = 任意适配器。</summary>
    public void BindToInterface(IPAddress? nicIp)
    {
        IPAddress target = nicIp ?? IPAddress.Any;
        bool restart;
        lock (_stateLock)
        {
            if (_bindAddress.Equals(target))
            {
                return;
            }

            _bindAddress = target;
            restart = _running;
        }

        if (restart)
        {
            _transmitter.Stop();
            _transmitter.Start(target);
            ApplyNetworkIdentity();   // 绑定网卡变化 → 同步刷新引导寄存器中的网络身份
            Log?.Invoke($"流通道已切换绑定到 {target}");
        }
    }

    /// <summary>
    /// 推入外部帧（转换器 / SDK 喂图路径）：懒创建内部 <see cref="LatestFrameSource"/>；
    /// 若当前未设置 <see cref="Source"/> 则自动将其设为帧源。已设置自定义 Source 时帧仍进入内部源
    /// （是否被采集取决于 Source 配置）。线程安全。
    /// </summary>
    public void PushExternalFrame(FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var push = _pushSource;
        if (push is null)
        {
            lock (_stateLock)
            {
                push = _pushSource;
                if (push is null)
                {
                    push = new LatestFrameSource();
                    _pushSource = push;
                    _source ??= push;           // Source 为空时接管采集
                }
            }
        }

        push.Push(frame);
    }

    // ———————————————————————————— 网络身份 ————————————————————————————

    /// <summary>
    /// 把流通道绑定网卡的网络参数写入相机身份与引导寄存器（当前 IP 0x0024/34/44 + 持久 IP 0x064C/65C/66C）。
    /// Discovery 负载直接镜像这些寄存器：缺失时客户端会把相机识别为 0.0.0.0/00:00... 并提示修改 IP。
    /// 绑定任意适配器（Any）时自动挑选首选网卡：优先“有 IPv4 网关的有线以太网”，其次任意非回环 IPv4。
    /// </summary>
    private void ApplyNetworkIdentity()
    {
        IPAddress? ip = _bindAddress.Equals(IPAddress.Any) ? PickDefaultNicAddress() : _bindAddress;
        if (ip is null || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.Loopback.Equals(ip))
        {
            Log?.Invoke("警告：未找到可用 IPv4 网卡，相机网络身份未配置（客户端将显示 0.0.0.0）");
            return;
        }

        var (subnet, gateway) = LookupNicNetworkConfig(ip);
        _identity.CurrentIp = ip;
        _identity.CurrentSubnetMask = subnet;
        _identity.CurrentGateway = gateway;

        uint ipValue = IpToUint(ip);
        uint subnetValue = IpToUint(subnet);
        uint gatewayValue = IpToUint(gateway);
        _registers.WriteRegister(GevRegisters.CurrentIp, ipValue);
        _registers.WriteRegister(GevRegisters.CurrentSubnet, subnetValue);
        _registers.WriteRegister(GevRegisters.CurrentGateway, gatewayValue);
        _registers.WriteRegister(GevRegisters.PersistentIp, ipValue);
        _registers.WriteRegister(GevRegisters.PersistentSubnet, subnetValue);
        _registers.WriteRegister(GevRegisters.PersistentGateway, gatewayValue);
        Log?.Invoke($"相机网络身份：IP={ip} 子网={subnet} 网关={gateway}（MAC {_identity.MacString}）");
    }

    /// <summary>挑选首选网卡 IPv4：有网关的有线以太网优先，其次任意 Up 状态非回环 IPv4。</summary>
    private static IPAddress? PickDefaultNicAddress()
    {
        IPAddress? fallback = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            bool hasGateway = false;
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    hasGateway = true;
                    break;
                }
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && hasGateway)
                {
                    return address.Address;   // 有网关的有线网卡优先
                }

                fallback ??= address.Address;
            }
        }

        return fallback;
    }

    /// <summary>查询指定 IPv4 所在网卡的子网掩码与网关；找不到匹配网卡时回退 255.255.255.0 / 0.0.0.0。</summary>
    private static (IPAddress Subnet, IPAddress Gateway) LookupNicNetworkConfig(IPAddress ip)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            IPAddress? subnet = null;
            bool match = false;
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                subnet ??= unicast.IPv4Mask;
                if (unicast.Address.Equals(ip))
                {
                    match = true;
                    subnet = unicast.IPv4Mask;
                }
            }

            if (!match)
            {
                continue;
            }

            IPAddress? gateway = null;
            foreach (var gatewayAddress in properties.GatewayAddresses)
            {
                if (gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    gateway = gatewayAddress.Address;
                    break;
                }
            }

            if (subnet is null || subnet.Equals(IPAddress.Any))
            {
                subnet = IPAddress.Parse("255.255.255.0");   // IPv4Mask 缺省时兜底 /24
            }

            return (subnet, gateway ?? IPAddress.Any);
        }

        return (IPAddress.Parse("255.255.255.0"), IPAddress.Any);
    }

    // ———————————————————————————— 采集线程 ————————————————————————————

    /// <summary>
    /// 采集循环：周期 = 1000/Fps 毫秒；先粗睡至剩约 2ms，再 SpinWait 到点（契约 7）。
    /// 帧来源：Source.AcquireFrame → null 且图案未关（≠0）时用 TestPatternGenerator 兜底；
    /// 时间戳填设备单调时钟（纳秒），BlockId 自 1 循环自增。
    /// </summary>
    private void AcquisitionLoop()
    {
        while (_streaming)
        {
            // 周期按当前 Fps 动态取值：客户端改帧率寄存器后下一帧即生效
            int fps = Math.Clamp(_config.Fps, MinFps, MaxFps);
            long periodTicks = Math.Max(1, 1000 / fps) * Stopwatch.Frequency / 1000;
            long deadline = Stopwatch.GetTimestamp() + periodTicks;

            try
            {
                long tickMs = Environment.TickCount64;
                var frame = _source?.AcquireFrame(tickMs);
                if (frame is null && (int)_config.Pattern != 0)
                {
                    frame = TestPatternGenerator.Generate(
                        _config.Width, _config.Height, _config.Format, _config.Pattern, tickMs);
                }

                if (frame is not null)
                {
                    frame.TimestampNs = StopwatchToNs(Stopwatch.GetTimestamp());
                    frame.BlockId = NextBlockId();
                    SendFrame(frame);
                    FrameStreamed?.Invoke(frame);   // 目的地无效时同样触发（仅跳过实际发送）
                }
            }
            catch (Exception ex)
            {
                // 帧源 / 生成器 / 事件处理器异常不拖垮采集线程，跳过本帧继续节拍
                Log?.Invoke($"采集循环异常（跳过本帧）: {ex.Message}");
            }

            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks > 0)
            {
                long remainingMs = remainingTicks * 1000 / Stopwatch.Frequency;
                if (remainingMs > 2)
                {
                    Thread.Sleep((int)(remainingMs - 2));
                }

                while (_streaming && Stopwatch.GetTimestamp() < deadline)
                {
                    Thread.SpinWait(32);
                }
            }
            // 落后于节拍（出帧耗时超过周期）：直接进入下一帧，自然降速不追帧
        }
    }

    /// <summary>解析流目的地并发送一帧；目的地/端口无效或流通道未绑定时静默跳过（仅产帧与事件）。</summary>
    private void SendFrame(FrameBuffer frame)
    {
        (IPAddress? destination, int port) = ResolveStreamTarget();

        if (destination is null || port == 0 || _transmitter.BoundPort == 0)
        {
            return;     // 契约 7：目的地与端口都无效 → 跳过发送，仅产出帧
        }

        _transmitter.SendFrame(frame, destination, port, frame.BlockId, _config.PacketSize, _config.PacketDelayUs);
    }

    /// <summary>
    /// 流目的地解析：IP = 0x0D18（GevSCDA）非 0 优先，否则回退当前控制者 IP；
    /// 端口 = 0x0D1C（GevSCSP，客户端写入其收流端口）非 0 优先，否则回退当前控制者端口（契约 7）。
    /// 注意：0x0D18 为大端 u32，必须逐字节重组——IPAddress(long) 在小端机按 host 序解释
    /// （0x7F000001 → "1.0.0.127"），不可用。
    /// </summary>
    private (IPAddress? Address, int Port) ResolveStreamTarget()
    {
        uint rawIp = ReadRegisterValue(GevRegisters.Sc0DestIp);
        IPAddress? address = rawIp != 0 ? UintToIp(rawIp) : _server.Controller?.Address;
        uint rawPort = ReadRegisterValue(GevRegisters.Sc0DestPort) & 0xFFFF;
        int port = rawPort != 0 ? (int)rawPort : _server.Controller?.Port ?? 0;
        return (address, port);
    }

    /// <summary>大端 u32 → IPAddress（IpToUint 的逆变换，逐字节重组，禁用 IPAddress(long) 构造）。</summary>
    private static IPAddress UintToIp(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>block_id 循环自增：1..65535，到 65535 后回到 1（仅采集线程调用）。</summary>
    private ushort NextBlockId()
    {
        ushort id = _blockId;
        _blockId = id == LastBlockId ? (ushort)1 : (ushort)(id + 1);
        return id;
    }

    private static void JoinThread(Thread? thread)
    {
        if (thread is null || thread == Thread.CurrentThread)
        {
            return;     // 防自等待（事件处理器运行于采集线程的极端场景）
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

    // ———————————————————————————— 寄存器表构建 ————————————————————————————

    /// <summary>构建寄存器表：标准引导区 / 网络 / 流通道 / 图像格式 / 采集控制 / 名称与 XML 内存区。</summary>
    private void BuildRegisters()
    {
        // ===== 设备信息 / 能力（标准引导区，只读）=====
        _registers.DefineRegister(GevRegisters.Version, 0x00010002u, "Version", RegisterAccess.ReadOnly);          // GV 1.2
        // bit31=1 大端设备：引导区与 GenICam 寄存器均为大端，与 XML 各寄存器节点的 Endianess 声明一致
        _registers.DefineRegister(GevRegisters.DeviceMode, 0x80000000u, "DeviceMode", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.MacHigh, MacRegisterHigh(), "DeviceMacAddressHigh", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.MacLow, MacRegisterLow(), "DeviceMacAddressLow", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.SupportedIpConfig, 0x5u, "SupportedIpConfiguration", RegisterAccess.ReadOnly); // 持久 IP + LLA
        _registers.DefineRegister(GevRegisters.CurrentIpConfig, 0x1u, "CurrentIpConfiguration", RegisterAccess.ReadOnly);      // 持久 IP 生效
        _registers.DefineRegister(GevRegisters.NetworkIfCount, 1, "NumberOfNetworkInterfaces", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.LinkSpeed0, 1000, "LinkSpeed0", RegisterAccess.ReadOnly);           // 千兆
        _registers.DefineRegister(GevRegisters.MessageChannelCount, 0, "NumberOfMessageChannels", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.StreamChannelCount, 1, "NumberOfStreamChannels", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.ActionSignalCount, 0, "NumberOfActionSignals", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.ActiveLinkCount, 1, "NumberOfActiveLinks", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.StreamChannelCaps, 0, "StreamChannelCapabilities", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.MessageChannelCaps, 0, "MessageChannelCapabilities", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.DiscoveryAckDelay, 0, "DiscoveryAckDelay", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.DeviceConfiguration, 0, "DeviceConfiguration");
        _registers.DefineRegister(GevRegisters.PendingAckTimeout, 0, "PendingAckTimeout");
        // GVCP 能力：bit1=WriteMem、bit2=PacketResend（Aravis create_stream 据此启用重发）
        _registers.DefineRegister(GevRegisters.GvcpCapability,
            0x2u | 0x4u, "GvcpCapability", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.TickFreqHigh, 0, "TimestampTickFrequencyHigh", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.TickFreqLow, 0x3B9ACA00u, "TimestampTickFrequencyLow", RegisterAccess.ReadOnly); // 1e9 Hz
        ulong timestampNow = StopwatchToNs(Stopwatch.GetTimestamp());
        _registers.DefineRegister(GevRegisters.TimestampControl, 0, "TimestampControl");
        _registers.DefineRegister(GevRegisters.TimestampLatchHigh, (uint)(timestampNow >> 32), "TimestampLatchHigh", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.TimestampLatchLow, (uint)timestampNow, "TimestampLatchLow", RegisterAccess.ReadOnly);

        // ===== 网络（自 identity 初始化）=====
        // Current 系列定义为 ReadWrite 以允许 GvcpServer.ApplyNetworkConfig(ForceIP) 内部刷新；
        // 外部客户端写由 OnWriteRegister 拦截回 WriteProtect（GigE Vision 规定当前 IP 寄存器只读）。
        uint ip = IpToUint(_identity.CurrentIp);
        uint subnet = IpToUint(_identity.CurrentSubnetMask);
        uint gateway = IpToUint(_identity.CurrentGateway);
        _registers.DefineRegister(GevRegisters.PersistentIp, ip, "PersistentIpAddress");
        _registers.DefineRegister(GevRegisters.PersistentSubnet, subnet, "PersistentSubnetMask");
        _registers.DefineRegister(GevRegisters.PersistentGateway, gateway, "PersistentGateway");
        _registers.DefineRegister(GevRegisters.CurrentIp, ip, "CurrentIpAddress");
        _registers.DefineRegister(GevRegisters.CurrentSubnet, subnet, "CurrentSubnetMask");
        _registers.DefineRegister(GevRegisters.CurrentGateway, gateway, "CurrentGateway");

        // 心跳超时：GvcpServer 已订阅 RegisterWritten(0x0938) 并把写入值 clamp ≥500ms 应用到监视器，
        // 本类不重复处理（避免双重逻辑）
        _registers.DefineRegister(GevRegisters.HeartbeatTimeout, GvcpServer.DefaultHeartbeatTimeoutMs, "HeartbeatTimeout");

        // 控制权：GvcpServer 在 WriteReg 分发层仲裁 0x0A00 并自行落表，此表项供客户端回读
        _registers.DefineRegister(GevRegisters.ControlPrivilege, 0, "ControlPrivilege");

        // ===== 流通道 0（GigE Vision 1.2 标准布局，VisionMaster/Aravis/pylon 按标准地址硬编码访问）=====
        // 0x0D04 语义（GigE Vision 标准）：低 16 位 = GVSP 包大小 + IP/UDP 28B 头；
        // bit31=FireTestPacket（自清零）、bit30=DoNotFragment、bit29=BigEndian。
        _registers.DefineRegister(GevRegisters.Sc0Capability, 0x80000000u, "SC0Capability", RegisterAccess.ReadOnly); // bit31=支持包重发
        _registers.DefineRegister(GevRegisters.Sc0PacketSize, (uint)(_config.PacketSize + UdpOverhead), "SC0PacketSize",
            onWrite: OnScPsWritten);
        _registers.DefineRegister(GevRegisters.Sc0PacketDelay, _config.PacketDelayUs, "SC0PacketDelay",
            onWrite: value => _config.PacketDelayUs = value);
        // 0x0D0C 标准测试包寄存器：bit0=FireTestPacket（与 0x0D04 bit31 同义，两处都响应）
        _registers.DefineRegister(GevRegisters.Sc0Control, 0, "SC0FireTestPacket",
            onWrite: OnScControlWritten);
        _registers.DefineRegister(GevRegisters.Sc0DestIp, 0, "SC0DestinationIpAddress");
        _registers.DefineRegister(GevRegisters.Sc0DestPort, 0, "SC0DestinationPort");
        // 源端口由 Start() 回填；外部写由 OnWriteRegister 拦截回 WriteProtect
        _registers.DefineRegister(GevRegisters.Sc0SourcePort, 0, "SC0SourcePort");
        // 0x0D20：VisionMaster 打开相机时必读（GV2=大端能力/GV1.2=时间戳控制），小端设备回 0，可写仅存储
        _registers.DefineRegister(GevRegisters.Sc0BigEndianess, 0, "SC0BigEndianess");
        _registers.DefineRegister(GevRegisters.Sc0TimestampFreq, 0x3B9ACA00u, "SC0TimestampTickFrequency", RegisterAccess.ReadOnly);
        ulong scTimestamp = StopwatchToNs(Stopwatch.GetTimestamp());
        _registers.DefineRegister(GevRegisters.Sc0TimestampHigh, (uint)(scTimestamp >> 32), "SC0TimestampHigh", RegisterAccess.ReadOnly);
        _registers.DefineRegister(GevRegisters.Sc0TimestampLow, (uint)scTimestamp, "SC0TimestampLow", RegisterAccess.ReadOnly);

        // ===== 图像格式 / 采集控制（设备自定义区）=====
        _registers.DefineRegister(GevRegisters.RegWidth, (uint)_config.Width, "Width",
            validate: ValidateWidth,
            onWrite: value =>
            {
                _config.Width = (int)value;
                UpdatePayloadSize();
            });
        _registers.DefineRegister(GevRegisters.RegHeight, (uint)_config.Height, "Height",
            validate: ValidateHeight,
            onWrite: value =>
            {
                _config.Height = (int)value;
                UpdatePayloadSize();
            });
        _registers.DefineRegister(GevRegisters.RegPixelFormat, (uint)_config.Format, "PixelFormat",
            validate: value => PixelFormatEx.IsSupported((PixelFormatCode)value)
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            onWrite: value =>
            {
                _config.Format = (PixelFormatCode)value;
                UpdatePayloadSize();
            });
        _registers.DefineRegister(GevRegisters.RegAcqStart, 0, "AcquisitionStart",
            onWrite: value =>
            {
                if (value == 1)
                {
                    StartAcquisition();     // 命令寄存器：写命令值 1 触发
                }
            });
        _registers.DefineRegister(GevRegisters.RegAcqStop, 0, "AcquisitionStop",
            onWrite: value =>
            {
                if (value == 1)
                {
                    StopAcquisition();
                }
            });
        _registers.DefineRegister(GevRegisters.RegAcqFrameRate, (uint)_config.Fps, "AcquisitionFrameRate",
            validate: value => value >= MinFps && value <= MaxFps
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            onWrite: value => _config.Fps = (int)value);
        _registers.DefineRegister(GevRegisters.RegPayloadSize, ComputePayloadSize(), "PayloadSize");
        _registers.DefineRegister(GevRegisters.RegTestPattern, (uint)_config.Pattern, "TestPattern",
            validate: value => value <= MaxTestPattern
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            onWrite: value => _config.Pattern = (PatternType)value);

        // ===== ASCII 名称区 / XML（只读内存区，内容经 WriteMemoryDirect 灌入）=====
        _registers.DefineMemory(GevRegisters.ManufacturerName, 32, "ManufacturerName", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.ModelName, 32, "ModelName", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.DeviceVersion, 32, "DeviceVersion", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.ManufacturerInfo, 32, "ManufacturerInfo", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.SerialNumber, 16, "SerialNumber", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.UserDefinedName, 16, "UserDefinedName", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.XmlUrl, XmlUrlMemorySize, "XmlUrl", RegisterAccess.ReadOnly);
        _registers.DefineMemory(GevRegisters.XmlUrl1, XmlUrlMemorySize, "XmlUrl1", RegisterAccess.ReadOnly);   // 第二 URL，恒为空
        _registers.DefineMemory(GevRegisters.XmlBlobBase, XmlBlobMemorySize, "GenICamXml", RegisterAccess.ReadOnly);

        _registers.WriteMemoryDirect(GevRegisters.ManufacturerName, ToAsciiPadded(_identity.ManufacturerName, 32));
        _registers.WriteMemoryDirect(GevRegisters.ModelName, ToAsciiPadded(_identity.ModelName, 32));
        _registers.WriteMemoryDirect(GevRegisters.DeviceVersion, ToAsciiPadded(_identity.DeviceVersion, 32));
        _registers.WriteMemoryDirect(GevRegisters.ManufacturerInfo, ToAsciiPadded(_identity.ManufacturerInfo, 32));
        _registers.WriteMemoryDirect(GevRegisters.SerialNumber, ToAsciiPadded(_identity.SerialNumber, 16));
        _registers.WriteMemoryDirect(GevRegisters.UserDefinedName, ToAsciiPadded(_identity.UserDefinedName, 16));
    }

    /// <summary>生成 GenICam XML（UTF-8），灌入 0x00100000 blob 区与 0x0200 URL 区，返回 URL 字符串。</summary>
    private string BuildGenICamBlob()
    {
        string xml = GenICamXml.BuildDefault(_identity, (uint)_config.Width, (uint)_config.Height);
        var xmlBytes = Encoding.UTF8.GetBytes(xml);
        if (xmlBytes.Length > XmlBlobMemorySize)
        {
            xmlBytes = xmlBytes[..XmlBlobMemorySize];   // 防御性截断（模板约 12KB，远低于 256KB 上限）
        }

        // Aravis/GenICam local URL 格式：local:///<文件名>;<地址 hex>;<大小 hex>（地址在前、大小在后）
        string url = $"local:///GigEForge.xml;{GevRegisters.XmlBlobBase:X8};{xmlBytes.Length:X8}";
        _registers.WriteMemoryDirect(GevRegisters.XmlBlobBase, xmlBytes);
        _registers.WriteMemoryDirect(GevRegisters.XmlUrl, ToAsciiPadded(url, XmlUrlMemorySize));
        return url;
    }

    /// <summary>0x0D04 写入钩子：低 16 位 = 包大小（含 IP/UDP 28B 头），夹紧后回写；bit31 = FireTestPacket（自清零）。</summary>
    private void OnScPsWritten(uint value)
    {
        if ((value & ScPsFireTestBit) != 0)
        {
            FireTestPacket();       // FireTestPacket 为自清零位：回写时不再置位
        }

        int regSize = (int)(value & 0xFFFF);
        if (regSize == 0)
        {
            return;                 // 仅标志位写入（如 FireTestPacket）：不改包大小
        }

        int gvspSize = Math.Clamp(regSize - UdpOverhead, MinPacketSize, MaxPacketSize);
        _config.PacketSize = (ushort)gvspSize;
        uint normalized = (value & ScPsFlagsKeepMask) | (uint)(gvspSize + UdpOverhead);
        if (normalized != value)
        {
            _registers.WriteRegister(GevRegisters.Sc0PacketSize, normalized);   // 回写归一化值（本钩子再次触发即收敛）
        }
    }

    /// <summary>0x0D0C 写入钩子：bit0=FireTestPacket（自清零，触发测试包）；bit1/2 仅存储。</summary>
    private void OnScControlWritten(uint value)
    {
        if ((value & ScControlFireTestBit) == 0)
        {
            return;
        }

        FireTestPacket();
        _registers.WriteRegister(GevRegisters.Sc0Control, value & ~ScControlFireTestBit);   // 自清零回写
    }

    /// <summary>
    /// FireTestPacket（0x0D04 bit31 / 0x0D0C bit0）：按当前包大小向流目的地发送一个 GVSP 数据包，
    /// 供客户端（Aravis auto_packet_size）探测路径 MTU；目的地无效时静默跳过。
    /// </summary>
    private void FireTestPacket()
    {
        (IPAddress? destination, int port) = ResolveStreamTarget();
        if (destination is null || port == 0 || _transmitter.BoundPort == 0)
        {
            return;
        }

        _transmitter.SendTestPacket(destination, port, _config.PacketSize);
        Log?.Invoke($"已发送 GVSP 测试包（FireTestPacket，{_config.PacketSize}B → {destination}:{port}）");
    }

    /// <summary>PayloadSize = W × H × 每像素字节数（契约 5.5）。</summary>
    private uint ComputePayloadSize() =>
        (uint)((long)_config.Width * _config.Height * PixelFormatEx.BytesPerPixel(_config.Format));

    /// <summary>
    /// 宽/高/格式变更成功落表后刷新负载寄存器（0x00140018）。
    /// 内部直接落表：不经过 IGvcpCallbacks（只有 GvcpServer 的外部写路径走回调），无递归风险。
    /// </summary>
    private void UpdatePayloadSize()
    {
        uint payload = ComputePayloadSize();
        _registers.WriteRegister(GevRegisters.RegPayloadSize, payload);
    }

    /// <summary>宽：16..8192 且偶数（契约 7），非法回 InvalidParameter(0x8015)。</summary>
    private static GevStatus ValidateWidth(uint value) =>
        value >= MinDimension && value <= MaxDimension && (value & 1) == 0
            ? GevStatus.Success
            : GevStatus.InvalidParameter;

    /// <summary>高：16..8192（契约 7）。</summary>
    private static GevStatus ValidateHeight(uint value) =>
        value >= MinDimension && value <= MaxDimension
            ? GevStatus.Success
            : GevStatus.InvalidParameter;

    /// <summary>读寄存器便捷封装：未定义地址回 0。</summary>
    private uint ReadRegisterValue(uint address) =>
        _registers.ReadRegister(address, out uint value) == GevStatus.Success ? value : 0;

    /// <summary>控制权寄存器变化：0 → 停采集 + ControllerChanged(null)；非 0 → ControllerChanged(当前控制者)。</summary>
    private void OnRegisterWritten(uint address, uint value)
    {
        if (address != GevRegisters.ControlPrivilege)
        {
            return;
        }

        if (value == 0)
        {
            // 控制权丢失（客户端主动释放 / 心跳超时自动释放 / 服务停止）：联动停止采集
            StopAcquisition();
            ControllerChanged?.Invoke(null);
        }
        else
        {
            // 获得控制（或同 IP 刷新）：GvcpServer 已先记录控制者端点再落表，此处可安全读取
            ControllerChanged?.Invoke(_server.Controller);
        }
    }

    // ———————————————————————————— IGvcpCallbacks（GvcpServer 回调）————————————————————————————

    /// <summary>
    /// 寄存器写入前置校验（GvcpServer 外部 WriteReg 路径）：只做语义校验与只读拦截，
    /// 不自行落表——返回 Success 后由 GvcpServer 统一写入寄存器表，onWrite 钩子负责联动动作。
    /// </summary>
    GevStatus IGvcpCallbacks.OnWriteRegister(uint address, uint value, IPEndPoint from)
    {
        // 设备自维护寄存器：映射层定义为 ReadWrite 仅为允许内部刷新（ForceIP / 源端口回填），
        // 对外一律回写保护
        if (address is GevRegisters.RegPayloadSize
            or GevRegisters.Sc0SourcePort
            or GevRegisters.CurrentIp or GevRegisters.CurrentSubnet or GevRegisters.CurrentGateway)
        {
            return GevStatus.WriteProtect;
        }

        // 语义校验（与映射层 validate 钩子一致，先行拦截以便 GvcpServer 回准确错误状态）
        return address switch
        {
            GevRegisters.RegWidth => ValidateWidth(value),
            GevRegisters.RegHeight => ValidateHeight(value),
            GevRegisters.RegPixelFormat => PixelFormatEx.IsSupported((PixelFormatCode)value)
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            GevRegisters.RegAcqFrameRate => value >= MinFps && value <= MaxFps
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            GevRegisters.RegTestPattern => value <= MaxTestPattern
                ? GevStatus.Success
                : GevStatus.InvalidParameter,
            _ => GevStatus.Success,
        };
    }

    /// <summary>流重发请求（单流通道，streamChannel 忽略）：转交流发送器按缓存重发。</summary>
    void IGvcpCallbacks.OnResendRequest(IPEndPoint from, ushort streamChannel, ushort blockId, ushort firstId, ushort lastId) =>
        _transmitter.HandleResend(from, blockId, firstId, lastId);

    /// <summary>协议栈日志 → Log 事件。</summary>
    void IGvcpCallbacks.OnLog(string message) => Log?.Invoke(message);

    // ———————————————————————————— 工具 ————————————————————————————

    /// <summary>MAC 寄存器高 32 位：仅低 16 位有效（前两个 MAC 字节）。</summary>
    private uint MacRegisterHigh()
    {
        var mac = _identity.MacAddress;
        return mac is { Length: 6 } ? (uint)((mac[0] << 8) | mac[1]) : 0;
    }

    /// <summary>MAC 寄存器低 32 位（后四个 MAC 字节）。</summary>
    private uint MacRegisterLow()
    {
        var mac = _identity.MacAddress;
        return mac is { Length: 6 }
            ? (uint)((mac[2] << 24) | (mac[3] << 16) | (mac[4] << 8) | mac[5])
            : 0;
    }

    /// <summary>IPv4 → 大端 u32（非 IPv4 地址按 0 处理），与 GvcpServer 的转换语义一致。</summary>
    private static uint IpToUint(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        var bytes = address.GetAddressBytes();
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    /// <summary>ASCII 编码并定长填充：超长截断（保留末位 0 结尾）、剩余 0 填充、非可打印字符替换为 '?'。</summary>
    private static byte[] ToAsciiPadded(string? text, int size)
    {
        var buffer = new byte[size];
        if (string.IsNullOrEmpty(text))
        {
            return buffer;
        }

        int n = Math.Min(size - 1, text.Length);
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            buffer[i] = c >= 32 && c <= 126 ? (byte)c : (byte)'?';
        }

        return buffer;
    }

    /// <summary>Stopwatch 时间戳 → 纳秒（整数运算避免大数值乘 1e9 溢出：秒与余数分别换算）。</summary>
    private static ulong StopwatchToNs(long timestamp)
    {
        long seconds = Math.DivRem(timestamp, Stopwatch.Frequency, out long fraction);
        return (ulong)(seconds * 1_000_000_000L + fraction * 1_000_000_000L / Stopwatch.Frequency);
    }

    /// <summary>停机：停采集线程 → 释放 GVCP 服务（含心跳监视器）→ 释放流发送器 → 解绑事件。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _server.Dispose();
        _transmitter.Dispose();
        _registers.RegisterWritten -= OnRegisterWritten;

        FrameStreamed = null;
        ControllerChanged = null;
        AcquisitionChanged = null;
        Log = null;
    }
}
