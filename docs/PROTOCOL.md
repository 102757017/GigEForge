# GigEForge 协议实现说明（GVCP / GVSP）

本文档描述 `GigEVision.Core` 自研设备端协议栈的实现细节，供调试与第三方客户端对接参考。
协议基线：**GigE Vision 1.2**（GVCP/GVSP 经典模式，非 GenDC）+ **GenICam GenApi XML（local 模式，v2 模板）**。

- 所有字节序：**大端（Big-Endian）**，手工位移编解码；
- GVCP 控制通道固定 **UDP 3956**（绑定 `0.0.0.0`，`ReuseAddress`）；
- GVSP 流通道源端口由系统分配（绑定 `BindAddress`，port=0），实际端口写入 **0x0D1C（GevSCSP）** 供客户端回读；
- 流目的地 = 客户端写 **0x0D18（GevSCDA）+ 0x0D00（GevSCPHostPort）**，任一为 0 时回退当前控制者 IP/端口；
- 引导寄存器布局与 **Aravis 0.8.x 注入节点 / Wireshark dissector 逐字一致**（见第 7 节兼容性要点）。

---

## 1. 寄存器总表

寄存器宽度均为 4 字节（u32，大端）；标注「内存区」的地址须用 ReadMem/WriteMem 访问。
「RO」= 客户端写入回 `WriteProtect(0x8004)`；「RO*」= 写容忍（ACK 成功但不生效）。

| 地址 | 名称 | 读写 | 含义 |
|------|------|------|------|
| `0x00000000` | Version | RO | 协议版本 `major<<16\|minor`，固定 `0x00010002`（1.2） |
| `0x00000004` | DeviceMode | RO | bit31=大端设备；本设备按大端寄存器口径声明 `0x80000000` |
| `0x00000008` | MacHigh | RO | MAC 高 32 位（仅低 16 位有效） |
| `0x0000000C` | MacLow | RO | MAC 低 32 位 |
| `0x00000010` | SupportedIpConfig | RO | 支持的 IP 配置：`0x05`（bit0 持久 IP、bit2 LLA） |
| `0x00000014` | CurrentIpConfig | RO | 当前生效配置：`0x01`（持久 IP） |
| `0x00000024` | CurrentIp | RO* | 当前 IP（*仅 Force IP 内部刷新，客户端写回 WriteProtect） |
| `0x00000034` | CurrentSubnet | RO* | 当前子网掩码（同上） |
| `0x00000044` | CurrentGateway | RO* | 当前网关（同上） |
| `0x00000048` | ManufacturerName | RO | 32B ASCII 内存区，厂商名（identity 注入，默认 `GigEForge`） |
| `0x00000068` | ModelName | RO | 32B ASCII 内存区，型号（默认 `VirtualCam-01`） |
| `0x00000088` | DeviceVersion | RO | 32B ASCII 内存区，设备版本 |
| `0x000000A8` | ManufacturerInfo | RO | 32B ASCII 内存区 |
| `0x000000D8` | SerialNumber | RO | 16B ASCII 内存区，序列号（即 Discovery DeviceID） |
| `0x000000E8` | UserDefinedName | RO | 16B ASCII 内存区，用户自定义名 |
| `0x00000200` | XmlUrl | RO | 512B ASCII 内存区，`local:///GigEForge.xml;00100000;<大小hex>` |
| `0x00000400` | XmlUrl1 | RO | 512B ASCII 内存区，第二 XML URL（镜像同一文件，客户端会探测读取） |
| `0x00000600` | NetworkIfCount | RO | 网络接口数 = 1 |
| `0x0000064C` | PersistentIp | RW | 持久 IP（接口 0） |
| `0x0000065C` | PersistentSubnet | RW | 持久子网掩码 |
| `0x0000066C` | PersistentGateway | RW | 持久网关 |
| `0x00000900` | MessageChannelCount | RO | 消息（事件）通道数 = 0 |
| `0x00000904` | StreamChannelCount | RO | 流通道数 = 1（Aravis create_stream 必读） |
| `0x00000920` | XmlFileCount | RO | GenICam XML 文件数 = 1 |
| `0x00000924` | XmlFileSelect | RO | 选中 XML 文件索引 = 0 |
| `0x00000928` | XmlFileSize | RO | XML 文件大小（字节，随模板动态生成） |
| `0x0000092C` | XmlFileCrc | RO | XML 文件 CRC32（zlib 兼容多项式 `0xEDB88320`，与 blob 实读一致） |
| `0x00000930` | XmlFileVersion | RO | XML 文件版本 `0x00010000` |
| `0x00000934` | GvcpCapability | RO | GVCP 能力位图：`0x00000007`（bit0=Concatenation、bit1=WriteMem、bit2=PacketResend）。**严禁把 XML URL 放本地址**——客户端会把 ASCII 当能力位解析 |
| `0x00000938` | HeartbeatTimeout | RW | 心跳超时（毫秒，写入 clamp ≥500，默认 3000） |
| `0x0000093C` | TickFreqHigh | RO | 时间戳计数频率高 32 位（1e9 Hz → 0） |
| `0x00000940` | TickFreqLow | RO | 时间戳计数频率低 32 位（`0x3B9ACA00`） |
| `0x00000944` | TimestampControl | RW | 时间戳控制（bit0 锁存请求；本实现接受写入） |
| `0x00000948` | TimestampLatchHigh | RO | 锁存时间戳高 32 位 |
| `0x0000094C` | TimestampLatchLow | RO | 锁存时间戳低 32 位 |
| `0x00000A00` | ControlPrivilege | RW | 控制权：写 0 释放；非 0 申请控制（仲裁见第 5 节，占用时回 `AccessDenied`） |
| `0x00000D00` | Sc0HostPort | RW | 流通道 0：**GevSCPHostPort**——客户端把它收流 socket 的 UDP 端口写进来（建流与测试包探测前都会写）；设备据此决定 GVSP 目的端口（低 16 位） |
| `0x00000D04` | Sc0PacketSize | RW | 流通道 0：**GevSCPSPacketSize**——值 bit15:0 = 包大小（**IP 包总字节数**，4 对齐，读回==写入、无 +28 夹紧）；bit31=FireTestPacket（GenApi Bit0，自清零）、bit30=DoNotFragment、bit29=BigEndian |
| `0x00000D08` | Sc0PacketDelay | RW | 流通道 0：GevSCPD 包间延迟（微秒） |
| `0x00000D18` | Sc0DestIp | RW | 流通道 0：GevSCDA GVSP 目的地 IP；0 = 回退使用当前控制者 IP |
| `0x00000D1C` | Sc0SourcePort | RO* | 流通道 0：GevSCSP GVSP 流源端口（低 16 位，Start() 时回填；*写容忍不生效） |
| `0x00000D20` | Sc0Capability | RO* | 流通道 0：SC 能力，恒 `0`（不声明 multipart → arv-test Multipart 按预期 SUCCESS；*写容忍不生效） |
| `0x00000D24` | Sc0Configuration | RW | 流通道 0：配置存储（仅存储，无语义位） |
| `0x00010000` | GenWidth | RW | GenICam Width（XML `<Integer>`，写后夹紧 [16..8192] 回写） |
| `0x00010004` | GenHeight | RW | GenICam Height（同上） |
| `0x00010008` | GenOffsetX | RW | 仅 0（XML Min=Max=0） |
| `0x0001000C` | GenOffsetY | RW | 仅 0 |
| `0x00010010` | GenSensorWidth | RO | 1280（XML WidthMax） |
| `0x00010014` | GenSensorHeight | RO | 720（XML HeightMax） |
| `0x00010018` | GenPixelFormat | RW | 仅 `0x01080001`（Mono8，与 XML 枚举一致） |
| `0x0001001C` | GenAcquisitionMode | RW | 0=Off 1=Continuous 2=SingleFrame |
| `0x00010020` | GenAcquisitionCommand | W | 命令：1=AcquisitionStart 0=AcquisitionStop |
| `0x00010024` | GenTriggerMode | RW | 0=Off 1=On（On 时等 TriggerSoftware 出帧） |
| `0x00010028` | GenTriggerSource | RW | 0=Line0 1=Software |
| `0x0001002C` | GenTriggerSoftware | W | 命令：1=触发一帧 |
| `0x00010030` | GenExposureTime | RW | IEEE754 单精度位图（µs，XML `<Float>` 10..1000000） |
| `0x00010034` | GenGain | RW | IEEE754 单精度位图（dB，XML `<Float>` 0..12） |
| `0x00010038` | GenFrameRate | RW | IEEE754 单精度位图（Hz，写后联动引擎 fps） |
| `0x0001003C` | GenTriggerActivation | RW | 仅 0（RisingEdge） |
| `0x00010040` | GenChunkModeActive | RW | 0/1（1 时 Leader/Trailer payload_type=0x4001，且 Chunk 块并入数据流末尾——Aravis 只扫描数据包负载区间，Trailer 包不计入 received_size） |
| `0x00010044` | GenChunkEnableWidth | RW | 0/1（ChunkSelector=Width 时 ChunkEnable 写入处） |
| `0x00010048` | GenChunkEnableHeight | RW | 0/1 |
| `0x0001004C` | GenChunkSelector | RW | 0=Width 1=Height |
| `0x00010050` | GenTriggerSelector | RW | 仅 0（FrameStart） |
| `0x00100000` | XmlBlobBase | RO | GenICam XML 内存区（256KB 上限），GenICam local 模式经 0x0200 URL 寻址 |
| `0x00140000` | Width | RW | 图像宽度（16..8192 且偶数，设备自定义区，App UI/SDK 使用） |
| `0x00140004` | Height | RW | 图像高度（16..8192，同上） |
| `0x00140008` | PixelFormat | RW | 像素格式（PFNC 码，见第 4 节，仅支持 4 种） |
| `0x0014000C` | AcquisitionStart | W | 命令寄存器：写 1 启动采集 |
| `0x00140010` | AcquisitionStop | W | 命令寄存器：写 1 停止采集 |
| `0x00140014` | AcquisitionFrameRate | RW | 采集帧率（u32 fps，1..120） |
| `0x00140018` | PayloadSize | RO | 负载大小（W×H×bpp/8，随宽高/格式自动维护） |
| `0x0014001C` | TestPattern | RW | 测试图案：0 关，1 彩条 / 2 渐变 / 3 网格 / 4 动态十字 |

> 宽/高/格式写入成功后，`0x00140018` 自动刷新为 `W×H×bpp/8`；GenICam XML 的 PayloadSize
> 为 SwissKnife `W*H`（从 0x10000/0x10004 特性寄存器实时计算，Mono8 下与上式一致）。
> ReadMem 对未定义地址不回错误：按 4 字节字逐字组装，未命中的字以 0 填充（兼容客户端对标准引导区的探测）。

---

## 2. GVCP 命令 / ACK 布局（UDP 3956）

包头 8 字节（大端）：

```
命令包：
[0]      = 0x42            GVCP key（非法包头直接忽略）
[1]      = flags           bit0=ACK 需要应答, bit1=广播
[2..3]   = command (u16)   命令码
[4..5]   = length  (u16)   载荷长度（不含包头）
[6..7]   = req_id  (u16)   请求标识（ACK 原样回传）

应答包：前两字节即 status u16（GigE Vision 标准）：
  成功：[0]=0x00, [1]=0x00        → Aravis 识别为 ACK 包（packet_type 0x00）
  错误：[0]=0x80, [1]=错误码低字节 → Aravis 识别为 ERROR 包（错误码取自 [1]）
[2..3]   = ack 命令码（命令码+1）
[4..5]   = payload 长度
[6..7]   = req_id
[8..]    = payload（成功 ACK；错误 ACK 无 payload）
```

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
    Success = 0x0000, NotImplemented = 0x8001, InvalidParameter = 0x8002, InvalidAddress = 0x8003,
    WriteProtect = 0x8004, BadAlignment = 0x8005, AccessDenied = 0x8006, Busy = 0x8007,
    PacketUnavailable = 0x800C, DataOverrun = 0x800D, InvalidHeader = 0x800E, Overflow = 0x8015, Error = 0x8FFF,
}
```
（状态码数值对齐 GigE Vision 规范，与 Wireshark GVCP dissector / Aravis ArvGvcpStatus 一致，勿自造。）

各命令语义与载荷（与 Aravis 0.8.x `arvgvcp.c` 线格式逐字段一致）：

| 命令 | 请求载荷 | ACK 载荷 | 说明 |
|------|----------|----------|------|
| `0x0002` Discovery | 无 | 248B 设备描述（第 3 节）= 引导寄存器区 0x0000-0x00F7 镜像 | 单播/广播一律应答 |
| `0x0004` ForceIP | 56B：`[2..7]` MAC、`[20..23]` IP、`[36..39]` 子网、`[52..55]` 网关（其余保留 0）；兼容 20B 紧凑变体（MAC@0/IP@8/子网@12/网关@16） | 无（status-only） | MAC 匹配则应用（写 identity + 0x64C/65C/66C 与 0x24/34/44）并回 Success；不匹配回 `InvalidHeader` |
| `0x0080` ReadReg | N × u32 地址 | N × u32 值 | 依地址顺序读取；首个失败则整包回该错误状态 |
| `0x0082` WriteReg | 地址/值对 × N | `index(u32)` = 成功写入个数 | 逐对写入；任一失败回该错误状态（Aravis 校验 ACK 总长 ≥ 包头+4） |
| `0x0084` ReadMem | `addr(u32) + count(u32)`（count 按 4 对齐向上取整） | `addr(u32)` 回显 + 数据 | count 上限 512（Aravis 分块拉取），再向下 4 字节对齐 |
| `0x0086` WriteMem | `addr(u32) + 数据`（紧跟地址，无保留字段） | `addr(u32)` 回显 | GenICam 端口写入走此命令 |
| `0x0040` Resend | `block_id(u32) + first(u32) + last(u32)`（低 24 位有效） | —（转 GVSP 重发） | 到达 GVCP 端口的情形；`last=0xFFFF/0xFFFFFFFF` 表示「直到结尾」，见第 4 节 |
| 未知命令 | — | ERROR（ack=cmd+1，`NotImplemented(0x8001)`） | — |

ReadReg/ReadMem 地址未定义（ReadReg 路径）→ `InvalidAddress(0x8003)`；非 4 字节对齐 → `BadAlignment(0x8005)`；
RO 地址写 → `WriteProtect(0x8004)`。

---

## 3. DiscoveryAck 载荷（248 字节，全部大端）

负载 = 引导寄存器区 0x0000-0x00F7 的标准镜像（GigE Vision 2.0 发现结构，与 Wireshark
`dissect_discovery_ack` / Aravis 逐字段一致；不单独拼包，直接由寄存器表生成，
与 ReadReg/ReadMem 读到的值天然一致）：

```
[0..3]   spec_version (u32: major<<16|minor，本设备 0x00010002 = GV 1.2)
[4..7]   device_mode (u32；bit31=1 大端设备)
[8..15]  MAC（u64 低 48 位，即 MAC 6 字节位于偏移 10..15；对应寄存器 0x0008/0x000C）
[16..19] supported_ip_configuration（bit0=Persistent bit1=DHCP bit2=LLA，本设备 0x05）
[20..23] current_ip_configuration（本设备 0x01 = Persistent 生效）
[36..39] 当前 IP        （寄存器 0x0024）
[52..55] 当前子网掩码    （寄存器 0x0034；与 IP 间隔 16 字节）
[68..71] 当前网关       （寄存器 0x0044）
[72..103]  ManufacturerName (32B ASCII)
[104..135] ModelName        (32B)
[136..167] DeviceVersion    (32B)
[168..199] ManufacturerInfo (32B)
[216..231] SerialNumber     (16B)
[232..247] UserDefinedName  (16B)
```

⚠ 此前版本曾按自造 250B 布局（MAC@2/IP@8/名称@22 起）发送，VisionMaster 等标准客户端按
本表偏移解析会得到 MAC=全 0、IP=0.0.0.0、子网读到字符串碎片——这正是「相机感叹号、
提示修改 IP、ForceIP 报 MAC 不匹配」的根因。

---

## 4. GVSP 流通道（UDP，源端口自选）

包头 8 字节（大端；**无魔数字节**，与 Wireshark GVSP dissector / Aravis arvgvsp 逐字段一致）：

```
[0..1]   = status (u16)    0x0000 = 成功（本设备恒发成功帧）
[2..3]   = block_id        帧号，从 1 自增，65535 后回 1
[4]      = content_type    Leader=0x01 / Trailer=0x02 / Data=0x03（bit7 = 扩展 ID 标志，本设备不用）
[5..7]   = packet_id (24 位) Leader=0，Data=1..N，Trailer=N+1
```

⚠ 此前版本曾按自造布局（[0]=0x37 魔数、[2..3]=format、[4..5]=packet_id、[6..7]=block_id）发送，
标准客户端无法识别。

### Leader（包头 8B + 载荷 36B）

```
[0..1]   flags = 0
[2..3]   payload_type = 1 (image)；Chunk 模式 = 0x4001（image | chunk）
[4..11]  timestamp (u64, ns，高 32 位在前；tick 频率声明 1 GHz)
[12..15] pixel_format (PFNC)
[16..19] size_x
[20..23] size_y
[24..27] offset_x = 0
[28..31] offset_y = 0
[32..33] padding_x = 0
[34..35] padding_y = 0
```

### Data（包头 8B + 像素分片）

像素按行切片。包大小约定（v2 标准语义，与 Aravis `auto_packet_size` 校验一致）：

```
0x0D04 低 16 位（GenICam GevSCPSPacketSize）= UDP 包总字节数（即 IP 包总长，4 对齐）
测试包/数据包 UDP 载荷 = 0x0D04 低 16 位 − 28（IP 20 + UDP 8）
GVSP Data 每片像素数据 = 0x0D04 低 16 位 − 36（IP+UDP 28 + GVSP 包头 8）
默认 0x0D04=1500 → Data 每片 1464B
```

⚠ 寄存器按客户端写入**原始值**存储（读回 == 写入，仅 bit31 自清零），旧版「+28 夹紧回写」已删除——
夹紧会让 Aravis 二分搜索把读回值与写入值比对失败，包大小探测直接塌缩到 576。

### Trailer（包头 8B + 载荷 8B；Chunk 模式 +16B）

```
[0..3]   payload_type（与 Leader 一致，image=1 / image|chunk=0x4001）(u32)
[4..7]   size_y
Chunk 模式（0x10040 ChunkModeActive=1）时追加 16 字节块：
[0..3]   chunk Width  (u32)
[4..7]   chunk Height (u32)
[8..11]  ChunkID = 0x00000001（与 XML <ChunkID> 一致）
[12..15] chunk 数据长度 = 8
```

XML 中 `ChunkWidth`/`ChunkHeight` 以 ChunkPort（ChunkID=1）为端口、块内偏移 0/4 读取本块。

### PFNC 像素格式

GenICam 标准特性区（XML）仅枚举 **Mono8**（`0x01080001`）；设备自定义区（0x140008，App/SDK 路径）
额外支持 BayerRG8 `0x01080009`、RGB8 `0x02180014`、BGR8 `0x02180015`。

### 重发机制与测试包

- 每帧整包序列（Leader + Data×N + Trailer）在发送前存入 `PacketCache`（环形保留**最近 4 帧**，回绕撞号整体替换）；
- 客户端对丢包重发 `ResendCmd(0x0040)`：`first..last` 区间内的包按原包头格式重发（`last=0xFFFF` 重发到帧尾），
  `packet_id` 与缓存下标一致（0=Leader，N+1=Trailer）；
- 到达 GVCP 端口的重发请求在 GvcpServer 解析后回调 `OnResendRequest`，转交 `GvspTransmitter.HandleResend`；
- **FireTestPacket（0x0D04 bit31，写 1 自清零）**：设备按**该次写入值低 16 位**向流目的地
  `(0x0D18, 0x0D00 低 16 位)` 发送一个 UDP 报文，**载荷长度精确 = 包大小 − 28**（内容不参与校验）。
  Aravis `auto_packet_size` 的二分搜索据此判定：载荷差一字节即判失败并向下二分（塌缩）；
  目的地无效时静默跳过；
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
   |--- WriteReg 0x0D18 = clientIP ----->|  流目的地 IP = 0x0D18 或控制者 IP |
   |--- WriteReg 0x0D00 = clientPort --->|  （GevSCPHostPort，0 回退控制者端口）|
   |--- WriteReg 0x0D04 = size|bit31 --->|  原值存储 + 测试包（载荷=size-28）|
   |--- WriteMem/Reg 0x00010000/04 W/H ->|  夹紧回写 + 刷新 PayloadSize   |
   |--- WriteReg 0x00010020 = 1 -------->|------------------------------>| StartAcquisition
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
  其他 IP 已占用 → ACK `AccessDenied(0x8006)`；
- **任何**来自当前控制者的 GVCP 请求都刷新心跳计时；0x0938 写入即时生效（下限 500ms）；
- 心跳超时 → 自动释放控制权并停止采集（GVSP 停发）；
- 流目的地：0x0D18 非 0 用之，否则用当前控制者 IP；端口 = **0x0D00** 低 16 位，0 回退当前控制者端口；两者都无 → 跳过发送（仅统计帧）；
- GenICam 采集语义：TriggerMode=On 时等 TriggerSoftware（0x1002C=1）出帧；AcquisitionMode=SingleFrame 出 1 帧后自动停止。

---

## 6. 实现状态清单

已实现：

- GVCP：Discovery（单播/广播恒答 248B 引导区镜像）、Force IP、ReadReg/WriteReg/ReadMem/WriteMem、Resend 转发、
  控制权仲裁（0x0A00）、心跳（0x0938）、未知命令 NotImplemented（ERROR 应答）；
- 标准引导区：版本/设备模式/MAC/IP 配置/名称区/XML URL×2/接口与通道计数/XML 清单（0x920-0x930，动态大小+CRC32）/
  GVCP 能力位图（0x7）/时间戳寄存器（1 GHz tick）；
- GVSP：Leader/Data/Trailer（经典模式单流通道）、block_id 回绕、包重发（缓存 4 帧）、
  包大小/包间延迟可调、FireTestPacket 测试包（载荷精确 size−28）、Chunk 模式（payload_type 0x4001 + 数据流末尾追加 Chunk 块）、
  软触发（TriggerMode/TriggerSoftware）与 SingleFrame 自动停；
- GenICam：GenApi XML v2 模板（嵌入资源 `GenICam/GigEForge.xml` + 身份注入，DeviceControl / ImageFormatControl /
  AcquisitionControl / AnalogControl / ChunkControl / TransportLayerControl 全特性，含
  `<Port Name="Device">` 与 `<Port Name="ChunkPort">`），XML blob 挂载于 0x00100000，URL 于 0x0200/0x0400；
- 网络：持久 IP 配置语义、Force IP（持久 IP 寄存器联动）、多网卡绑定。

未实现（与 GigE Vision 规范的已知差异）：

- 多流通道（仅 1 个流通道，0x0904 恒为 1）；
- 消息/事件通道（0x0900 = 0，无事件应答）；
- Extended ID / GVCP 扩展包头（flags 的高位扩展）；
- GenDC 传输格式（payload_type 仅 image / image|chunk）；
- PacketResend ACK（0x0041，重发为静默 GVSP 包，不发 ACK）；
- 持久 IP 切换的重启语义（写 0x64C 系列仅落表，需 Force IP 生效）；
- OffsetX/OffsetY 仅 0；ROI 平移不支持；曝光/增益寄存器仅存储不作用于图案。

---

## 7. Aravis / VisionMaster 兼容性要点

对接 Aravis 0.8.x（`arv-tool` / `arv-camera-test-0.8`）与常见 GenICam 客户端时，以下约定缺一不可：

1. **GVCP Capability 寄存器（0x934）必须存在且为 `0x00000007`**（bit0 Concatenation | bit1 WriteMemory |
   bit2 PacketResend）：Aravis 据此启用 WriteMem 与重发。**严禁把 XML URL 字符串放本地址**——
   客户端会把 ASCII 当能力位解析（WriteMem/Resend 位被污染）。
2. **GenICam XML 必须包含 `<Port Name="Device" NameSpace="Standard">` 节点**：所有寄存器节点
   通过 `<pPort>Device</pPort>` 引用它，缺失时 GenApi 构建节点表报
   `"Port not found for node"`，客户端无法打开设备。
3. **XML URL 格式**：Aravis 仅接受 `local:///<文件名>;<地址hex>;<大小hex>`（地址在前、大小在后）。
   本设备写入 `local:///GigEForge.xml;00100000;<len>`，并同时镜像到 0x0400。
4. **ReadMem 线格式**：请求 `addr(u32)+count(u32)`；应答 `addr(u32) 回显 + 数据`——数据紧跟 4 字节地址，
   没有独立的 2 字节 index 字段。
5. **0x0D04（SCPS）位布局**：包大小在低 16 位（**IP 包总长**），FireTestPacket=bit31、DoNotFragment=bit30、
   BigEndian=bit29。GenApi BigEndian 寄存器位序：`<Bit>0</Bit>` = 寄存器值 bit31、
   `<LSB>31</LSB><MSB>16</MSB>` = 低 16 位（arvgcregisternode.c：`lsb = 32 − Bit − 1`）。
   **测试包校验**：载荷必须精确 = 包大小 − 28，且发到 `(0x0D18, 0x0D00 低 16 位)` 的 socket——
   不是 GVCP 控制 socket；设备侧 0x0D00 必须可写（RW）。
6. **心跳**：Aravis 心跳线程周期性读 0x0A00（不写 0x938），每次读取都会刷新本设备的心跳计时。
7. **引导区地址不可挪动**：0x934/0x938/0x93C/0x940 与 SC0 块 0x0D00/0x0D04/0x0D08/0x0D18/0x0D1C/0x0D20
   为客户端硬编码或注入默认特性所引用的标准地址；GenICam 标准特性放 0x10000+，设备自定义放 0x140000+。
8. **SC0 块语义（勿回退旧布局）**：0x0D00 = GevSCPHostPort（**RW**，客户端收流端口——旧版误定义为
   RO Capability 会让 aravis 测试包探测全超时、包大小二分塌缩到 576）；0x0D1C = GevSCSP（RO 源端口，
   写容忍）；0x0D20 = SC 能力恒 0（读 0 = multipart 不支持，arv-test 判 SUCCESS）。任一地址回
   `InvalidAddress` 客户端立即断开（表现为「控制端已接入 → ReadReg 0x0D20 失败 → 控制端已断开」）。
9. **引导区宽容模式（RegisterMap lenientBootstrap）**：0x00000000–0x000FFFFF 内未定义地址
   ReadReg/ReadMem 回 0、WriteReg/WriteMem 静默忽略（均 ACK 成功），客户端探测标准保留寄存器
   （如 0x0A04/0x0240）不再触发断线；GenICam 设备区（≥0x00100000）仍严格报错以暴露特征地址配置错误。
10. **arv-test-0.8 判定要点**：Gain/ExposureTime/AcquisitionFrameRate 必须是 `<Float>` 节点（Integer 不被识别）；
    Width/Height 为 `<Integer>`；AcquisitionMode 枚举含 Off/Continuous/SingleFrame；TriggerSoftware 为
    `<Command>`；Chunk 五件套（ChunkModeActive/ChunkSelector/ChunkEnable/ChunkWidth/ChunkHeight）齐全才判
    SoftwareTrigger/Chunks 支持；连续出帧帧率须与设定值偏差 <10%（时间戳取自 Leader u64 纳秒）。
