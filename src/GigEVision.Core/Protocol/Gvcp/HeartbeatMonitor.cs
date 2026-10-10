namespace GigEVision.Core.Protocol.Gvcp;

/// <summary>
/// GVCP 控制心跳监视器：控制端必须周期性发送 GVCP 请求（或显式调用 <see cref="Touch"/>）保活。
/// 内部 System.Threading.Timer 每 250ms 检查一次；超时触发 <see cref="Expired"/> 一次后解除武装，
/// 需重新 <see cref="Touch"/>（或 <see cref="Start"/>）才会再次武装。
/// </summary>
public sealed class HeartbeatMonitor : IDisposable
{
    /// <summary>检查间隔（毫秒）。</summary>
    private const int CheckIntervalMs = 250;

    private readonly object _lock = new();
    private readonly Timer _timer;                 // System.Threading.Timer
    private TimeSpan _timeout;
    private DateTime _lastTouchUtc;
    private bool _running;
    private bool _armed;
    private bool _invoking;                        // 防止 Expired 处理器重入
    private bool _disposed;

    /// <summary>心跳超时事件（单次触发后需重新 Touch 才会再次触发）。</summary>
    public event Action? Expired;

    /// <summary>创建监视器（不启动计时，Start 后生效）。</summary>
    public HeartbeatMonitor(TimeSpan initialTimeout)
    {
        _timeout = initialTimeout < TimeSpan.Zero ? TimeSpan.Zero : initialTimeout;
        _timer = new Timer(OnTick, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>心跳超时时间。</summary>
    public TimeSpan Timeout
    {
        get
        {
            lock (_lock)
            {
                return _timeout;
            }
        }

        set
        {
            lock (_lock)
            {
                _timeout = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            }
        }
    }

    /// <summary>刷新心跳时间戳并（重新）武装监视器。</summary>
    public void Touch()
    {
        lock (_lock)
        {
            _lastTouchUtc = DateTime.UtcNow;
            _armed = true;
        }
    }

    /// <summary>启动监视：刷新心跳并开启 250ms 周期检查。</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastTouchUtc = DateTime.UtcNow;
            _armed = true;
            _running = true;
        }

        _timer.Change(TimeSpan.FromMilliseconds(CheckIntervalMs), TimeSpan.FromMilliseconds(CheckIntervalMs));
    }

    /// <summary>停止监视：暂停周期检查并解除武装（下次 Start 前不再触发 Expired）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _armed = false;
        }

        _timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnTick(object? state)
    {
        bool fire;
        lock (_lock)
        {
            fire = _running && _armed && !_invoking && DateTime.UtcNow - _lastTouchUtc >= _timeout;
            if (fire)
            {
                _armed = false;   // 触发一次后解除武装，需重新 Touch 才再武装
            }
        }

        if (!fire)
        {
            return;
        }

        _invoking = true;
        try
        {
            Expired?.Invoke();
        }
        finally
        {
            _invoking = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _running = false;
            _armed = false;
        }

        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
