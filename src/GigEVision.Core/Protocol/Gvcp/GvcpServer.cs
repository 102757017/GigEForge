using System.Net;
using System.Net.Sockets;
using GigEVision.Core.Camera;

namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GVCP 服务回调（由相机门面层实现，衔接寄存器语义校验、流重发与日志）。
/// </summary>
public interface IGvcpCallbacks
{
    /// <summary>
    /// 寄存器写入前置钩子：校验寄存器语义（宽高范围、采集开关、帧率等）。
    /// 返回非 Success 则该次写入被拒绝并回 ACK 错误状态。
    /// 注意：回调只做校验/翻译，不要在回调里自行写寄存器表——实际落表由 GvcpServer 在回调
    /// 返回 Success 后统一执行（避免 onWrite 钩子被触发两次）。
    /// </summary>
    GevStatus OnWriteRegister(uint address, uint value, IPEndPoint from);

    /// <summary>收到流重发请求（GVCP 端口上的 ResendCmd），通常转交 GvspTransmitter.HandleResend。</summary>
    void OnResendRequest(IPEndPoint from, ushort streamChannel, ushort blockId, ushort firstId, ushort lastId);

    /// <summary>日志输出。</summary>
    void OnLog(string message);
}

/// <summary>
/// GVCP 控制通道服务端（默认 UDP 3956）：发现 / 强制 IP / 寄存器与内存读写 /
/// 控制权仲裁（0x0A00）/ 心跳保活。
/// 收包循环独立异步运行（<see cref="Start"/> 后 while + await ReceiveAsync），
/// 每个数据报抛到线程池处理，处理函数 try/catch 全包，单个坏包不会拖垮服务。
/// </summary>
public sealed class GvcpServer : IDisposable
{
    /// <summary>默认心跳超时（毫秒，0x0938 未写时的出厂值）。</summary>
    public const int DefaultHeartbeatTimeoutMs = 3000;

    /// <summary>心跳超时可配置下限（契约 5.1）。</summary>
    public const int MinHeartbeatTimeoutMs = 500;

    /// <summary>ReadMem 单次读取字节数上限（Aravis 客户端按 ≤512 字节分块拉取，对齐 ARV_GVCP_DATA_SIZE_MAX）。</summary>
    public const int MaxReadMemCount = 512;

    /// <summary>
    /// Discovery 应答负载长度（字节）：引导寄存器区 0x0000-0x00F7 的标准镜像。
    /// 布局（GigE Vision 2.0 规范，与 Wireshark/Aravis 一致）：
    /// [0..3] spec_version、[4..7] device_mode、[8..15] MAC（u64 低 48 位，即 MAC 在偏移 10 起）、
    /// [16..19] supported_ip_cfg（bit0=持久 bit1=DHCP bit2=LLA）、[20..23] current_ip_cfg、
    /// [36..39] 当前 IP、[52..55] 子网掩码、[68..71] 网关（三者间隔 16 字节），
    /// [72/104/136/168/216/232] 名称区（32/32/32/32/16/16）。
    /// </summary>
    public const int DiscoveryPayloadSize = 248;

    private static readonly byte[] EmptyPayload = Array.Empty<byte>();

    private readonly CameraIdentity _identity;
    private readonly RegisterMap _registers;
    private readonly IGvcpCallbacks _callbacks;
    private readonly HeartbeatMonitor _heartbeat;
    private readonly object _gate = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private IPEndPoint? _controller;

    /// <summary>创建 GVCP 服务（不绑定端口，<see cref="Start"/> 后开始收包）。</summary>
    public GvcpServer(CameraIdentity identity, RegisterMap registers, IGvcpCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(callbacks);

        _identity = identity;
        _registers = registers;
        _callbacks = callbacks;
        _heartbeat = new HeartbeatMonitor(TimeSpan.FromMilliseconds(DefaultHeartbeatTimeoutMs));
        _heartbeat.Expired += OnHeartbeatExpired;
        // 心跳超时寄存器 0x0938 可写：写入后立即生效（下限 500ms）
        _registers.RegisterWritten += OnRegisterWrittenForHeartbeat;
    }

    /// <summary>是否正在收包。</summary>
    public bool IsRunning => _running;

    /// <summary>当前控制者（无控制者时为 null）。</summary>
    public IPEndPoint? Controller
    {
        get
        {
            lock (_gate)
            {
                return _controller is null ? null : new IPEndPoint(_controller.Address, _controller.Port);
            }
        }
    }

    /// <summary>启动 GVCP 服务并绑定 0.0.0.0:port（默认 3956）。端口绑定失败时抛 SocketException。</summary>
    public void Start(int port = 3956)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            var udp = new UdpClient();
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));   // 0.0.0.0
            }
            catch
            {
                udp.Dispose();
                throw;
            }

            cts = new CancellationTokenSource();
            _udp = udp;
            _cts = cts;
            _running = true;
        }

        _ = Task.Run(() => ReceiveLoopAsync(cts.Token));
        Log($"GVCP 服务已启动，监听 UDP 0.0.0.0:{port}");
    }

    /// <summary>停止服务并释放控制权。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        UdpClient? udp;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            cts = _cts;
            _cts = null;
            udp = _udp;
            _udp = null;
        }

        try { cts?.Cancel(); } catch { /* 忽略取消异常 */ }
        try { udp?.Close(); } catch { /* 关闭失败可忽略 */ }
        try { cts?.Dispose(); } catch { /* 忽略释放异常 */ }

        ReleaseControl();
        Log("GVCP 服务已停止");
    }

    /// <summary>
    /// 尝试获得控制权：无控制者或来自同一 IP 时成功。
    /// 成功即记录控制者端点并启动/刷新心跳；被其他 IP 占用时返回 false。
    /// </summary>
    public bool TryAcquireControl(IPEndPoint from)
    {
        ArgumentNullException.ThrowIfNull(from);

        lock (_gate)
        {
            if (_controller is not null)
            {
                if (_controller.Address.Equals(from.Address))
                {
                    _heartbeat.Touch();          // 同 IP 重复申请 = 心跳刷新
                    return true;
                }

                return false;                    // 其他 IP 已占用
            }

            _controller = new IPEndPoint(from.Address, from.Port);
            _heartbeat.Start();                  // 记录控制者 + 心跳开始
            return true;
        }
    }

    /// <summary>释放控制权（心跳超时 / 写 0x0A00=0 / 服务停止时调用）；0x0A00 寄存器同步回零。</summary>
    public void ReleaseControl()
    {
        IPEndPoint? previous;
        lock (_gate)
        {
            previous = _controller;
            _controller = null;
            _heartbeat.Stop();
        }

        if (previous is null)
        {
            return;
        }

        _registers.WriteRegister(GevRegisters.ControlPrivilege, 0);   // 供回读与上层感知控制权变化
        Log($"控制权已释放（原控制端 {previous}）");
    }

    /// <summary>来自当前控制者的请求刷新心跳计时（非控制者调用无任何效果）。</summary>
    public void TouchHeartbeat(IPEndPoint from)
    {
        lock (_gate)
        {
            if (_controller is not null && _controller.Address.Equals(from.Address))
            {
                _heartbeat.Touch();
            }
        }
    }

    /// <summary>
    /// 应用强制 IP 配置（ForceIP）：更新身份信息，并写入持久 IP 寄存器（0x064C/65C/66C）
    /// 与当前 IP 寄存器（0x0024/34/44，Discovery 负载直接镜像这三个寄存器）。
    /// </summary>
    public void ApplyNetworkConfig(IPAddress ip, IPAddress subnet, IPAddress gateway)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(subnet);
        ArgumentNullException.ThrowIfNull(gateway);

        _identity.CurrentIp = ip;
        _identity.CurrentSubnetMask = subnet;
        _identity.CurrentGateway = gateway;

        uint ipValue = IpToUint(ip);
        uint subnetValue = IpToUint(subnet);
        uint gatewayValue = IpToUint(gateway);
        WriteRegisterBestEffort(GevRegisters.PersistentIp, ipValue);
        WriteRegisterBestEffort(GevRegisters.PersistentSubnet, subnetValue);
        WriteRegisterBestEffort(GevRegisters.PersistentGateway, gatewayValue);
        WriteRegisterBestEffort(GevRegisters.CurrentIp, ipValue);
        WriteRegisterBestEffort(GevRegisters.CurrentSubnet, subnetValue);
        WriteRegisterBestEffort(GevRegisters.CurrentGateway, gatewayValue);

        Log($"ForceIP 已应用：IP={ip} 子网={subnet} 网关={gateway}");
    }

    // ———————————————————————————— 收包与命令分发 ————————————————————————————

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        UdpClient? udp;
        lock (_gate)
        {
            udp = _udp;
        }

        if (udp is null)
        {
            return;
        }

        while (_running && !ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;   // Stop() 关闭了 socket
            }
            catch (SocketException ex)
            {
                Log($"GVCP 接收错误: {ex.SocketErrorCode}");
                continue;
            }

            _ = Task.Run(() => Handle(result.Buffer, result.RemoteEndPoint));
        }
    }

    /// <summary>单包处理（线程池执行）：try/catch 全包，保证服务不因异常退出。</summary>
    private void Handle(byte[] datagram, IPEndPoint from)
    {
        try
        {
            if (datagram.Length < GvcpHeaderCodec.Size)
            {
                return;   // 过短报文忽略
            }

            var header = GvcpHeaderCodec.Parse(datagram);
            if (header.Key != GvcpHeaderCodec.GvcpKey)
            {
                return;   // 非法包头（首字节 != 0x42）忽略
            }

            int payloadLen = Math.Min(header.Length, datagram.Length - GvcpHeaderCodec.Size);
            var payload = datagram.AsSpan(GvcpHeaderCodec.Size, Math.Max(0, payloadLen));

            // 契约：来自当前控制者的任何请求都刷新心跳计时
            TouchHeartbeat(from);

            switch ((GvcpCommand)header.Command)
            {
                case GvcpCommand.DiscoveryCmd:
                    // 单播/广播都回 DiscoveryAck：负载 = 引导寄存器区前 248 字节的标准镜像。
                    // 不再单独拼包——身份/IP/名称全部来自寄存器表，与 ReadReg/ReadMem 读到的值天然一致。
                    var discoveryPayload = new byte[DiscoveryPayloadSize];
                    _registers.ReadMemory(0, discoveryPayload);
                    SendAck(GvcpHeaderCodec.BuildAck(
                        GvcpCommand.DiscoveryAck, header.ReqId, discoveryPayload), from);
                    break;

                case GvcpCommand.ForceIpCmd:
                    HandleForceIp(header, payload, from);
                    break;

                case GvcpCommand.ReadRegCmd:
                    HandleReadRegisters(header, payload, from);
                    break;

                case GvcpCommand.WriteRegCmd:
                    HandleWriteRegisters(header, payload, from);
                    break;

                case GvcpCommand.ReadMemCmd:
                    HandleReadMemory(header, payload, from);
                    break;

                case GvcpCommand.WriteMemCmd:
                    HandleWriteMemory(header, payload, from);
                    break;

                case GvcpCommand.ResendCmd:
                    HandleResendCommand(payload, from);
                    break;

                default:
                    // 未知命令：按“ACK 命令码 = 命令码 + 1”回 NotImplemented 错误应答
                    SendError((GvcpCommand)(ushort)(header.Command + 1), header.ReqId,
                        GevStatus.NotImplemented, from);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"GVCP 报文处理异常（来自 {from}）: {ex.Message}");
        }
    }

    /// <summary>
    /// ForceIP：标准载荷 56 字节，MAC 在偏移 2、IP 在 20、子网在 36、网关在 52（GigE Vision 规范，
    /// 与 Wireshark dissector 一致）。兼容 20 字节紧凑变体（MAC@0、IP@8、子网@12、网关@16）。
    /// MAC 匹配才应用并回 ACK；不匹配回 InvalidHeader（客户端拿到的 MAC 来自 Discovery 负载，
    /// 因此 Discovery 寄存器镜像正确后本路径不应再被触发）。
    /// </summary>
    private void HandleForceIp(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        int macOffset, ipOffset, maskOffset, gatewayOffset;
        if (payload.Length >= 56)
        {
            macOffset = 2;
            ipOffset = 20;
            maskOffset = 36;
            gatewayOffset = 52;
        }
        else if (payload.Length >= 20)
        {
            macOffset = 0;
            ipOffset = 8;
            maskOffset = 12;
            gatewayOffset = 16;
        }
        else
        {
            SendError(GvcpCommand.ForceIpAck, header.ReqId, GevStatus.InvalidHeader, from);
            return;
        }

        var mac = payload.Slice(macOffset, 6);
        var identityMac = _identity.MacAddress;
        if (identityMac is not { Length: 6 } || !mac.SequenceEqual(identityMac))
        {
            Log($"ForceIP 拒绝：MAC 不匹配（请求者 {from}）");
            SendError(GvcpCommand.ForceIpAck, header.ReqId, GevStatus.InvalidHeader, from);
            return;
        }

        var ip = new IPAddress(payload.Slice(ipOffset, 4));
        var subnet = new IPAddress(payload.Slice(maskOffset, 4));
        var gateway = new IPAddress(payload.Slice(gatewayOffset, 4));
        ApplyNetworkConfig(ip, subnet, gateway);
        SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.ForceIpAck, header.ReqId, EmptyPayload), from);
    }

    /// <summary>ReadReg：payload = N×u32 地址；ack payload = N×u32 值（首个失败则整包回该错误状态）。</summary>
    private void HandleReadRegisters(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length == 0 || (payload.Length & 0x3) != 0)
        {
            SendError(GvcpCommand.ReadRegAck, header.ReqId, GevStatus.InvalidParameter, from);
            return;
        }

        int count = payload.Length / 4;
        var values = new uint[count];
        GevStatus error = GevStatus.Success;
        for (int i = 0; i < count; i++)
        {
            uint address = ReadU32(payload, i * 4);
            var st = _registers.ReadRegister(address, out uint value);
            if (st != GevStatus.Success)
            {
                error = st;
                Log($"ReadReg 0x{address:X8} 失败: {st}");
                break;
            }

            values[i] = value;
        }

        if (error != GevStatus.Success)
        {
            SendError(GvcpCommand.ReadRegAck, header.ReqId, error, from);
            return;
        }

        var ackPayload = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            WriteU32(ackPayload.AsSpan(i * 4), values[i]);
        }

        SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.ReadRegAck, header.ReqId, ackPayload), from);
    }

    /// <summary>
    /// WriteReg：payload = 地址/值对，逐对依次写；ack payload = index(u32) = 成功写入个数。
    /// 写 0x0A00 走控制权仲裁；任一对失败即回该错误状态，未写入的地址跳过。
    /// </summary>
    private void HandleWriteRegisters(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length == 0 || (payload.Length & 0x7) != 0)
        {
            SendError(GvcpCommand.WriteRegAck, header.ReqId, GevStatus.InvalidParameter, from);
            return;
        }

        int pairCount = payload.Length / 8;
        int written = 0;
        GevStatus error = GevStatus.Success;
        for (int i = 0; i < pairCount; i++)
        {
            uint address = ReadU32(payload, i * 8);
            uint value = ReadU32(payload, (i * 8) + 4);

            GevStatus st;
            if (address == GevRegisters.ControlPrivilege)
            {
                // 控制权仲裁：0=释放；非 0=申请（无主或同 IP 成功并刷新，他 IP 占用回 AccessDenied）
                st = HandleControlPrivilegeWrite(value, from);
            }
            else
            {
                st = _callbacks.OnWriteRegister(address, value, from);   // 相机层语义校验
                if (st == GevStatus.Success)
                {
                    st = _registers.WriteRegister(address, value);       // 校验通过后统一落表
                }
            }

            if (st != GevStatus.Success)
            {
                error = st;
                Log($"WriteReg 0x{address:X8}={value} 失败: {st}");
                break;
            }

            written++;
        }

        if (error != GevStatus.Success)
        {
            SendError(GvcpCommand.WriteRegAck, header.ReqId, error, from);
            return;
        }

        // Aravis 校验 WriteRegAck 总长 ≥ 包头+4（payload 为 u32 index）
        var ackPayload = new byte[4];
        WriteU32(ackPayload.AsSpan(0), (uint)written);
        SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.WriteRegAck, header.ReqId, ackPayload), from);
    }

    /// <summary>0x0A00 控制权写入仲裁。</summary>
    private GevStatus HandleControlPrivilegeWrite(uint value, IPEndPoint from)
    {
        if (value == 0)
        {
            ReleaseControl();
            return GevStatus.Success;
        }

        if (!TryAcquireControl(from))
        {
            Log($"控制权申请被拒绝：已被 {Controller} 占用（请求者 {from}）");
            return GevStatus.AccessDenied;
        }

        // 获得控制（或同 IP 刷新）：记录申请值供回读
        _registers.WriteRegister(GevRegisters.ControlPrivilege, value);
        return GevStatus.Success;
    }

    /// <summary>
    /// ReadMem：payload = addr(u32) + count(u32，按 4 字节对齐)；
    /// ack payload = addr(u32) 回显 + 数据。count 上限 512（Aravis 按此分块拉取 XML blob）。
    /// </summary>
    private void HandleReadMemory(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 8)
        {
            SendError(GvcpCommand.ReadMemAck, header.ReqId, GevStatus.InvalidParameter, from);
            return;
        }

        uint address = ReadU32(payload, 0);
        ulong requested = ReadU32(payload, 4);
        int count = requested > (ulong)MaxReadMemCount ? MaxReadMemCount : (int)requested;
        count -= count % 4;            // 内存访问 4 字节对齐
        if (count <= 0)
        {
            SendError(GvcpCommand.ReadMemAck, header.ReqId, GevStatus.InvalidParameter, from);
            return;
        }

        var data = new byte[count];
        var st = _registers.ReadMemory(address, data);
        if (st != GevStatus.Success)
        {
            Log($"ReadMem 0x{address:X8}+{count} 失败: {st}");
            SendError(GvcpCommand.ReadMemAck, header.ReqId, st, from);
            return;
        }

        var ackPayload = new byte[4 + count];
        WriteU32(ackPayload.AsSpan(0), address);   // 地址回显（Aravis 据此定位数据起点）
        data.CopyTo(ackPayload, 4);
        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.ReadMemAck, header.ReqId, ackPayload), from);
    }

    /// <summary>WriteMem：payload = addr(u32) + 数据；ack payload = addr(u32) 回显。</summary>
    private void HandleWriteMemory(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 4)
        {
            SendError(GvcpCommand.WriteMemAck, header.ReqId, GevStatus.InvalidParameter, from);
            return;
        }

        uint address = ReadU32(payload, 0);
        var data = payload.Slice(4);   // 紧跟地址之后，无保留字段（Aravis 线格式）

        // 与 WriteReg 路径同权的相机层语义校验（逐 4 字节字；内存区地址默认放行，由寄存器表做 RO 检查）
        for (int offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            uint wordAddress = address + (uint)offset;
            uint wordValue = ReadU32(data, offset);
            var vst = _callbacks.OnWriteRegister(wordAddress, wordValue, from);
            if (vst != GevStatus.Success)
            {
                Log($"WriteMem 0x{wordAddress:X8}={wordValue} 校验失败: {vst}");
                SendError(GvcpCommand.WriteMemAck, header.ReqId, vst, from);
                return;
            }
        }

        var st = _registers.WriteMemory(address, data);
        if (st != GevStatus.Success)
        {
            Log($"WriteMem 0x{address:X8}+{data.Length} 失败: {st}");
            SendError(GvcpCommand.WriteMemAck, header.ReqId, st, from);
            return;
        }

        var ackPayload = new byte[4];
        WriteU32(ackPayload.AsSpan(0), address);
        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.WriteMemAck, header.ReqId, ackPayload), from);
    }

    /// <summary>
    /// ResendCmd：payload = block_id(u32) + first_packet_id(u32) + last_packet_id(u32)
    /// （仅低 24 位有效；last=0xFFFFFFFF/0xFFFF 表示直到帧尾）。不回 ACK，直接转交流发送器重发数据包。
    /// </summary>
    private void HandleResendCommand(ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 12)
        {
            Log($"ResendCmd 载荷不足，忽略（来自 {from}）");
            return;
        }

        ushort blockId = (ushort)(ReadU32(payload, 0) & 0xFFFF);
        ushort firstId = (ushort)(ReadU32(payload, 4) & 0xFFFF);
        ushort lastId = (ushort)(ReadU32(payload, 8) & 0xFFFF);
        _callbacks.OnResendRequest(from, 0, blockId, firstId, lastId);
    }

    // ———————————————————————————— 内部工具 ————————————————————————————

    private void SendAck(byte[] packet, IPEndPoint to)
    {
        UdpClient? udp;
        lock (_gate)
        {
            udp = _udp;
        }

        if (udp is null)
        {
            return;
        }

        try
        {
            udp.Send(packet, packet.Length, to);
        }
        catch (SocketException ex)
        {
            Log($"GVCP ACK 发送失败: {ex.SocketErrorCode}");   // 发送失败仅记日志
        }
        catch (ObjectDisposedException)
        {
            // 服务停止瞬间的发送直接忽略
        }
    }

    /// <summary>发送错误应答：status 置于包头前两字节（[0]=0x80，[1]=错误码），无 payload。</summary>
    private void SendError(GvcpCommand ackCommand, ushort reqId, GevStatus status, IPEndPoint to)
        => SendAck(GvcpHeaderCodec.BuildError(ackCommand, reqId, status), to);

    private void WriteRegisterBestEffort(uint address, uint value)
    {
        var st = _registers.WriteRegister(address, value);
        if (st != GevStatus.Success)
        {
            Log($"警告：寄存器 0x{address:X8} 写入被忽略（{st}，可能未定义）");
        }
    }

    private void OnRegisterWrittenForHeartbeat(uint address, uint value)
    {
        if (address != GevRegisters.HeartbeatTimeout)
        {
            return;
        }

        int ms = value >= int.MaxValue ? int.MaxValue : (int)value;
        if (ms < MinHeartbeatTimeoutMs)
        {
            ms = MinHeartbeatTimeoutMs;   // 契约：心跳超时下限 500ms
        }

        _heartbeat.Timeout = TimeSpan.FromMilliseconds(ms);
    }

    private void OnHeartbeatExpired()
    {
        Log("控制心跳超时，自动释放控制权");
        ReleaseControl();
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int offset)
        => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    private static void WriteU32(Span<byte> b, uint value)
    {
        b[0] = (byte)(value >> 24);
        b[1] = (byte)(value >> 16);
        b[2] = (byte)(value >> 8);
        b[3] = (byte)value;
    }

    private static uint IpToUint(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        var bytes = address.GetAddressBytes();
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    private void Log(string message) => _callbacks.OnLog(message);

    public void Dispose()
    {
        Stop();
        _registers.RegisterWritten -= OnRegisterWrittenForHeartbeat;
        _heartbeat.Expired -= OnHeartbeatExpired;
        _heartbeat.Dispose();
    }
}
