using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using GigEVision.Core.Imaging;

namespace GigEVision.App.Pages;

/// <summary>
/// 模拟器页（契约第 9 节）：左侧 320px 配置列（图像源 / 采集参数 / 相机身份）+ 右侧预览。
/// 启动相机 = 写入 Config/Identity 后 EnsureStarted；开始采集 = 设置帧源后 StartAcquisition
/// （内置图案模式 Source=null，由相机采集线程的 TestPatternGenerator 兜底，契约 7）。
/// </summary>
public class SimulatorPage : UserControl
{
    /// <summary>预览框最大宽度（高度随可用空间与帧纵横比自适应）。</summary>
    private const int MaxPreviewWidth = 640;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".jpg", ".jpeg", ".png", ".tif", ".tiff",
    };

    private static readonly string[] PatternNames = ["彩条", "渐变", "网格", "动态十字"];
    private static readonly PatternType[] PatternValues =
    [
        PatternType.ColorBars, PatternType.Gradient, PatternType.Grid, PatternType.MovingCrosshair,
    ];

    private readonly RadioButton _radioImageFile;
    private readonly RadioButton _radioFolder;
    private readonly RadioButton _radioPattern;
    private readonly TextBox _pathBox;
    private readonly Button _browseButton;
    private readonly ComboBox _patternCombo;

    private readonly NumericUpDown _widthUpDown;
    private readonly NumericUpDown _heightUpDown;
    private readonly ComboBox _formatCombo;
    private readonly NumericUpDown _fpsUpDown;
    private readonly ComboBox _nicCombo;

    private readonly TextBox _serialBox;
    private readonly TextBox _userNameBox;

    private readonly Button _startCameraButton;
    private readonly Button _stopCameraButton;
    private readonly Button _startAcqButton;
    private readonly Button _stopAcqButton;
    private readonly Label _statusLabel;

    private readonly Panel _previewHost;
    private readonly PictureBox _preview;
    private readonly System.Windows.Forms.Timer _previewTimer;

    private readonly List<NicOption> _nicOptions = [];
    private FrameBuffer? _lastFrame;        // 上次渲染的帧引用（相同则跳过重绘）
    private int _renderedWidth = -1;
    private int _renderedHeight = -1;
    private bool _shutdown;

    public SimulatorPage()
    {
        BackColor = Theme.WindowBack;
        DoubleBuffered = true;

        // ———— 右侧预览区（先加入：Dock=Fill 最后布局） ————
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

        // ———— 左侧配置列 ————
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 320,
            BackColor = Theme.WindowBack,
            AutoScroll = true,
        };

        var leftTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        leftTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // ── GroupBox：图像源 ──
        _radioImageFile = Theme.MakeRadio("静态图片");
        _radioImageFile.Checked = true;
        _radioImageFile.CheckedChanged += OnSourceRadioChanged;

        _radioFolder = Theme.MakeRadio("图片文件夹轮播（5 秒间隔）");
        _radioFolder.CheckedChanged += OnSourceRadioChanged;

        _radioPattern = Theme.MakeRadio("内置测试图案");
        _radioPattern.CheckedChanged += OnSourceRadioChanged;

        _pathBox = Theme.MakeTextBox();
        _pathBox.Dock = DockStyle.Fill;
        _pathBox.PlaceholderText = "选择图片或文件夹路径";

        _browseButton = Theme.MakeButton("浏览…");
        _browseButton.Click += OnBrowse;

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_pathBox, 0, 0);
        pathRow.Controls.Add(_browseButton, 1, 0);

        _patternCombo = Theme.MakeComboBox();
        _patternCombo.Items.AddRange(PatternNames);
        _patternCombo.SelectedIndex = 0;
        _patternCombo.Dock = DockStyle.Fill;

        var patternRow = MakeFormRow("图案", _patternCombo);

        var sourceGroup = Theme.MakeGroupBox("图像源");
        var sourceTable = MakeStackTable(5);
        sourceTable.Controls.Add(_radioImageFile, 0, 0);
        sourceTable.Controls.Add(_radioFolder, 0, 1);
        sourceTable.Controls.Add(_radioPattern, 0, 2);
        sourceTable.Controls.Add(pathRow, 0, 3);
        sourceTable.Controls.Add(patternRow, 0, 4);
        sourceGroup.Controls.Add(sourceTable);
        sourceGroup.MinimumSize = new Size(300, 200);

        // ── GroupBox：采集参数 ──
        _widthUpDown = Theme.MakeNumeric(16, 8192, 1280, 2);
        _widthUpDown.Dock = DockStyle.Fill;
        _heightUpDown = Theme.MakeNumeric(16, 8192, 720, 2);
        _heightUpDown.Dock = DockStyle.Fill;
        _formatCombo = Theme.MakeComboBox();
        foreach (PixelFormatCode fmt in PixelFormatEx.Supported)
        {
            _formatCombo.Items.Add(PixelFormatEx.PName(fmt));
        }

        _formatCombo.SelectedIndex = 0;
        _formatCombo.Dock = DockStyle.Fill;
        _fpsUpDown = Theme.MakeNumeric(1, 120, 30);
        _fpsUpDown.Dock = DockStyle.Fill;
        _nicCombo = Theme.MakeComboBox();
        _nicCombo.Dock = DockStyle.Fill;
        LoadNicOptions();

        var acqGroup = Theme.MakeGroupBox("采集参数");
        var acqTable = Theme.MakeFormTable();
        AddFormRow(acqTable, 0, Theme.MakeLabel("宽度"), _widthUpDown);
        AddFormRow(acqTable, 1, Theme.MakeLabel("高度"), _heightUpDown);
        AddFormRow(acqTable, 2, Theme.MakeLabel("像素格式"), _formatCombo);
        AddFormRow(acqTable, 3, Theme.MakeLabel("帧率 (fps)"), _fpsUpDown);
        AddFormRow(acqTable, 4, Theme.MakeLabel("网卡"), _nicCombo);
        acqGroup.Controls.Add(acqTable);
        acqGroup.MinimumSize = new Size(300, 200);

        // ── GroupBox：相机身份 ──
        _serialBox = Theme.MakeTextBox();
        _serialBox.Text = "GF-0000001";
        _serialBox.Dock = DockStyle.Fill;
        _userNameBox = Theme.MakeTextBox();
        _userNameBox.Text = "GigEForgeCam";
        _userNameBox.Dock = DockStyle.Fill;

        var idGroup = Theme.MakeGroupBox("相机身份");
        var idTable = Theme.MakeFormTable();
        AddFormRow(idTable, 0, Theme.MakeLabel("SerialNumber"), _serialBox);
        AddFormRow(idTable, 1, Theme.MakeLabel("UserDefinedName"), _userNameBox);
        idGroup.Controls.Add(idTable);
        idGroup.MinimumSize = new Size(300, 110);

        // ── 按钮排（2×2）──
        _startCameraButton = Theme.MakeButton("启动相机", Theme.Emerald);
        _startCameraButton.Dock = DockStyle.Fill;
        _startCameraButton.Click += OnStartCamera;

        _stopCameraButton = Theme.MakeButton("停止相机");
        _stopCameraButton.Dock = DockStyle.Fill;
        _stopCameraButton.Click += OnStopCamera;

        _startAcqButton = Theme.MakeButton("开始采集");
        _startAcqButton.Dock = DockStyle.Fill;
        _startAcqButton.Click += OnStartAcquisition;

        _stopAcqButton = Theme.MakeButton("停止采集");
        _stopAcqButton.Dock = DockStyle.Fill;
        _stopAcqButton.Click += OnStopAcquisition;

        var buttonTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(6, 2, 6, 2),
        };
        buttonTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttonTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttonTable.Controls.Add(_startCameraButton, 0, 0);
        buttonTable.Controls.Add(_stopCameraButton, 1, 0);
        buttonTable.Controls.Add(_startAcqButton, 0, 1);
        buttonTable.Controls.Add(_stopAcqButton, 1, 1);

        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 44,
            ForeColor = Theme.DimText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(6, 2, 6, 2),
            Text = "空闲（相机未启动）",
        };

        leftTable.Controls.Add(sourceGroup, 0, 0);
        leftTable.Controls.Add(acqGroup, 0, 1);
        leftTable.Controls.Add(idGroup, 0, 2);
        leftTable.Controls.Add(buttonTable, 0, 3);
        leftTable.Controls.Add(_statusLabel, 0, 4);
        leftPanel.Controls.Add(leftTable);

        Controls.Add(_previewHost);   // Fill：最后布局
        Controls.Add(leftPanel);      // Left：先停靠

        SetButtons(cameraRunning: false, streaming: false);
        OnSourceRadioChanged(this, EventArgs.Empty);

        // 预览轮询：200ms 读取 CameraHost.LatestFrame（相机 FrameStreamed 事件的缓存）
        _previewTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();

        // 客户端（Pylon 等）写采集命令寄存器时同步按钮状态
        CameraHost.Instance.AcquisitionChanged += OnAcquisitionChanged;
    }

    // ———————————————————————————— 布局小工具 ————————————————————————————

    /// <summary>单列堆叠表（GroupBox 内容用）。</summary>
    private static TableLayoutPanel MakeStackTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = rows,
            Margin = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < rows; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return table;
    }

    /// <summary>两列表（标签列自动宽 + 控件列 100%）。</summary>
    private static TableLayoutPanel MakeFormRow(string labelText, Control control)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = Theme.MakeLabel(labelText);
        table.Controls.Add(label, 0, 0);
        table.Controls.Add(control, 1, 0);
        return table;
    }

    private static void AddFormRow(TableLayoutPanel table, int row, Control label, Control control)
    {
        table.RowCount = Math.Max(table.RowCount, row + 1);
        while (table.RowStyles.Count <= row)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        label.Margin = new Padding(3, 7, 6, 3);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
    }

    // ———————————————————————————— 参数读取 ————————————————————————————

    private sealed record NicOption(string Display, IPAddress? Address)
    {
        public override string ToString() => Display;
    }

    /// <summary>填充网卡下拉框：第 1 项固定"任意适配器"，其后为所有在线网卡的 IPv4。</summary>
    private void LoadNicOptions()
    {
        _nicOptions.Add(new NicOption("任意适配器", null));
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPAddress? ip = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (ip is null)
                {
                    continue;
                }

                _nicOptions.Add(new NicOption($"{nic.Name} ({ip})", ip));
            }
        }
        catch
        {
            // 网卡枚举失败：仅保留"任意适配器"兜底项
        }

        foreach (var option in _nicOptions)
        {
            _nicCombo.Items.Add(option);
        }

        _nicCombo.SelectedIndex = 0;
    }

    private IPAddress? SelectedNic() =>
        _nicCombo.SelectedItem is NicOption option ? option.Address : null;

    private PixelFormatCode SelectedFormat() =>
        PixelFormatEx.Supported[Math.Clamp(_formatCombo.SelectedIndex, 0, PixelFormatEx.Supported.Count - 1)];

    private PatternType SelectedPattern() =>
        PatternValues[Math.Clamp(_patternCombo.SelectedIndex, 0, PatternValues.Length - 1)];

    /// <summary>
    /// 把 UI 参数写入相机：宽/高/格式/帧率走寄存器路径（onWrite 钩子同步 Config 与 PayloadSize），
    /// 身份同步 Discovery 载荷与 DeviceID 内存区。在 EnsureStarted 之前调用。
    /// </summary>
    private void ApplyConfigToCamera()
    {
        CameraHost.ApplyIdentity(_serialBox.Text, _userNameBox.Text);
        CameraHost.ApplyCaptureSettings(
            (int)_widthUpDown.Value,
            (int)_heightUpDown.Value,
            SelectedFormat(),
            (int)_fpsUpDown.Value);
    }

    // ———————————————————————————— 按钮动作 ————————————————————————————

    private void OnSourceRadioChanged(object? sender, EventArgs e)
    {
        bool pattern = _radioPattern.Checked;
        _patternCombo.Enabled = pattern;
        _pathBox.Enabled = !pattern;
        _browseButton.Enabled = !pattern;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        if (_radioFolder.Checked)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择图片文件夹（BMP/JPG/PNG/TIF，每 5 秒轮播）",
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true,
            };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                _pathBox.Text = dialog.SelectedPath;
            }

            return;
        }

        using var fileDialog = new OpenFileDialog
        {
            Title = "选择静态图片",
            Filter = "图片文件|*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff|全部文件|*.*",
            CheckFileExists = true,
        };
        if (fileDialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _pathBox.Text = fileDialog.FileName;
        }
    }

    private void OnStartCamera(object? sender, EventArgs e)
    {
        try
        {
            var camera = CameraHost.Instance;      // 确保单例构建（构建不绑定端口）
            ApplyConfigToCamera();                 // 先写参数，再上线
            CameraHost.EnsureStarted(SelectedNic());

            SetButtons(cameraRunning: true, streaming: camera.IsStreaming);
            UpdateStatusLabel();
            CameraHost.Commit(
                $"模拟器：相机已上线 {camera.Config.Width}x{camera.Config.Height} " +
                $"{PixelFormatEx.PName(camera.Config.Format)} @{camera.Config.Fps}fps");
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"模拟器：启动相机失败 — {ex.Message}");
            MessageBox.Show($"启动相机失败：{ex.Message}", "GigEForge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnStopCamera(object? sender, EventArgs e)
    {
        // Stop() 内部会先停采集再下线 GVCP（幂等）
        CameraHost.Instance.Stop();
        SetButtons(cameraRunning: false, streaming: false);
        UpdateStatusLabel();
    }

    private void OnStartAcquisition(object? sender, EventArgs e)
    {
        try
        {
            var camera = CameraHost.Instance;
            if (!camera.IsRunning)
            {
                ApplyConfigToCamera();
                CameraHost.EnsureStarted(SelectedNic());
                CameraHost.Commit("模拟器：相机尚未启动，已自动上线");
            }

            camera.Source = BuildFrameSource();    // 图案模式返回 null → 内置 TestPatternGenerator 兜底
            camera.StartAcquisition();

            SetButtons(cameraRunning: true, streaming: true);
            UpdateStatusLabel();
        }
        catch (Exception ex)
        {
            CameraHost.Commit($"模拟器：开始采集失败 — {ex.Message}");
            MessageBox.Show($"开始采集失败：{ex.Message}", "GigEForge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnStopAcquisition(object? sender, EventArgs e)
    {
        CameraHost.Instance.StopAcquisition();
        SetButtons(cameraRunning: CameraHost.Instance.IsRunning, streaming: false);
        UpdateStatusLabel();
    }

    /// <summary>
    /// 依据图像源选择构建帧源：静态图 → 单文件 ImageFileSource；文件夹 → 多文件轮播；
    /// 内置图案 → 返回 null（采集线程用 TestPatternGenerator 按 Config.Pattern 兜底）。
    /// </summary>
    private IFrameSource? BuildFrameSource()
    {
        var format = SelectedFormat();
        int width = (int)_widthUpDown.Value;
        int height = (int)_heightUpDown.Value;

        if (_radioPattern.Checked)
        {
            // 同步图案寄存器（0x14001C），Source = null → 采集线程用内置 TestPatternGenerator 兜底
            CameraHost.ApplyCaptureSettings(width, height, format, pattern: SelectedPattern());
            CameraHost.Commit($"模拟器：帧源 = 内置测试图案（{PatternNames[Math.Max(0, _patternCombo.SelectedIndex)]}）");
            return null;
        }

        string path = _pathBox.Text.Trim();
        if (_radioImageFile.Checked)
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                throw new FileNotFoundException("请先在「图像源」中选择一张图片文件", path);
            }

            CameraHost.Commit($"模拟器：帧源 = 静态图片 {Path.GetFileName(path)} → {width}x{height}");
            return new ImageFileSource([path], format, width, height);
        }

        if (path.Length == 0 || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("请先在「图像源」中选择图片文件夹");
        }

        List<string> files = Directory.EnumerateFiles(path)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("文件夹中没有 BMP/JPG/PNG/TIF 图片");
        }

        CameraHost.Commit($"模拟器：帧源 = 文件夹轮播 {files.Count} 张（5 秒间隔）→ {width}x{height}");
        return new ImageFileSource(files, format, width, height);
    }

    // ———————————————————————————— 状态同步 ————————————————————————————

    private void SetButtons(bool cameraRunning, bool streaming)
    {
        _startCameraButton.Enabled = !cameraRunning;
        _stopCameraButton.Enabled = cameraRunning;
        _startAcqButton.Enabled = cameraRunning && !streaming;
        _stopAcqButton.Enabled = cameraRunning && streaming;
    }

    /// <summary>状态 Label：空闲 / GVCP 在线（含 GenICam URL 短形式）/ 采集中。</summary>
    private void UpdateStatusLabel()
    {
        var camera = CameraHost.Instance;
        if (!camera.IsRunning)
        {
            _statusLabel.Text = "空闲（相机未启动）";
            return;
        }

        _statusLabel.Text = camera.IsStreaming
            ? $"GVCP 3956 在线 · 采集中 {camera.Stats.CurrentFps:0.0} fps · GenICam local;GigEForge.xml"
            : "GVCP 3956 在线 · GenICam local;GigEForge.xml";
    }

    /// <summary>采集启停变化（含客户端写寄存器触发，跨线程）→ 切回 UI 线程同步按钮。</summary>
    private void OnAcquisitionChanged()
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

            var camera = CameraHost.Instance;
            SetButtons(camera.IsRunning, camera.IsStreaming);
            UpdateStatusLabel();
        }));
    }

    // ———————————————————————————— 预览 ————————————————————————————

    /// <summary>200ms 轮询：帧引用变化才重绘预览（旧 Bitmap 及时 Dispose）。</summary>
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

    /// <summary>预览框：白底 Zoom 居中，宽上限 640，高度按可用空间与帧纵横比自适应。</summary>
    private void UpdatePreviewBounds()
    {
        int availWidth = Math.Max(1, _previewHost.ClientSize.Width - _previewHost.Padding.Horizontal);
        int availHeight = Math.Max(1, _previewHost.ClientSize.Height - _previewHost.Padding.Vertical);

        int frameWidth = _lastFrame?.Width ?? 640;
        int frameHeight = _lastFrame?.Height ?? 360;
        double aspect = (double)frameWidth / Math.Max(1, frameHeight);

        int w = Math.Min(availWidth, MaxPreviewWidth);
        int h = (int)Math.Round(w / aspect);
        if (h > availHeight)
        {
            h = availHeight;
            w = (int)Math.Round(h * aspect);
        }

        w = Math.Clamp(w, 1, availWidth);
        h = Math.Clamp(h, 1, availHeight);

        _preview.Size = new Size(w, h);
        _preview.Location = new Point(
            _previewHost.Padding.Left + (availWidth - w) / 2,
            _previewHost.Padding.Top + (availHeight - h) / 2);

        _renderedWidth = frameWidth;
        _renderedHeight = frameHeight;
    }

    // ———————————————————————————— 收尾 ————————————————————————————

    /// <summary>停预览定时器并解绑相机事件（幂等；MainForm 关闭时调用，Dispose 兜底）。</summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        if (CameraHost.IsCreated)
        {
            CameraHost.Instance.AcquisitionChanged -= OnAcquisitionChanged;
        }
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
