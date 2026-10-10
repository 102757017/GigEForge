#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""robodk_feed.py — 把 RoboDK 站点画面实时喂给 GigEForge 虚拟相机。

用法：
    pip install robodk pillow
    # 1) 先在另一个终端启动 GigEForge 喂图服务（二选一）：
    #    dotnet run --project samples/RoboDKFeedServer        # 或在 GigEVision.App 的「SDK 服务」页启动
    # 2) 再运行本脚本（参数须与服务端 CameraOptions.FeedPort 一致）：
    python robodk_feed.py --host 127.0.0.1 --port 5010 --fps 30 --quality 85
    # 降级：未安装 robodk、Robolink() 连接失败或显式加 --screen 时，
    # 改用 PIL.ImageGrab.grab() 抓取本机屏幕画面（Windows 可直接运行）。

GVF1 UDP 喂图协议（单条数据报，全部大端）：
    "GVF1"(4B ASCII) + seq(u32) + jpegLen(u32) + JPEG 字节
    jpegLen 必须 <= 60000 字节；超限时自动降低 quality 重压一次，仍超限则丢弃该帧并计数。
"""
import argparse
import io
import os
import socket
import tempfile
import time

from PIL import Image, ImageGrab

# —— RoboDK 导入失败不影响降级路径（--screen 纯屏幕抓图无需 robodk）——
try:
    from robodk.robolink import Robolink
    HAS_ROBODK = True
except ImportError:
    HAS_ROBODK = False

MAX_JPEG_BYTES = 60000                      # GVF1 协议对 JPEG 载荷的上限
SNAPSHOT_PATH = os.path.join(tempfile.gettempdir(), "robodk_feed_frame.png")


def parse_args():
    ap = argparse.ArgumentParser(
        description="把 RoboDK 仿真视角（或本机屏幕）以 GVF1 协议喂给 GigEForge 虚拟相机")
    ap.add_argument("--host", default="127.0.0.1",
                    help="GigEForge 喂图服务地址（默认 127.0.0.1）")
    ap.add_argument("--port", type=int, default=5010,
                    help="喂图端口（默认 5010，须与服务端 CameraOptions.FeedPort 一致）")
    ap.add_argument("--fps", type=float, default=30.0, help="发送帧率（默认 30）")
    ap.add_argument("--quality", type=int, default=85, help="JPEG 质量 1-95（默认 85）")
    ap.add_argument("--screen", action="store_true",
                    help="强制屏幕抓图降级模式（不连接 RoboDK）")
    return ap.parse_args()


def grab_frame(rdk, use_screen):
    """取一帧 RGB 图像：优先 RoboDK 仿真视角，降级/无 RoboDK 时抓本机屏幕。"""
    if rdk is not None and not use_screen:
        rdk.Cam2D_Snapshot(SNAPSHOT_PATH)               # 抓取当前 3D 仿真视角
        return Image.open(SNAPSHOT_PATH).convert("RGB")
    return ImageGrab.grab().convert("RGB")              # 降级：整屏截图


def encode_jpeg(img, quality):
    """缩放并压 JPEG；超 60000B 自动降 quality 重压一次，仍超限返回 None（丢帧）。"""
    img.thumbnail((1600, 1600))                         # 控制分辨率上限，兼顾压缩率
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=quality)
    if buf.tell() <= MAX_JPEG_BYTES:
        return buf.getvalue()
    buf = io.BytesIO()                                  # 重压一次：质量减半
    img.save(buf, "JPEG", quality=max(10, quality // 2))
    return buf.getvalue() if buf.tell() <= MAX_JPEG_BYTES else None


def main():
    args = parse_args()
    use_screen = args.screen or not HAS_ROBODK
    rdk = None
    if not use_screen:
        try:
            rdk = Robolink()                            # 连接 RoboDK 站点（本机或远程）
        except Exception as exc:                        # RoboDK 未启动/连接被拒 → 降级
            print(f"[robodk_feed] RoboDK 连接失败（{exc}），降级为屏幕抓图模式")
            use_screen = True

    mode = "屏幕抓图（降级）" if use_screen else "RoboDK 仿真视角"
    print(f"[robodk_feed] 图像源: {mode}  ->  udp://{args.host}:{args.port}"
          f"  @ {args.fps:g} fps  q={args.quality}")
    print("[robodk_feed] Ctrl+C 退出")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)   # UDP：丢帧可容忍，不重传
    seq, sent, dropped = 0, 0, 0
    interval = 1.0 / max(args.fps, 0.1)
    try:
        while True:
            t0 = time.time()
            jpg = encode_jpeg(grab_frame(rdk, use_screen), args.quality)
            if jpg is None:
                dropped += 1                          # 重压后仍超 60000B：丢弃该帧
            else:
                sock.sendto(b"GVF1" + seq.to_bytes(4, "big")
                            + len(jpg).to_bytes(4, "big") + jpg,
                            (args.host, args.port))
                seq += 1
                sent += 1
            delay = interval - (time.time() - t0)     # 按目标帧率节拍（编码耗时已计入）
            if delay > 0:
                time.sleep(delay)
    except KeyboardInterrupt:
        pass
    finally:
        sock.close()
        print(f"[robodk_feed] 已停止：发送 {sent} 帧（seq={seq}），丢弃 {dropped} 帧")


if __name__ == "__main__":
    main()
