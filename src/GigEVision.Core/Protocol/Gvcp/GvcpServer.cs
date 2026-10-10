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

    /// <summary>ReadMem 单次读取字节数上限（防止超过 MTU）。</summary>
    public const int MaxReadMemCount = 500;

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
    /// 应用强制 IP 配置（ForceIP）：更新身份信息，并写入持久 IP 寄存器（0x0640/44/48）
    /// 与当前 IP 寄存器（0x06C8/CC/D0）。
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
                    // 单播/广播都回 DiscoveryAck（payload 250B 设备描述）
                    SendAck(GvcpHeaderCodec.BuildAck(
                        GvcpCommand.DiscoveryAck, header.ReqId, (ushort)GevStatus.Success,
                        _identity.BuildDiscoveryPayload()), from);
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
                    // 未知命令：按“ACK 命令码 = 命令码 + 1”回 NotImplemented
                    SendAck(GvcpHeaderCodec.BuildAck(
                        (GvcpCommand)(ushort)(header.Command + 1), header.ReqId,
                        (ushort)GevStatus.NotImplemented, EmptyPayload), from);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"GVCP 报文处理异常（来自 {from}）: {ex.Message}");
        }
    }

    /// <summary>ForceIP：payload = 保留(2) + MAC(6) + IP(4) + 子网(4) + 网关(4)，全大端。MAC 匹配才应用。</summary>
    private void HandleForceIp(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 20)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.ForceIpAck, header.ReqId, (ushort)GevStatus.InvalidHeader, EmptyPayload), from);
            return;
        }

        var mac = payload.Slice(2, 6);
        var identityMac = _identity.MacAddress;
        if (identityMac is not { Length: 6 } || !mac.SequenceEqual(identityMac))
        {
            Log($"ForceIP 拒绝：MAC 不匹配（请求者 {from}）");
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.ForceIpAck, header.ReqId, (ushort)GevStatus.InvalidHeader, EmptyPayload), from);
            return;
        }

        var ip = new IPAddress(payload.Slice(8, 4));
        var subnet = new IPAddress(payload.Slice(12, 4));
        var gateway = new IPAddress(payload.Slice(16, 4));
        ApplyNetworkConfig(ip, subnet, gateway);
        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.ForceIpAck, header.ReqId, (ushort)GevStatus.Success, EmptyPayload), from);
    }

    /// <summary>ReadReg：payload = N×u32 地址；ack payload = N×u32 值（首个失败则整包回该错误状态）。</summary>
    private void HandleReadRegisters(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length == 0 || (payload.Length & 0x3) != 0)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.ReadRegAck, header.ReqId, (ushort)GevStatus.InvalidParameter, EmptyPayload), from);
            return;
        }

        int count = payload.Length / 4;
        var values = new uint[count];
        ushort status = (ushort)GevStatus.Success;
        for (int i = 0; i < count; i++)
        {
            uint address = ReadU32(payload, i * 4);
            var st = _registers.ReadRegister(address, out uint value);
            if (st != GevStatus.Success)
            {
                status = (ushort)st;
                Log($"ReadReg 0x{address:X8} 失败: {st}");
                break;
            }

            values[i] = value;
        }

        if (status != (ushort)GevStatus.Success)
        {
            SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.ReadRegAck, header.ReqId, status, EmptyPayload), from);
            return;
        }

        var ackPayload = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            WriteU32(ackPayload.AsSpan(i * 4), values[i]);
        }

        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.ReadRegAck, header.ReqId, (ushort)GevStatus.Success, ackPayload), from);
    }

    /// <summary>
    /// WriteReg：payload = 地址/值对，逐对依次写；ack payload = index(u16) = 成功写入个数。
    /// 写 0x0A00 走控制权仲裁；任一对失败即回该错误状态，未写入的地址跳过。
    /// </summary>
    private void HandleWriteRegisters(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length == 0 || (payload.Length & 0x7) != 0)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.WriteRegAck, header.ReqId, (ushort)GevStatus.InvalidParameter, EmptyPayload), from);
            return;
        }

        int pairCount = payload.Length / 8;
        int written = 0;
        ushort status = (ushort)GevStatus.Success;
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
                status = (ushort)st;
                Log($"WriteReg 0x{address:X8}={value} 失败: {st}");
                break;
            }

            written++;
        }

        var ackPayload = new byte[2];
        WriteU16(ackPayload.AsSpan(0), (ushort)written);
        SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.WriteRegAck, header.ReqId, status, ackPayload), from);
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
    /// ReadMem：payload = addr(u32) + count(u16)；ack payload = index(u16) + 数据。
    /// count 上限 500（超限截断）并向下对齐到 4 字节；对齐/越界由寄存器表回 BadAlignment/InvalidAddress。
    /// </summary>
    private void HandleReadMemory(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 6)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.ReadMemAck, header.ReqId, (ushort)GevStatus.InvalidParameter, EmptyPayload), from);
            return;
        }

        uint address = ReadU32(payload, 0);
        int count = ReadU16(payload, 4);
        if (count > MaxReadMemCount)
        {
            count = MaxReadMemCount;   // 防止超 MTU：截断处理
        }

        count -= count % 4;            // 内存访问 4 字节对齐
        if (count <= 0)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.ReadMemAck, header.ReqId, (ushort)GevStatus.InvalidParameter, EmptyPayload), from);
            return;
        }

        var data = new byte[count];
        var st = _registers.ReadMemory(address, data);
        if (st != GevStatus.Success)
        {
            Log($"ReadMem 0x{address:X8}+{count} 失败: {st}");
            SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.ReadMemAck, header.ReqId, (ushort)st, EmptyPayload), from);
            return;
        }

        var ackPayload = new byte[2 + count];
        WriteU16(ackPayload.AsSpan(0), 0);   // index：保留字段（协议建议 0）
        data.CopyTo(ackPayload, 2);
        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.ReadMemAck, header.ReqId, (ushort)GevStatus.Success, ackPayload), from);
    }

    /// <summary>WriteMem：payload = addr(u32) + 数据；ack payload = index(u16) = 写入字节数。</summary>
    private void HandleWriteMemory(GvcpHeader header, ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 4)
        {
            SendAck(GvcpHeaderCodec.BuildAck(
                GvcpCommand.WriteMemAck, header.ReqId, (ushort)GevStatus.InvalidParameter, EmptyPayload), from);
            return;
        }

        uint address = ReadU32(payload, 0);
        var data = payload.Slice(4);
        var st = _registers.WriteMemory(address, data);
        if (st != GevStatus.Success)
        {
            Log($"WriteMem 0x{address:X8}+{data.Length} 失败: {st}");
            SendAck(GvcpHeaderCodec.BuildAck(GvcpCommand.WriteMemAck, header.ReqId, (ushort)st, EmptyPayload), from);
            return;
        }

        var ackPayload = new byte[2];
        WriteU16(ackPayload.AsSpan(0), (ushort)data.Length);
        SendAck(GvcpHeaderCodec.BuildAck(
            GvcpCommand.WriteMemAck, header.ReqId, (ushort)GevStatus.Success, ackPayload), from);
    }

    /// <summary>
    /// ResendCmd：payload = stream_channel(u16) + block_id(u16) + first_packet_id(u16) + last_packet_id(u16)
    /// （last=0xFFFF 表示直到帧尾）。不回 ACK，直接转交流发送器重发数据包。
    /// </summary>
    private void HandleResendCommand(ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        if (payload.Length < 8)
        {
            Log($"ResendCmd 载荷不足，忽略（来自 {from}）");
            return;
        }

        ushort streamChannel = ReadU16(payload, 0);
        ushort blockId = ReadU16(payload, 2);
        ushort firstId = ReadU16(payload, 4);
        ushort lastId = ReadU16(payload, 6);
        _callbacks.OnResendRequest(from, streamChannel, blockId, firstId, lastId);
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

        _heartbeat.TimeoutPeriod = TimeSpan.FromMilliseconds(ms);
    }

    private void OnHeartbeatExpired()
    {
        Log("控制心跳超时，自动释放控制权");
        ReleaseControl();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int offset)
        => (ushort)((b[offset] << 8) | b[offset + 1]);

    private static uint ReadU32(ReadOnlySpan<byte> b, int offset)
        => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    private static void WriteU16(Span<byte> b, ushort value)
    {
        b[0] = (byte)(value >> 8);
        b[1] = (byte)value;
    }

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
