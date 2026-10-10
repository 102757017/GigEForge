# GigEForge 协议实现说明（GVCP / GVSP）

本文档描述 `GigEVision.Core` 自研设备端协议栈的实现细节，供调试与第三方客户端对接参考。
协议基线：**GigE Vision 1.2**（GVCP/GVSP 经典模式，非 GenDC）+ **GenICam GenApi XML（local 模式）**。

- 所有字节序：**大端（Big-Endian）**，手工位移编解码；
- GVCP 控制通道固定 **UDP 3956**（绑定 `0.0.0.0`，`ReuseAddress`）；
- GVSP 流通道源端口由系统分配（绑定 `BindAddress`，port=0），实际端口写入寄存器表并由客户端经 0x0D00 读取。

---

## 1. 寄存器总表

寄存器宽度均为 4 字节（u32，大端）；标注「内存区」的地址须用 ReadMem/WriteMem 访问。
「RO」= 客户端写入回 `WriteProtect(0x800C)`。

| 地址 | 名称 | 读写 | 含义 |
|------|------|------|------|
| `0x00000004` | Version | RO | GVCP 协议版本，固定 `0x01020000`（1.2） |
| `0x00000008` | ManufacturerName | RO | 32B ASCII 内存区，厂商名 `GigEForge` |
| `0x00000048` | ModelName | RO | 32B ASCII 内存区，型号 `VirtualCam-01` |
| `0x00000088` | DeviceVersion | RO | 32B ASCII 内存区，设备版本 `1.0.0` |
| `0x000000C8` | ManufacturerInfo | RO | 48B ASCII 内存区 |
| `0x000000F8` | SerialNumber | RO | 16B ASCII 内存区，序列号（即 Discovery DeviceID） |
| `0x00000108` | UserDefinedName | RO | 64B ASCII 内存区，用户自定义名 |
| `0x00000200` | XmlUrl | RO | 512B ASCII 内存区，`local;GigEForge.xml;1048576;<xml字节长度>` |
| `0x00000600` | DeviceMode | RO | 设备模式，固定 0（经典 GVSP，非 GenDC） |
| `0x00000640` | PersistentIp | RW | 持久 IP |
| `0x00000644` | PersistentSubnet | RW | 持久子网掩码 |
| `0x00000648` | PersistentGateway | RW | 持久网关 |
| `0x000006A4` | NetworkIfCount | RO | 网络接口数 = 1 |
| `0x000006C8` | CurrentIp | RO* | 当前 IP（*仅 Force IP 内部刷新，客户端写回 WriteProtect） |
| `0x000006CC` | CurrentSubnet | RO* | 当前子网掩码（同上） |
| `0x000006D0` | CurrentGateway | RO* | 当前网关（同上） |
| `0x00000938` | HeartbeatTimeout | RW | 心跳超时（毫秒，写入 clamp ≥500，默认 3000） |
| `0x00000A00` | ControlPrivilege | RW | 控制权：写 0 释放；非 0 申请控制（仲裁见第 5 节，占用时回 `AccessDenied`） |
| `0x00000B00` | StreamChannelCount | RO | 流通道数 = 1 |
| `0x00000BC8` | TickFreqHigh | RO | 时间戳计数频率高 32 位（1e9 Hz → `0x3B9ACA00`） |
| `0x00000BCC` | TickFreqLow | RO | 时间戳计数频率低 32 位（0） |
| `0x00000D00` | Sc0Port | RW | 流通道 0：主机端监听端口（客户端接收 GVSP 的 UDP 端口） |
| `0x00000D04` | Sc0PacketSize | RW | 流通道 0：GVSP 包大小，写入 clamp [576,16000]，默认 1500 |
| `0x00000D08` | Sc0PacketDelay | RW | 流通道 0：包间延迟（微秒） |
| `0x00000D10` | Sc0DestIp | RW | 流通道 0：目的地 IP；0 = 回退使用当前控制者 IP |
| `0x00000D18` | Sc0PayloadSize | RO | 流通道 0：负载大小（随宽高/格式自动维护） |
| `0x00100000` | XmlBlobBase | RO | GenICam XML 内存区（256KB 上限），GenICam local 模式经 0x0200 URL 寻址 |
| `0x00140000` | Width | RW | 图像宽度（16..8192 且偶数，非法回 `InvalidParameter(0x8015)`） |
| `0x00140004` | Height | RW | 图像高度（16..8192） |
| `0x00140008` | PixelFormat | RW | 像素格式（PFNC 码，见第 4 节，仅支持 4 种） |
| `0x0014000C` | AcquisitionStart | W | 命令寄存器：写 1 启动采集 |
| `0x00140010` | AcquisitionStop | W | 命令寄存器：写 1 停止采集 |
| `0x00140014` | AcquisitionFrameRate | RW | 采集帧率（u32 fps，1..120） |
| `0x00140018` | PayloadSize | RO | 负载大小（0x0D18 的镜像） |
| `0x0014001C` | TestPattern | RW | 测试图案：0 关，1 彩条 / 2 渐变 / 3 网格 / 4 动态十字 |

> 宽/高/格式写入成功后，`0x0D18` 与 `0x00140018` 自动刷新为 `W×H×bpp/8`。

---

## 2. GVCP 命令 / ACK 布局（UDP 3956）

包头 8 字节（大端）：

```
[0]      = 0x42            GVCP key（非法包头直接忽略）
[1]      = flags           bit0=ACK 需要应答, bit1=广播
[2..3]   = command (u16)   命令码
[4..5]   = length  (u16)   载荷长度（不含包头）
[6..7]   = req_id  (u16)   请求标识（ACK 原样回传）
```

ACK 包：`[2..3] = 命令码+1`（ack 命令码），`length = 2 + payload`，payload 前 2 字节为 status u16。

命令码与状态码：

```csharp
public enum GvcpCommand : ushort {
    DiscoveryCmd = 0x0002, DiscoveryAck = 0x0003,
    ForceIpCmd = 0x0004, ForceIpAck = 0x0005, PendingAck = 0x0006,
    ResendCmd = 0x0040, ResendAck = 0x0041,
    ReadRegCmd = 0x0080, ReadRegAck = 0x0081,
    WriteRegCmd = 0x0082, WriteRegAck = 0x0083,
    ReadMemCmd = 0x0084, ReadMemAck = 0x0085,
    WriteMemCmd = 0x0086, WriteMemAck = 0x0087,
}
public enum GevStatus : ushort {
    Success = 0x0000, NotImplemented = 0x8001, Error = 0x8002, AccessDenied = 0x8003,
    PortInUse = 0x8006, InvalidHeader = 0x8009, InvalidAddress = 0x800B,
    WriteProtect = 0x800C, BadAlignment = 0x800D, NotAvailable = 0x8013, InvalidParameter = 0x8015,
}
```

各命令语义与载荷：

| 命令 | 请求载荷 | ACK 载荷 | 说明 |
|------|----------|----------|------|
| `0x0002` Discovery | 无 | 250B 设备描述（第 3 节） | 单播/广播一律应答 |
| `0x0004` ForceIP | `[0..1]` 保留、`[2..7]` MAC(6B)、`[8..11]` IP、`[12..15]` 子网、`[16..19]` 网关 | 无（status-only） | MAC 匹配则应用（写 identity + 0x0640/44/48/6C 与 0x06C8/CC/D0）并回 Success；不匹配回 `InvalidHeader` |
| `0x0080` ReadReg | N × u32 地址 | N × u32 值 | 依地址顺序读取；首个失败则全部回该错误状态 |
| `0x0082` WriteReg | 地址/值对 × N | `index(u16)` = 成功写入个数 | 逐对写入；任一失败回该错误状态（未写入的地址跳过） |
| `0x0084` ReadMem | `addr(u32) + count(u16)` | `index(u16)` + 数据 | count 上限 500（防超 MTU），多余截断并 4 字节对齐 |
| `0x0086` WriteMem | `addr(u32) + 数据` | `index(u16)` = 写入字节数 | — |
| `0x0040` Resend | `stream_ch(u16) + block_id(u16) + first(u16) + last(u16)` | —（转 GVSP 重发） | 到达 GVCP 端口的情形；`last=0xFFFF` 表示「直到结尾」，见第 4 节 |
| 未知命令 | — | ack=cmd+1，status=`NotImplemented(0x8001)` | — |

ReadReg/ReadMem 地址未定义 → `InvalidAddress(0x800B)`；非 4 字节对齐 → `BadAlignment(0x800D)`；
RO 地址写 → `WriteProtect(0x800C)`。

---

## 3. DiscoveryAck 载荷（250 字节，全部大端）

```
[0]     spec_major = 1
[1]     spec_minor = 2
[2..3]   MAC 高 16 位
[4..7]   MAC 低 32 位
[8..11]  当前 IP
[12..15] 当前子网
[16..19] 当前网关
[20..21] ip_options = 0
[22..53]  ManufacturerName   (32B, ASCII, 0 结尾 0 填充)
[54..85]  ModelName          (32B)
[86..117] DeviceVersion     (32B)
[118..165]ManufacturerInfo  (48B)
[166..181]SerialNumber      (16B)
[182..245]UserDefinedName   (64B)
[246]    ip_config_options = 0x05  (LLA | Persistent capable)
[247]    ip_config_current = 0x01  (LLA 生效)
[248]    reserved = 0
[249]    reserved = 0
```

ASCII 名称区编码规则：超长截断（保留末位 0 结尾）、剩余 0 填充、非可打印字符替换为 `?`。

---

## 4. GVSP 流通道（UDP，源端口自选）

包头 8 字节（大端）：

```
[0]      = 0x37            GVSP key
[1]      = flags = 0
[2..3]   = format          Leader=0x0001 / Trailer=0x0002 / Data=0x0003
[4..5]   = packet_id       Leader=0，Data=1..N，Trailer=N+1
[6..7]   = block_id        帧号，从 1 自增，65535 后回 1
```

### Leader（包头 8B + 载荷 34B）

```
[0..1]   保留 = 0
[2..3]   payload_type = 1 (image)
[4..9]   timestamp (u48, ns)
[10..13] pixel_format (PFNC)
[14..17] size_x
[18..21] size_y
[22..25] offset_x = 0
[26..29] offset_y = 0
[30..31] padding_x = 0
[32..33] padding_y = 0
```

### Data（包头 8B + 像素分片）

像素按行切片，每片 ≤ `packetSize − 8` 字节（packetSize 来自 0x0D04，默认 1500）。

### Trailer（包头 8B + 载荷 8B）

```
[0..1]   保留 = 0
[2..3]   payload_type = 1
[4..7]   size_y
```

### PFNC 像素格式（仅支持以下 4 种）

| 名称 | PFNC 码 | 位深/像素 | PayloadSize |
|------|---------|-----------|-------------|
| Mono8 | `0x01080001` | 8 | W×H |
| BayerRG8 | `0x01080009` | 8 | W×H |
| RGB8 | `0x02180014` | 24 | W×H×3 |
| BGR8 | `0x02180015` | 24 | W×H×3 |

### 重发机制

- 每帧整包序列（Leader + Data×N + Trailer）在发送前存入 `PacketCache`（环形保留**最近 4 帧**，回绕撞号整体替换）；
- 客户端对丢包重发 `ResendCmd(0x0040)`：`first..last` 区间内的包按原包头格式重发（`last=0xFFFF` 重发到帧尾），
  `packet_id` 与缓存下标一致（0=Leader，N+1=Trailer）；
- 到达 GVCP 端口的重发请求在 GvcpServer 解析后回调 `OnResendRequest`，转交 `GvspTransmitter.HandleResend`；
- 统计：`StreamStats.ResendRequests`（请求数）与 `ResendPackets`（实发重发包数）。

---

## 5. 控制权与心跳时序

```
 客户端                          GigEForge (GVCP 3956)               采集线程
   |                                     |                               |
   |--- WriteReg 0x0A00 = 1 ------------>|                               |
   |    (无控制器 或 同一 IP)             |-- TryAcquireControl 成功      |
   |<-- WriteRegAck(status=0) -----------|   记录控制者 EP，心跳启动       |
   |                                     |                               |
   |--- WriteReg 0x0D00 = clientPort --->|  流目的地 = 0x0D10 或控制者 IP  |
   |--- WriteReg 0x0D04 = packetSize --->|  clamp [576,16000]            |
   |--- WriteReg 0x00140000/04 = W/H --->|  语义校验 + 刷新 PayloadSize   |
   |--- WriteReg 0x0014000C = 1 -------->|------------------------------>| StartAcquisition
   |                                     |                               | 按 fps 节拍发 GVSP
   |<================= GVSP Leader/Data.../Trailer (UDP) ================|
   |                                     |                               |
   |--- 任意 GVCP 请求（心跳刷新） ------->|  TouchHeartbeat               |
   |    ... 超时 = 0x0938 (默认 3000ms)   |                               |
   |                                     |-- 心跳超时：释放控制权 + 停止采集|
   |                                     |                               |
   |--- WriteReg 0x0A00 = 0 ------------>|  ReleaseControl（停止采集）     |
   |<-- WriteRegAck(status=0) -----------|                               |
```

规则摘要：

- 写 `0x0A00`：`value==0` 释放控制；非 0 且当前无控制器**或来自同一 IP** → 获得控制（记录控制者 EP + 心跳开始）；
  其他 IP 已占用 → ACK `AccessDenied(0x8003)`；
- **任何**来自当前控制者的 GVCP 请求都刷新心跳计时；0x0938 写入即时生效（下限 500ms）；
- 心跳超时 → 自动释放控制权并停止采集（GVSP 停发）；
- 流目的地：0x0D10 非 0 用之，否则用当前控制者 IP；端口 = 0x0D00；两者都无 → 跳过发送（仅统计帧）。

---

## 6. 实现状态清单

已实现：

- GVCP：Discovery（单播/广播恒答 250B）、Force IP、ReadReg/WriteReg/ReadMem/WriteMem、Resend 转发、
  控制权仲裁（0x0A00）、心跳（0x0938）、未知命令 NotImplemented；
- GVSP：Leader/Data/Trailer（经典模式单流通道）、block_id 回绕、包重发（缓存 4 帧）、包大小/包间延迟可调；
- GenICam：GenApi XML local 模式自动生成（DeviceControl / ImageFormatControl / AcquisitionControl），
  XML blob 挂载于 0x00100000，URL 于 0x0200；
- 网络：LLA 自动配置（ip_config_current=0x01）、Force IP（持久 IP 寄存器联动）、多网卡绑定。

未实现（与 GigE Vision 规范的已知差异）：

- 多流通道（仅 1 个流通道，0x0B00 恒为 1）；
- Extended ID / GVCP 扩展包头（flags 的高位扩展）；
- GenDC 传输格式（payload_type 仅 image）；
- PacketResend ACK（0x0041，重发为静默 GVSP 包，不发 ACK）；
- 持久 IP 切换的重启语义（写 0x0640 系列仅落表，需 Force IP 生效）。
