# RoboDK 喂图方案（RoboDK 仿真 → GigE Vision 相机）

把 RoboDK 站点的 3D 仿真画面实时喂给 GigEForge 虚拟相机，让 VisionMaster / pylon 等视觉软件
「看到」机器人仿真视角——用于视觉方案离线虚拟调试（标定、定位、手眼流程验证）。

## 1. 拓扑

```
+----------------------+     Cam2D_Snapshot      +--------------------------+
|      RoboDK 站点      |------------------------>|  robodk_feed.py          |
|  （机器人仿真 / 相机   |     PNG 截图            |  PIL 打开 → 缩放 ≤1600px |
|    视角 Cam2D）       |                         |  → JPEG 压缩 (quality)   |
+----------------------+                         +--------------------------+
                                                              |
                                              UDP GVF1（单报 ≤ 60000B）
                                              "GVF1"+seq(u32 BE)+len(u32 BE)+JPEG
                                                              |
                                                              v
+--------------------------------------------------------------+-----------+
| GigEForge（二选一）                                            |           |
|   A. samples/RoboDKFeedServer  （控制台，FeedPort=5010）       |           |
|   B. GigEVision.App「SDK 服务」页（FeedPort 可调，默认 5010）   |           |
|                                                                |           |
|  JPEG 解码 → 缩放到相机宽高 → 转目标像素格式 → 采集线程按 Fps 发流 |           |
+------------------------------------------------+---------------+-----------+
                                                   |
                                    GVSP（UDP，Leader/Data/Trailer + 重发）
                                                   |
                                                   v
                                 +---------------------------------+
                                 |  视觉软件采集                    |
                                 |  VisionMaster (VM) / pylon /    |
                                 |  Harvesters / AravisSharp       |
                                 +---------------------------------+
```

协议要点：`robodk_feed.py` 把每帧截图压成 JPEG，按 **GVF1** 协议发到 GigEForge 的 UDP 喂图端口
（默认 **5010**）；GigEForge 解码后转成相机配置的宽高/像素格式，以标准 GigE Vision 流对外提供。

## 2. 分步操作

1. **安装依赖**
   - RoboDK（桌面版，加载你的站点 .rdk 文件）；
   - Python 依赖：`pip install robodk pillow`。

2. **启动喂图服务**（二选一）
   - 控制台：`dotnet run --project samples/RoboDKFeedServer`（默认 1280x720 Mono8 30fps，喂图端口 5010）；
   - 图形界面：运行 `GigEVision.App` → 「SDK 服务」页 → 设喂图端口 → `启动 SDK 服务`。

3. **运行喂图脚本**
   ```bash
   python samples/robodk_feed.py --host 127.0.0.1 --port 5010 --fps 30 --quality 85
   ```
   - `--host`：GigEForge 所在主机（同机用 127.0.0.1；跨机填服务端 IP）；
   - `--screen`：不连 RoboDK，改抓本机屏幕（降级模式，也可先用来快速验证链路）。

4. **客户端采集验证**
   - 推荐：`dotnet run --project samples/AravisSharpClient`（列表模式确认发现，`-- <DeviceId>` 采一帧存 `frame.pgm`）；
   - 或 VisionMaster / pylon Viewer：搜索 `GigEForgeCam` → 连接 → 实时取流；
   - RoboDKFeedServer 控制台每秒打印 `已喂帧 N (xx fps)`，接近 `--fps` 即链路正常。

## 3. 常见问题

| 现象 | 原因与处理 |
|------|-----------|
| 脚本提示「RoboDK 连接失败…降级为屏幕抓图模式」 | RoboDK 未启动或 API 端口被占用。启动 RoboDK 后重跑；或确认确要降级（屏幕抓图同样可用，仅画面内容不同）。可加 `--screen` 显式进入降级模式 |
| 日志出现「GVF1 载荷不合法（len=…，上限 60000）」 | 截图分辨率太高导致 JPEG 超 60000B。脚本内置「降 quality 重压一次」，仍超限则丢帧；可调低 `--quality`（如 70）或缩小 RoboDK 3D 视口/降低屏幕分辨率 |
| 每秒打印 fps 低于设定值 | 发送端编码耗时超过 1/fps（大屏抓图常见），或 UDP 丢包后服务端按最新帧覆盖。降低 `--fps`/`--quality`，或缩小画面 |
| 客户端搜不到相机 | 防火墙未放行：入站 UDP 3956（GVCP）与 5010（喂图）+ GigEForge 进程的 GVSP 流端口，建议仅在**专用网络**放行；跨网段需关闭 AP 隔离（客户端隔离）；`--host` 必须指向服务端实际网卡 IP |
| 画面撕裂/花屏 | 客户端与服务端像素格式不一致（如客户端请求 BayerRG8 但喂图是彩色）。服务端启动时固定 `CameraOptions.Format`（如 RGB8），客户端设置 `PixelFormat=RGB8` |
| 喂图正常但客户端黑屏 | 客户端未发送 `AcquisitionStart`（GigEForge 按需发流）；或 GigEForge 停止采集（心跳超时后自动停流）——重新连接客户端即可 |
| 跨机部署丢包严重 | 千兆网开巨型帧（客户端写 `GevSCPSPacketSize=9000`，交换机同步开启）；或降低帧率/分辨率 |
