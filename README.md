# GigEForge — GigE Vision 虚拟相机工作台

GigEForge 是一套运行在 Windows 上的 **GigE Vision 虚拟相机套件**：让一台普通 PC 在局域网中表现为标准的
GigE Vision 相机（GVCP 控制 + GVSP 流 + GenICam XML），供 pylon / VisionMaster / Harvesters / Aravis 等
现有视觉软件直接发现与采集。协议基线为 **GigE Vision 1.2（GVCP/GVSP 经典模式，非 GenDC）+ GenICam
GenApi XML（local 模式）**，仅用于工业视觉测试与虚拟调试。

## 功能矩阵

| 功能 | 形态 | 能力 |
|------|------|------|
| **模拟器** | `GigEVision.App` WinForms | 播放 BMP/JPG/TIF 静态图或文件夹轮播；内置彩条/渐变/网格/动态十字测试图案；帧率 1–120 fps、最大 8192×8192；GenICam XML 自动生成，Pylon/Harvesters/VisionMaster 即插即用；支持 Force IP 与心跳保活 |
| **转换器** | `GigEVision.App` WinForms | USB 摄像头经 OpenCV 桥接为 GigE Vision 流；局域网多节点同时观察；CameraLink/模拟相机通过 `IFrameSource` 扩展接入；将非 GigE 设备统一进千兆网采集体系 |
| **Server SDK** | `GigEVision.Core` 类库 | 进程内 `PushFrame`/`PushJpeg` 一行推流；UDP 喂图端口，任何语言 30 行代码接入；RoboDK 仿真画面实时喂给虚拟相机；视觉软件按需采集，实现虚拟调试闭环 |

## 环境要求

- Windows 10/11 x64
- .NET 10 SDK

## 构建与运行

```bash
# 构建全部（Core + App + 样例）
dotnet build GigEForge.sln -c Release

# 启动 WinForms 工作台（模拟器 / 转换器 / SDK 服务三个页签）
dotnet run --project src/GigEVision.App

# 或运行最小 SDK 示例（纯控制台：内置彩条以 30 fps 流出）
dotnet run --project samples/MinimalPush
```

## Windows 防火墙提示

首次运行请放行（建议仅在**专用网络**启用）：

- **UDP 3956**：GVCP 控制通道（Discovery / 寄存器读写 / 心跳），客户端必须可达；
- **UDP 5010**：SDK UDP 喂图端口（GVF1 协议，RoboDK 喂图用）；
- **GVSP 流端口**：流通道源端口由系统随机分配（`0x0D00` 写入主机监听端口后作为目的地端口），
  建议直接放行 `GigEForge.exe` / `dotnet.exe` 的入站 UDP，避免逐端口放行。

跨网段/多网卡环境请关闭 AP 隔离（客户端隔离），必要时用 Force IP 把虚拟相机配置到与客户端同网段。

## 客户端验证矩阵

| 客户端 | 说明 | 一行用法 |
|--------|------|----------|
| **AravisSharpClient 样例**（推荐） | Windows 零安装，与本仓库同源码分发 | `dotnet run --project samples/AravisSharpClient`（列表模式；加 `-- <DeviceId>` 采集一帧存 `frame.pgm`） |
| Basler pylon Viewer | 商业客户端，GenICam 兼容性最严格 | 启动 GigEForge → pylon Viewer → Device Discovery 选中 `GigEForgeCam` → Continuous Shot |
| VisionMaster (VM) | 国产视觉平台，验证工程化对接 | 相机源选 GigE Vision → 搜索 `GigEForge` → 连接后实时取流 |
| arv-tool（Linux/MSYS2） | Aravis 命令行，快速核对寄存器 | `arv-tool-0.8 features --name GigEForgeCam`；`arv-tool-0.8 control Width=1920 --name GigEForgeCam` |

Python 侧快速验证（Harvesters）：

```python
from genicam.genapi import NodeMap
from harvesters.core import Harvester

h = Harvester()
h.add_file("C:/Program Files/GenICam/library/GCLib/GigEVisionTLS.cti")  # 你的 GVCP GenTL producer
h.update()
print([i.id_ for i in h.device_info_list])   # 应包含 GigEForge / GF-0000001
```

## 目录结构

```
GigEForge.sln                     # 解决方案：Core + App + 2 个内置样例
README.md                         # 本文件
docs/PROTOCOL.md                  # GVCP/GVSP 协议实现说明（寄存器总表、包布局、时序）
docs/SDK.md                       # GigECameraServer SDK 文档（API、示例、扩展指南）
docs/ROBODK.md                    # RoboDK 喂图方案（拓扑、分步操作、FAQ）
src/GigEVision.Core/              # 设备端协议栈 + 图像管线 + SDK（类库）
  Protocol/Gvcp/                  #   GVCP：发现/读写寄存器/心跳/控制权
  Protocol/Gvsp/                  #   GVSP：Leader/Data/Trailer、包重发缓存、统计
  GenICam/                        #   GenApi XML（local 模式）生成
  Imaging/                        #   测试图案、图片加载、像素转换、帧源抽象
  Camera/                         #   VirtualCamera 门面 + 相机身份
  Sdk/                            #   GigECameraServer（一行上线虚拟相机）
src/GigEVision.App/               # WinForms 工作台（模拟器 / 转换器 / SDK 服务）
samples/MinimalPush/              # 最小 SDK 示例：内置图案推流（FeedPort=0）
samples/RoboDKFeedServer/         # UDP 喂图服务示例（FeedPort=5010）+ 每秒帧率统计
samples/robodk_feed.py            # RoboDK → GVF1 喂图脚本（可降级为屏幕抓图）
samples/AravisSharpClient/        # AravisSharp 第三方客户端验证样例（独立 csproj，不进 sln）
```


## 许可

MIT License。本项目仅用于工业视觉测试与虚拟调试用途。
