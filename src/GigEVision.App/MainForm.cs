using System.Drawing;
using System.Windows.Forms;
using GigEVision.App.Pages;

namespace GigEVision.App;

/// <summary>
/// 主窗体：三个页签（模拟器 / 转换器 / SDK 服务）+ 底部日志与状态栏（契约第 9 节）。
/// 负责：全局深色主题、1s 状态栏刷新、日志管道展示、窗体关闭时统一停机（页面 → 相机）。
/// </summary>
public class MainForm : Form
{
    /// <summary>日志 TextBox 兜底清理阈值（行数；完整历史仍在 CameraHost 环形缓冲中）。</summary>
    private const int MaxLogLines = 400;

    private readonly TabControl _tabs;
    private readonly SimulatorPage _simulatorPage;
    private readonly ConverterPage _converterPage;
    private readonly SdkPage _sdkPage;
    private readonly TextBox _logBox;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _stateLabel;
    private readonly ToolStripStatusLabel _controllerLabel;
    private readonly ToolStripStatusLabel _fpsLabel;
    private readonly ToolStripStatusLabel _framesLabel;
    private readonly ToolStripStatusLabel _resendLabel;
    private readonly System.Windows.Forms.Timer _statsTimer;

    public MainForm()
    {
        Theme.ApplyGlobalRenderer();

        Text = "GigEForge — GigE Vision 虚拟相机工作台";
        Font = Theme.BaseFont;
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextColor;
        Size = new Size(1100, 720);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;

        _simulatorPage = new SimulatorPage();
        _converterPage = new ConverterPage();
        _sdkPage = new SdkPage();

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(150, 32),
            SizeMode = TabSizeMode.Fixed,
            BackColor = Theme.WindowBack,
            Margin = new Padding(0),
        };
        _tabs.TabPages.Add(MakeTab("模拟器", _simulatorPage));
        _tabs.TabPages.Add(MakeTab("转换器", _converterPage));
        _tabs.TabPages.Add(MakeTab("SDK 服务", _sdkPage));
        _tabs.DrawItem += OnTabDrawItem;

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Bottom,
            Height = 140,
            BackColor = Theme.LogBack,
            ForeColor = Theme.TextColor,
            Font = Theme.LogFont,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BorderStyle = BorderStyle.FixedSingle,
            TabStop = false,
        };

        _stateLabel = new ToolStripStatusLabel("● 空闲")
        {
            ForeColor = Theme.OfflineGray,
            AutoSize = true,
            Margin = new Padding(4, 0, 20, 0),
        };
        _controllerLabel = MakeStatus("控制端: -", 20);
        _fpsLabel = MakeStatus("0.0 fps", 20);
        _framesLabel = MakeStatus("发帧 0", 20);
        _resendLabel = MakeStatus("重发 0", 0);

        _status = new StatusStrip
        {
            BackColor = Theme.WindowBack,
            GripStyle = ToolStripGripStyle.Hidden,
            SizingGrip = false,
            Dock = DockStyle.Bottom,
            Padding = new Padding(8, 3, 8, 3),
        };
        _status.Items.AddRange(
        [
            _stateLabel,
            _controllerLabel,
            _fpsLabel,
            _framesLabel,
            _resendLabel,
            new ToolStripStatusLabel { Spring = true, AutoSize = false },
        ]);

        // Dock 布局按加入集合的逆序生效：最后加入的最先停靠（占据最外缘）
        Controls.Add(_tabs);      // Fill：最后布局，占据日志与状态栏之外的剩余区域
        Controls.Add(_logBox);    // Bottom：位于状态栏之上
        Controls.Add(_status);    // Bottom：最底部

        _statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();

        // 日志管道：camera.Log 已由 CameraHost 聚合入环形缓冲，这里只消费提交事件并回放历史
        foreach (string line in CameraHost.SnapshotLogs())
        {
            AppendLog(line);
        }

        CameraHost.LogCommitted += OnLogCommitted;

        Load += (_, _) => CameraHost.Commit("GigEForge 工作台已就绪（GVCP 3956 · GenICam local 模式）");
    }

    /// <summary>创建页签页（深色背景 + 页面 UserControl 填充）。</summary>
    private static TabPage MakeTab(string title, UserControl page)
    {
        var tab = new TabPage(title) { BackColor = Theme.WindowBack, Padding = new Padding(0) };
        page.Dock = DockStyle.Fill;
        tab.Controls.Add(page);
        return tab;
    }

    private static ToolStripStatusLabel MakeStatus(string text, int rightMargin)
    {
        return new ToolStripStatusLabel(text)
        {
            ForeColor = Theme.DimText,
            AutoSize = true,
            Margin = new Padding(0, 0, rightMargin, 0),
        };
    }

    /// <summary>页签自绘：深色底 + 选中页加粗高亮，下划线颜色与该页主按钮主色一致。</summary>
    private void OnTabDrawItem(object? sender, DrawItemEventArgs e)
    {
        Color[] accents = [Theme.Emerald, Theme.Amber, Theme.Violet];
        bool selected = e.Index == _tabs.SelectedIndex;

        using (var back = new SolidBrush(Theme.WindowBack))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        Font font = selected ? new Font(Font, FontStyle.Bold) : Font;
        TextRenderer.DrawText(
            e.Graphics,
            _tabs.TabPages[e.Index].Text,
            font,
            e.Bounds,
            selected ? Theme.TextColor : Theme.DimText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (!ReferenceEquals(font, Font))
        {
            font.Dispose();
        }

        if (selected)
        {
            using var accent = new SolidBrush(accents[Math.Clamp(e.Index, 0, accents.Length - 1)]);
            e.Graphics.FillRectangle(accent, e.Bounds.Left + 14, e.Bounds.Bottom - 4, e.Bounds.Width - 28, 3);
        }
    }

    // ———————————————————————————— 日志 ————————————————————————————

    /// <summary>CameraHost 提交线程回调：切回 UI 线程后追加。</summary>
    private void OnLogCommitted(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), line);
            return;
        }

        AppendLog(line);
    }

    /// <summary>追加一行日志并滚动到底；超出阈值时清屏（环形缓冲仍保留最近 200 条）。</summary>
    private void AppendLog(string line)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (_logBox.GetLineFromCharIndex(_logBox.TextLength) >= MaxLogLines)
        {
            _logBox.Clear();
        }

        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;   // 滚动到底
        _logBox.SelectionLength = 0;
        _logBox.ScrollToCaret();
    }

    // ———————————————————————————— 状态栏 ————————————————————————————

    /// <summary>每秒刷新：在线状态点 / 控制端 / fps / 发帧 / 重发（读相机 Stats 快照）。</summary>
    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        bool online = CameraHost.IsRunning;
        _stateLabel.ForeColor = online ? Theme.Emerald : Theme.OfflineGray;
        _stateLabel.Text = online ? "● 在线" : "● 空闲";

        var controller = CameraHost.CurrentController;
        _controllerLabel.Text = $"控制端: {controller?.ToString() ?? "-"}";

        if (CameraHost.IsCreated)
        {
            var stats = CameraHost.Instance.Stats;
            _fpsLabel.Text = $"{stats.CurrentFps:0.0} fps";
            _framesLabel.Text = $"发帧 {stats.FramesSent}";
            _resendLabel.Text = $"重发 {stats.ResendPackets}";
        }
        else
        {
            _fpsLabel.Text = "0.0 fps";
            _framesLabel.Text = "发帧 0";
            _resendLabel.Text = "重发 0";
        }
    }

    // ———————————————————————————— 关闭收尾 ————————————————————————————

    /// <summary>窗体关闭：先停各页面（线程 / 定时器 / SDK 服务），再统一停相机。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        CameraHost.LogCommitted -= OnLogCommitted;
        _tabs.DrawItem -= OnTabDrawItem;
        _statsTimer.Stop();
        _statsTimer.Tick -= OnStatsTick;
        _statsTimer.Dispose();

        _simulatorPage.Shutdown();
        _converterPage.Shutdown();
        _sdkPage.Shutdown();
        CameraHost.StopCamera();
    }
}
