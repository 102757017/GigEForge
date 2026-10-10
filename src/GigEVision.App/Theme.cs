using System.Drawing;

namespace GigEVision.App;

/// <summary>
/// 深色主题常量与控件工厂（契约第 9 节）：
/// 三个主色 emerald/amber/violet 只用于各页主按钮与状态点，其余一律中性色。
/// </summary>
internal static class Theme
{
    // —— 契约三主色 ——
    /// <summary>模拟器主按钮 / 在线状态点（emerald #10B981）。</summary>
    public static readonly Color Emerald = FromHex(0x10B981);

    /// <summary>转换器主按钮（amber #F59E0B）。</summary>
    public static readonly Color Amber = FromHex(0xF59E0B);

    /// <summary>SDK 服务主按钮（violet #8B5CF6）。</summary>
    public static readonly Color Violet = FromHex(0x8B5CF6);

    // —— 契约中性色 ——
    /// <summary>窗体背景 #1E1E22。</summary>
    public static readonly Color WindowBack = FromHex(0x1E1E22);

    /// <summary>面板 / GroupBox 背景 #26262B。</summary>
    public static readonly Color PanelBack = FromHex(0x26262B);

    /// <summary>主文本 #E4E4E7。</summary>
    public static readonly Color TextColor = FromHex(0xE4E4E7);

    /// <summary>日志背景 #141416。</summary>
    public static readonly Color LogBack = FromHex(0x141416);

    // —— 派生中性色（仅深浅灰，不引入其他色相）——
    /// <summary>次要文本（灰）。</summary>
    public static readonly Color DimText = FromHex(0xA1A1AA);

    /// <summary>离线状态点 / 禁用文本（深灰）。</summary>
    public static readonly Color OfflineGray = FromHex(0x71717A);

    /// <summary>控件描边（中性灰）。</summary>
    public static readonly Color Border = FromHex(0x3F3F46);

    /// <summary>输入框底色（比面板更深一档的中性色）。</summary>
    public static readonly Color InputBack = FromHex(0x1B1B1F);

    /// <summary>主按钮文字色（深色中性，保证在三主色上的可读性）。</summary>
    public static readonly Color OnAccent = FromHex(0x141416);

    /// <summary>全局基础字体。</summary>
    public static readonly Font BaseFont = new("Microsoft YaHei UI", 9F);

    /// <summary>日志等宽字体。</summary>
    public static readonly Font LogFont = new("Consolas", 8.5F);

    /// <summary>代码示例等宽字体。</summary>
    public static readonly Font MonoFont = new("Consolas", 9F);

    private static Color FromHex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>
    /// 应用全局 ToolStrip 渲染器（状态栏 / 下拉框等共享），把系统默认的浅色渐变统一为深色中性。
    /// 在主窗体构造时调用一次即可。
    /// </summary>
    public static void ApplyGlobalRenderer()
    {
        ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
    }

    /// <summary>深色 ProfessionalColorTable：仅覆盖状态栏 / 菜单下拉涉及的中性色。</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color StatusStripGradientBegin => WindowBack;
        public override Color StatusStripGradientEnd => WindowBack;
        public override Color ToolStripDropDownBackground => PanelBack;
        public override Color ImageMarginGradientBegin => PanelBack;
        public override Color ImageMarginGradientMiddle => PanelBack;
        public override Color ImageMarginGradientEnd => PanelBack;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => PanelBack;
        public override Color MenuItemSelectedGradientBegin => PanelBack;
        public override Color MenuItemSelectedGradientEnd => PanelBack;
        public override Color MenuItemPressedGradientBegin => PanelBack;
        public override Color MenuItemPressedGradientEnd => PanelBack;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    // ———————————————————————————— 控件工厂 ————————————————————————————

    /// <summary>深色按钮；accent 为空 = 中性次级按钮，否则为主色实心按钮。</summary>
    public static Button MakeButton(string text, Color? accent = null)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            MinimumSize = new Size(0, 30),
            Margin = new Padding(3),
            UseVisualStyleBackColor = false,
        };

        if (accent.HasValue)
        {
            button.BackColor = accent.Value;
            button.ForeColor = OnAccent;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(accent.Value, 0.1F);
        }
        else
        {
            button.BackColor = PanelBack;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(PanelBack, 0.06F);
        }

        return button;
    }

    /// <summary>深色 GroupBox（Auto：随内容与 DPI 缩放增高，MinimumSize 由调用方设定兜底）。</summary>
    public static GroupBox MakeGroupBox(string title)
    {
        return new GroupBox
        {
            Text = title,
            BackColor = PanelBack,
            ForeColor = TextColor,
            Font = BaseFont,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(6),
        };
    }

    /// <summary>深色单行输入框。</summary>
    public static TextBox MakeTextBox()
    {
        return new TextBox
        {
            BackColor = InputBack,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3),
        };
    }

    /// <summary>深色下拉框（默认只读选择）。</summary>
    public static ComboBox MakeComboBox(bool editable = false)
    {
        return new ComboBox
        {
            FlatStyle = FlatStyle.Flat,
            BackColor = InputBack,
            ForeColor = TextColor,
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            Margin = new Padding(3),
        };
    }

    /// <summary>数值输入框（统一外观）。</summary>
    public static NumericUpDown MakeNumeric(decimal min, decimal max, decimal value, decimal increment = 1)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Increment = increment,
            BackColor = InputBack,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3),
            TextAlign = HorizontalAlignment.Left,
        };
    }

    /// <summary>深色文本标签；dim = 次要文本。</summary>
    public static Label MakeLabel(string text, bool dim = false)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = dim ? DimText : TextColor,
            BackColor = Color.Transparent,
            Margin = new Padding(3, 6, 3, 3),
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    /// <summary>深色单选项。</summary>
    public static RadioButton MakeRadio(string text)
    {
        return new RadioButton
        {
            Text = text,
            AutoSize = true,
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Margin = new Padding(3, 4, 3, 2),
            UseVisualStyleBackColor = false,
        };
    }

    /// <summary>构建 2 列（标签列自动宽 + 控件列 100%）的布局表。</summary>
    public static TableLayoutPanel MakeFormTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return table;
    }
}
