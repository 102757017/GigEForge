using System.Drawing;
using System.Windows.Forms;
using GigEVision.Core.Imaging;
using GigEVision.Core.Sdk;

namespace GigEVision.App.Pages;

/// <summary>
/// SDK 服务页（契约第 9 节）：以 <see cref="GigECameraServer"/> 为核心，演示
/// 「任意语言 30 行代码喂图」的 UDP 喂图模式（GVF1 协议，默认端口 5010）。
/// 与模拟器页共用一台相机不同，本页以 SDK 方式独立起一台虚拟相机（可自定义身份）；
/// 因此启动前若工作台相机（模拟器/转换器页）在线，会先停机切换模式，避免 3956 端口冲突。
/// </summary>
public class SdkPage : UserControl
{
    private readonly NumericUpDown _feedPortUpDown;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly Label _stateLabel;
    private readonly Label _counterLabel;
    private readonly Panel _previewHost;
    private readonly PictureBox _preview;
    private readonly TextBox _codeBox;
    private readonly System.Windows.Forms.Timer _previewTimer;

    private GigECameraServer? _server;
    private FrameBuffer? _lastFrame;          // 最近解码帧引用（引用替换为原子操作）
    private FrameBuffer? _renderedFrame;      // 上次渲染的帧引用（相同则跳过重绘）
    private int _decodedCount;                // 累计解码帧数（int 读写在 .NET 原子）
    private int _renderedWidth = -1;
    private int _renderedHeight = -1;
    private bool _shutdown;

    /// <summary>只读示例代码（与 docs/SDK.md 的最小示例一致）。</summary>
    private const string SampleCode =
        """
        // C# 进程内推流（NuGet/项目引用 GigEVision.Core）
        using GigEVision.Core.Imaging;
        using GigEVision.Core.Sdk;

        var server = new GigECameraServer(new CameraOptions {
            Width = 1920, Height = 1080,
            Format = PixelFormatCode.RGB8,
            Fps = 30,
            SerialNumber = "GF-0000001",
            FeedPort = 5010,       // 0 = 关闭 UDP 喂图
        });
        server.Start();                                   // GVCP:3956 上线，等待 Discovery

        server.PushJpeg(File.ReadAllBytes("render.jpg")); // 进程内推流
        // 或任意语言经 UDP 喂图（GVF1 协议）：
        //   "GVF1"(4B) + seq(u32 BE) + len(u32 BE) + JPEG(≤60000B)
        """;

    public SdkPage()
    {
        BackColor = Theme.WindowBack;
        DoubleBuffered = true;

        // ———— 右侧：预览（Fill）+ 示例代码（Bottom，先加 Fill 后加 Bottom） ————
        _previewHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelBack,
            Padding = new Padding(12),
        };
        _preview = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
        };
        _previewHost.Controls.Add(_preview);
        _previewHost.Resize += (_, _) => UpdatePreviewBounds();

        var codeHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 190,
            BackColor = Theme.PanelBack,
            Padding = new Padding(12, 0, 12, 12),
        };
        _codeBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Theme.LogBack,
            ForeColor = Theme.TextColor,
            BorderStyle = BorderStyle.None,
            Font = Theme.LogFont,
            Text = SampleCode,
        };
        var codeCaption = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "最小 C# 示例（完整 API 见 docs/SDK.md；RoboDK Python 脚本见 samples/robodk_feed.py）",
            ForeColor = Theme.DimText,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        codeHost.Controls.Add(_codeBox);
        codeHost.Controls.Add(codeCaption);

        // ———— 左侧配置列 ————
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 340,
            BackColor = Theme.WindowBack,
            Padding = new Padding(10),
        };

        var serviceGroup = Theme.MakeGroupBox("SDK 服务（独立虚拟相机实例）");
        serviceGroup.Dock = DockStyle.Top;
        serviceGroup.Height = 118;
        var portLabel = Theme.MakeLabel("UDP 喂图端口（GVF1 协议）");
        portLabel.SetBounds(14, 28, 300, 20);
        _feedPortUpDown = Theme.MakeNumeric(1024, 65000, 5010, 10);
        _feedPortUpDown.SetBounds(14, 50, 120, 26);
        _stateLabel = Theme.MakeLabel("状态：空闲", dim: true);
        _stateLabel.SetBounds(14, 84, 300, 20);
        serviceGroup.Controls.Add(portLabel);
        serviceGroup.Controls.Add(_feedPortUpDown);
        serviceGroup.Controls.Add(_stateLabel);

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Theme.WindowBack,
            Padding = new Padding(2, 8, 2, 0),
        };
        _startButton = Theme.MakeButton("启动 SDK 服务", Theme.Violet);
        _startButton.Size = new Size(150, 30);
        _startButton.Location = new Point(2, 8);
        _startButton.Click += OnStartServer;
        _stopButton = Theme.MakeButton("停止");
        _stopButton.Size = new Size(90, 30);
        _stopButton.Location = new Point(160, 8);
        _stopButton.Enabled = false;
        _stopButton.Click += OnStopServer;
        buttonPanel.Controls.Add(_startButton);
        buttonPanel.Controls.Add(_stopButton);

        var infoGroup = Theme.MakeGroupBox("UDP 喂图协议（任何语言 30 行代码接入）");
        infoGroup.Dock = DockStyle.Fill;
        var infoText = Theme.MakeLabel(
            "数据报格式：\"GVF1\"(4B magic) + seq(u32 BE) + len(u32 BE) + JPEG 字节（≤60000B）。\n\n" +
            "服务端把收到的 JPEG 解码并缩放到相机分辨率，按帧率节拍以 GVSP 推流到网络。\n\n" +
            "RoboDK：运行 samples/robodk_feed.py 把仿真画面喂进来，" +
            "视觉软件（VM / Pylon / AravisSharp 客户端）采集到的就是仿真当前视角。", dim: true);
        infoText.SetBounds(14, 26, 296, 150);
        _counterLabel = Theme.MakeLabel("已接收 JPEG 帧：0");
        _counterLabel.SetBounds(14, 190, 296, 22);
        infoGroup.Controls.Add(infoText);
        infoGroup.Controls.Add(_counterLabel);

        leftPanel.Controls.Add(infoGroup);
        leftPanel.Controls.Add(buttonPanel);
        leftPanel.Controls.Add(serviceGroup);

        // Dock 加入顺序：先 Fill，后 Bottom/Left（后加者占据边缘）
        Controls.Add(_previewHost);
        Controls.Add(codeHost);
        Controls.Add(leftPanel);

        _previewTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();
    }

    // ———————————————————————————— 启停 ————————————————————————————

    private void OnStartServer(object? sender, EventArgs e)
    {
        if (_server is not null)
        {
            return;
        }

        try
        {
            // SDK 模式独占 GVCP 3956：先停掉工作台相机（模拟器/转换器页），避免端口冲突
            if (CameraHost.IsRunning)
            {
                CameraHost.StopCamera();
                CameraHost.Commit("SDK 服务：已停止工作台相机，切换到 SDK 服务模式");
            }

            // 以当前工作台配置为快照（相机未创建时用 CameraOptions 默认值）
            var options = new CameraOptions { FeedPort = (int)_feedPortUpDown.Value };
            if (CameraHost.IsCreated)
            {
                var cfg = CameraHost.Instance.Config;
                options.Width = cfg.Width;
                options.Height = cfg.Height;
                options.Format = cfg.Format;
                options.Fps = cfg.Fps;
                options.SerialNumber = CameraHost.Instance.Identity.SerialNumber;
                options.UserDefinedName = CameraHost.Instance.Identity.UserDefinedName;
            }

            var server = new GigECameraServer(options);
            server.Log += CameraHost.Commit;
            server.FrameDecoded += OnFrameDecoded;
            server.Start();                     // 内部异常会向上抛，统一在这里兜住

            _server = server;
            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _stateLabel.Text = $"状态：在线 · GVCP 3956 · 喂图端口 {options.FeedPort}";
            _stateLabel.ForeColor = Theme.Emerald;
            CameraHost.Commit($"SDK 服务已启动：{options.Width}x{options.Height} " +
                              $"{PixelFormatEx.PName(options.Format)} @ {options.Fps}fps，喂图端口 {options.FeedPort}");
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"SDK 服务启动失败 — {ex.Message}");
            CleanupServer();
        }
    }

    private void OnStopServer(object? sender, EventArgs e) => StopServer("SDK 服务已停止");

    private void StopServer(string message)
    {
        if (_server is null)
        {
            return;
        }

        CleanupServer();
        CameraHost.Commit(message);
    }

    /// <summary>解绑事件并释放服务（幂等）。</summary>
    private void CleanupServer()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is null)
        {
            return;
        }

        server.Log -= CameraHost.Commit;
        server.FrameDecoded -= OnFrameDecoded;
        try
        {
            server.Stop();
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"SDK 服务停止异常 — {ex.Message}");
        }

        server.Dispose();
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _stateLabel.Text = "状态：空闲";
        _stateLabel.ForeColor = Theme.DimText;
    }

    private void OnFrameDecoded(FrameBuffer frame)
    {
        Interlocked.Increment(ref _decodedCount);
        _lastFrame = frame;                 // 供预览定时器取用（引用替换为原子操作）
    }

    // ———————————————————————————— 预览与收尾 ————————————————————————————

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        _counterLabel.Text = $"已接收 JPEG 帧：{_decodedCount}";

        var frame = _lastFrame;
        if (frame is null || ReferenceEquals(frame, _renderedFrame))
        {
            return;
        }

        try
        {
            var old = _preview.Image;
            _preview.Image = ImageLoader.ToPreviewBitmap(frame);
            old?.Dispose();
            _renderedFrame = frame;
            UpdatePreviewBounds();
        }
        catch (Exception ex)
        {
            // 预览失败不影响推流（例如帧格式暂不支持）
            CameraHost.Commit($"SDK 预览刷新失败 — {ex.Message}");
        }
    }

    /// <summary>按帧宽高等比缩放预览并居中（Zoom 语义下手动定位，参考模拟器页）。</summary>
    private void UpdatePreviewBounds()
    {
        var frame = _lastFrame;
        int availWidth = _previewHost.ClientSize.Width - _previewHost.Padding.Left - _previewHost.Padding.Right;
        int availHeight = _previewHost.ClientSize.Height - _previewHost.Padding.Top - _previewHost.Padding.Bottom;
        if (availWidth <= 0 || availHeight <= 0)
        {
            return;
        }

        int frameWidth = frame?.Width ?? _renderedWidth;
        int frameHeight = frame?.Height ?? _renderedHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            _preview.Size = new Size(0, 0);
            return;
        }

        if (frameWidth == _renderedWidth && frameHeight == _renderedHeight)
        {
            return;                         // 分辨率未变化，无需重排
        }

        double scale = Math.Min((double)availWidth / frameWidth, (double)availHeight / frameHeight);
        int w = Math.Max(1, (int)(frameWidth * scale));
        int h = Math.Max(1, (int)(frameHeight * scale));
        _preview.Size = new Size(w, h);
        _preview.Location = new Point(
            _previewHost.Padding.Left + (availWidth - w) / 2,
            _previewHost.Padding.Top + (availHeight - h) / 2);

        _renderedWidth = frameWidth;
        _renderedHeight = frameHeight;
    }

    /// <summary>停预览定时器并释放 SDK 服务（幂等；MainForm 关闭时调用，Dispose 兜底）。</summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        CleanupServer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
        }

        base.Dispose(disposing);
    }
}
