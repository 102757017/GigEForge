using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GigEVision.Core.Imaging;
using OpenCvSharp;

namespace GigEVision.App.Pages;

/// <summary>
/// 转换器页（契约第 9 节）：USB 摄像头经 OpenCV 桥接为 GigE Vision 虚拟相机。
/// 管线：VideoCapture.Read → Cv2.CvtColor(BGR2RGB) → Cv2.Resize → FrameBuffer(RGB8→目标格式)
/// → VirtualCamera.PushExternalFrame（内部 LatestFrameSource 存最新帧，采集节拍由相机线程驱动）。
/// CameraLink / 模拟相机等无法直接读取的设备，实现 IFrameSource 即可接入（见 docs/SDK.md）。
/// </summary>
public class ConverterPage : UserControl
{
    /// <summary>分辨率下拉框选项（显示文本 → 宽高）。</summary>
    private static readonly (string Display, int Width, int Height)[] Resolutions =
    [
        ("640x480", 640, 480),
        ("1280x720", 1280, 720),
        ("1920x1080", 1920, 1080),
    ];

    private readonly NumericUpDown _deviceUpDown;
    private readonly ComboBox _resolutionCombo;
    private readonly ComboBox _formatCombo;
    private readonly Button _openButton;
    private readonly Button _stopButton;
    private readonly Label _stateLabel;
    private readonly Panel _previewHost;
    private readonly PictureBox _preview;
    private readonly System.Windows.Forms.Timer _previewTimer;

    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private volatile bool _capturing;
    private FrameBuffer? _lastFrame;
    private int _renderedWidth = -1;
    private int _renderedHeight = -1;
    private bool _shutdown;

    public ConverterPage()
    {
        BackColor = Theme.WindowBack;
        DoubleBuffered = true;

        // —— 预览区（先加入：Dock=Fill 最后布局）——
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

        // —— 说明 ——
        var infoLabel = new Label
        {
            Text = "USB 摄像头经 OpenCV 桥接为 GigE Vision 虚拟相机；CameraLink / 模拟相机请实现 IFrameSource 接口接入（见 docs/SDK.md）。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 44,
            ForeColor = Theme.DimText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 6, 12, 0),
        };
        var infoPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Theme.WindowBack,
            Padding = new Padding(6, 0, 6, 0),
        };
        infoPanel.Controls.Add(infoLabel);

        // —— 配置行 ——
        _deviceUpDown = Theme.MakeNumeric(0, 9, 0);
        _resolutionCombo = Theme.MakeComboBox();
        _resolutionCombo.Items.AddRange(Resolutions.Select(r => r.Display).ToArray());
        _resolutionCombo.SelectedIndex = 1;
        _formatCombo = Theme.MakeComboBox();
        foreach (PixelFormatCode fmt in PixelFormatEx.Supported)
        {
            _formatCombo.Items.Add(PixelFormatEx.PName(fmt));
        }

        _formatCombo.SelectedIndex = 2;     // 默认 RGB8（OpenCV 输出即 RGB，零转换）

        _openButton = Theme.MakeButton("打开 USB 相机", Theme.Amber);
        _openButton.Click += OnOpenCamera;

        _stopButton = Theme.MakeButton("停止采集");
        _stopButton.Click += OnStopCamera;

        _stateLabel = new Label
        {
            Text = "未采集",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 30,
            ForeColor = Theme.DimText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 4, 12, 0),
        };

        var configPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6,
            RowCount = 2,
            BackColor = Theme.WindowBack,
            Padding = new Padding(6),
        };
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 标签
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 索引
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 标签
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 分辨率
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                       // 标签
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));                  // 格式（拉伸）

        configPanel.Controls.Add(Theme.MakeLabel("设备索引"), 0, 0);
        configPanel.Controls.Add(_deviceUpDown, 1, 0);
        configPanel.Controls.Add(Theme.MakeLabel("分辨率"), 2, 0);
        configPanel.Controls.Add(_resolutionCombo, 3, 0);
        configPanel.Controls.Add(Theme.MakeLabel("目标像素格式"), 4, 0);
        configPanel.Controls.Add(_formatCombo, 5, 0);
        _formatCombo.Dock = DockStyle.Fill;

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.WindowBack,
            Padding = new Padding(6, 0, 6, 0),
        };
        buttonPanel.Controls.Add(_openButton);
        buttonPanel.Controls.Add(_stopButton);

        var bottomPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.WindowBack };
        bottomPanel.Controls.Add(_stateLabel);
        bottomPanel.Controls.Add(buttonPanel);
        buttonPanel.SendToBack();

        Controls.Add(_previewHost);     // Fill：最后布局
        Controls.Add(infoPanel);        // Top：说明
        Controls.Add(configPanel);      // Top：参数行
        Controls.Add(bottomPanel);      // Top：按钮 + 状态（位于最上）

        SetButtons(running: false);

        _previewTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();
    }

    // ———————————————————————————— 参数 ————————————————————————————

    private (int Width, int Height) SelectedResolution()
    {
        int index = Math.Clamp(_resolutionCombo.SelectedIndex, 0, Resolutions.Length - 1);
        return (Resolutions[index].Width, Resolutions[index].Height);
    }

    private PixelFormatCode SelectedFormat() =>
        PixelFormatEx.Supported[Math.Clamp(_formatCombo.SelectedIndex, 0, PixelFormatEx.Supported.Count - 1)];

    // ———————————————————————————— 启停 ————————————————————————————

    private void OnOpenCamera(object? sender, EventArgs e)
    {
        if (_capturing)
        {
            return;
        }

        int deviceIndex = (int)_deviceUpDown.Value;
        (int width, int height) = SelectedResolution();
        var format = SelectedFormat();

        try
        {
            var camera = CameraHost.Instance;
            CameraHost.EnsureStarted();                     // 先保证相机上线（GVCP 应答 Discovery）

            // 参数经寄存器路径写入：Config / PayloadSize / GenICam 三者保持一致
            CameraHost.ApplyCaptureSettings(width, height, format);
            camera.StopAcquisition();                       // 转换器接管采集
            camera.Source = null;                           // 首帧 PushExternalFrame 时 LatestFrameSource 接管帧源
            camera.StartAcquisition();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _capturing = true;
            _captureThread = new Thread(() => CaptureLoop(deviceIndex, width, height, format, token))
            {
                IsBackground = true,
                Name = "GigEForge-UsbCapture",
            };
            _captureThread.Start();

            SetButtons(running: true);
            _stateLabel.Text = $"采集中：USB 相机 #{deviceIndex} → {width}x{height} {PixelFormatEx.PName(format)}";
            CameraHost.Commit($"转换器：打开 USB 相机 #{deviceIndex} → {width}x{height} {PixelFormatEx.PName(format)}");
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"转换器：打开失败 — {ex.Message}");
            MessageBox.Show($"打开 USB 相机失败：{ex.Message}", "GigEForge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetButtons(running: false);
        }
    }

    private void OnStopCamera(object? sender, EventArgs e)
    {
        StopCapture(stopAcquisition: true);
        _stateLabel.Text = "未采集";
    }

    /// <summary>停采集线程（CTS.Cancel → Join → 线程内 using 释放 VideoCapture），可选停相机采集线程。</summary>
    private void StopCapture(bool stopAcquisition)
    {
        if (_capturing)
        {
            _capturing = false;
            _cts?.Cancel();
            _captureThread?.Join(1500);
            _cts?.Dispose();
            _cts = null;
            _captureThread = null;
            CameraHost.Commit("转换器：USB 采集已停止");
        }

        if (stopAcquisition && CameraHost.IsCreated)
        {
            CameraHost.Instance.StopAcquisition();
        }

        SetButtons(running: false);
    }

    // ———————————————————————————— OpenCV 采集线程 ————————————————————————————

    /// <summary>
    /// USB 抓帧循环：Read → BGR2RGB →（可选）Resize → 字节拷贝 → FrameBuffer → PushExternalFrame。
    /// 帧进 LatestFrameSource 最新帧缓冲；是否按帧率发出由相机采集线程决定（此处无需对齐节拍）。
    /// </summary>
    private void CaptureLoop(int deviceIndex, int width, int height, PixelFormatCode format, CancellationToken token)
    {
        try
        {
            using var capture = new VideoCapture(deviceIndex);
            if (!capture.IsOpened())
            {
                CameraHost.Commit($"转换器：USB 相机 #{deviceIndex} 打开失败（设备不存在或被占用）");
                OnCaptureFailed("USB 相机打开失败", $"设备索引 #{deviceIndex} 不存在或已被其他程序占用。");
                return;
            }

            double sourceWidth = capture.Get(VideoCaptureProperties.FrameWidth);
            double sourceHeight = capture.Get(VideoCaptureProperties.FrameHeight);
            bool needResize = width != (int)sourceWidth || height != (int)sourceHeight;
            var targetSize = new OpenCvSharp.Size(width, height);
            CameraHost.Commit(
                $"转换器：USB 相机 #{deviceIndex} 已打开（原始 {(int)sourceWidth}x{(int)sourceHeight}），" +
                $"输出 {width}x{height} {PixelFormatEx.PName(format)}");

            while (!token.IsCancellationRequested)
            {
                using var frame = new Mat();
                if (!capture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(20);       // 部分驱动在无新帧时返回 false，稍后重试
                    continue;
                }

                using var rgb = new Mat();
                Cv2.CvtColor(frame, rgb, ColorConversionCodes.BGR2RGB);

                Mat view = rgb;
                using Mat? resized = needResize ? new Mat() : null;
                if (resized is not null)
                {
                    Cv2.Resize(rgb, resized, targetSize, 0, 0, InterpolationFlags.Linear);
                    view = resized;
                }

                int byteCount = (int)(view.Total() * view.ElemSize());
                var data = new byte[byteCount];
                Marshal.Copy(view.Data, data, 0, byteCount);    // CvtColor/Resize 输出连续，可整块拷贝

                // 目标格式非 RGB8 时经 Core PixelConverter 转换（Mono8 亮度 / BayerRG8 采样 / BGR8 交换）
                byte[] payload = format == PixelFormatCode.RGB8
                    ? data
                    : PixelConverter.Convert(data, PixelFormatCode.RGB8, format, width, height);

                CameraHost.Instance.PushExternalFrame(new FrameBuffer
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    Data = payload,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"转换器：USB 采集线程异常退出 — {ex.Message}");
            OnCaptureFailed("USB 采集异常", ex.Message);
        }
        finally
        {
            OnCaptureFinished();
        }
    }

    /// <summary>线程异常退出时：停相机采集并复位按钮（跨线程 → BeginInvoke）。</summary>
    private void OnCaptureFailed(string title, string detail)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || _shutdown)
            {
                return;
            }

            StopCapture(stopAcquisition: true);
            _stateLabel.Text = $"未采集（{title}）";
        }));
        _ = (title, detail);    // 详细原因已写入日志管道
    }

    /// <summary>采集线程收尾：复位按钮（跨线程 → BeginInvoke）。</summary>
    private void OnCaptureFinished()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || _shutdown)
            {
                return;
            }

            SetButtons(running: false);
        }));
    }

    private void SetButtons(bool running)
    {
        _openButton.Enabled = !running;
        _stopButton.Enabled = running;
        _deviceUpDown.Enabled = !running;
        _resolutionCombo.Enabled = !running;
        _formatCombo.Enabled = !running;
    }

    // ———————————————————————————— 预览 ————————————————————————————

    /// <summary>200ms 轮询 CameraHost.LatestFrame，帧引用变化才重绘（旧 Bitmap 及时 Dispose）。</summary>
    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var frame = CameraHost.LatestFrame;
        if (ReferenceEquals(frame, _lastFrame))
        {
            return;
        }

        _lastFrame = frame;
        var oldImage = _preview.Image;
        _preview.Image = ImageLoader.ToPreviewBitmap(frame);
        oldImage?.Dispose();

        if (frame is not null && (frame.Width != _renderedWidth || frame.Height != _renderedHeight))
        {
            UpdatePreviewBounds();
        }
    }

    /// <summary>预览框：白底 Zoom 居中，随可用空间与帧纵横比自适应。</summary>
    private void UpdatePreviewBounds()
    {
        int availWidth = Math.Max(1, _previewHost.ClientSize.Width - _previewHost.Padding.Horizontal);
        int availHeight = Math.Max(1, _previewHost.ClientSize.Height - _previewHost.Padding.Vertical);

        int frameWidth = _lastFrame?.Width ?? 640;
        int frameHeight = _lastFrame?.Height ?? 360;
        double aspect = (double)frameWidth / Math.Max(1, frameHeight);

        int w = availWidth;
        int h = (int)Math.Round(w / aspect);
        if (h > availHeight)
        {
            h = availHeight;
            w = (int)Math.Round(h * aspect);
        }

        w = Math.Clamp(w, 1, availWidth);
        h = Math.Clamp(h, 1, availHeight);

        _preview.Size = new System.Drawing.Size(w, h);
        _preview.Location = new System.Drawing.Point(
            _previewHost.Padding.Left + (availWidth - w) / 2,
            _previewHost.Padding.Top + (availHeight - h) / 2);

        _renderedWidth = frameWidth;
        _renderedHeight = frameHeight;
    }

    // ———————————————————————————— 收尾 ————————————————————————————

    /// <summary>窗体关闭：停 USB 采集线程（相机采集由 MainForm 统一停）；幂等。</summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        StopCapture(stopAcquisition: false);
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
