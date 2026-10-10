using System;
using System.Collections.Generic;

namespace GigEVision.Core.Imaging;

/// <summary>
/// 帧源抽象：采集线程按节拍调用 AcquireFrame 拉取一帧。
/// </summary>
public interface IFrameSource
{
    /// <summary>获取一帧；无可用帧返回 null。tickMs 为调用方的单调毫秒时钟（Environment.TickCount64）。</summary>
    FrameBuffer? AcquireFrame(long tickMs);
}

/// <summary>
/// 最新帧缓冲源：外部线程 Push 原子替换最新帧并触发 FramePushed；
/// 采集线程 AcquireFrame 直接取最新（无则 null）。转换器 / SDK 推流共用此实现。
/// </summary>
public sealed class LatestFrameSource : IFrameSource
{
    private volatile FrameBuffer? _latest; // 引用替换为原子操作，读侧免锁

    /// <summary>有新帧被推入时触发（在 Push 调用方线程上）。</summary>
    public event Action? FramePushed;

    /// <summary>线程安全地替换最新帧并通知订阅者。</summary>
    public void Push(FrameBuffer frame)
    {
        _latest = frame ?? throw new ArgumentNullException(nameof(frame));
        FramePushed?.Invoke();
    }

    /// <summary>返回最新一帧；尚无帧时返回 null。</summary>
    public FrameBuffer? AcquireFrame(long tickMs) => _latest;
}

/// <summary>内置测试图案源：持有宽/高/格式/图案类型，每帧调用 TestPatternGenerator 生成。</summary>
public sealed class TestPatternSource : IFrameSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly PixelFormatCode _format;
    private readonly PatternType _pattern;

    public TestPatternSource(int w, int h, PixelFormatCode f, PatternType p)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "宽高必须为正");
        if (!PixelFormatEx.IsSupported(f)) throw new NotSupportedException($"不支持的像素格式: 0x{(uint)f:X8}");
        _width = w;
        _height = h;
        _format = f;
        _pattern = p;
    }

    public FrameBuffer? AcquireFrame(long tickMs) =>
        TestPatternGenerator.Generate(_width, _height, _format, _pattern, tickMs);
}

/// <summary>
/// 图片文件源：单文件 = 静态图；多文件 = 每 5000ms 轮换索引。
/// 文件懒加载（首次取帧时才读盘），加载失败的文件记入失败表并跳过，
/// 全部失败时返回 null（不抛异常，保持采集节拍稳定）。
/// </summary>
public sealed class ImageFileSource : IFrameSource
{
    private const long RotateIntervalMs = 5000; // 多文件轮播间隔
    private const int CacheLimit = 32;          // 已解码帧缓存上限（超出整体清空，简单防膨胀）

    private readonly IReadOnlyList<string> _paths;
    private readonly PixelFormatCode _target;
    private readonly int _width;
    private readonly int _height;
    private readonly object _gate = new();
    private readonly Dictionary<string, FrameBuffer> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private int _index;
    private long _rotateAt;
    private bool _started;

    public ImageFileSource(IReadOnlyList<string> paths, PixelFormatCode target, int w, int h)
    {
        if (paths is null || paths.Count == 0) throw new ArgumentException("至少需要一个图片路径", nameof(paths));
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "宽高必须为正");
        if (!PixelFormatEx.IsSupported(target)) throw new NotSupportedException($"不支持的像素格式: 0x{(uint)target:X8}");
        _paths = new List<string>(paths); // 防御性拷贝
        _target = target;
        _width = w;
        _height = h;
    }

    public FrameBuffer? AcquireFrame(long tickMs)
    {
        lock (_gate)
        {
            int count = _paths.Count;
            if (count == 0) return null;

            if (!_started)
            {
                _started = true; // 首帧立即返回第 0 张
                _rotateAt = tickMs + RotateIntervalMs;
            }
            else if (count > 1 && tickMs >= _rotateAt)
            {
                _rotateAt = tickMs + RotateIntervalMs; // 每 5s 轮换到下一索引
                _index = (_index + 1) % count;
            }

            // 最多尝试 count 次（全部文件加载失败时避免死循环）
            for (int attempt = 0; attempt < count; attempt++)
            {
                string path = _paths[_index];
                if (_failed.Contains(path))
                {
                    _index = (_index + 1) % count; // 跳过已知失败文件
                    continue;
                }
                if (_cache.TryGetValue(path, out var cached)) return cached;

                try
                {
                    var fb = ImageLoader.Load(path, _target, _width, _height); // 懒加载 + 缩放 + 格式转换
                    if (_cache.Count >= CacheLimit) _cache.Clear();
                    _cache[path] = fb;
                    return fb;
                }
                catch
                {
                    _failed.Add(path); // 加载失败：跳过（无日志通道，靠返回 null + 上层兜底）
                    _index = (_index + 1) % count;
                }
            }
            return null; // 所有文件都不可用
        }
    }
}
