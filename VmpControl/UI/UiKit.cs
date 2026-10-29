using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using VmpControl.Core;

namespace VmpControl.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(18, 20, 24);
    public static readonly Color Panel = Color.FromArgb(27, 30, 37);
    public static readonly Color Panel2 = Color.FromArgb(36, 40, 50);
    public static readonly Color Border = Color.FromArgb(52, 58, 71);
    public static readonly Color Text = Color.FromArgb(230, 232, 236);
    public static readonly Color Muted = Color.FromArgb(138, 147, 163);
    public static readonly Color Accent = Color.FromArgb(76, 141, 255);
    public static readonly Color Green = Color.FromArgb(61, 214, 140);
    public static readonly Color Red = Color.FromArgb(255, 92, 92);
    public static readonly Color Amber = Color.FromArgb(255, 184, 77);
    public static readonly Color Grey = Color.FromArgb(125, 133, 146);

    public static readonly Font Base = new("Segoe UI", 9.5f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Big = new("Segoe UI Semibold", 15f);
    public static readonly Font Huge = new("Segoe UI Semibold", 18f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Mono = new("Consolas", 9.5f);

    public static Color ColorOf(TrayState state) => state switch
    {
        TrayState.Down => Red,
        TrayState.Idle => Accent,
        TrayState.Active => Green,
        _ => Grey,
    };
}

internal static class Native
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size, Mask;
        public int Min, Max;
        public uint Page;
        public int Pos, TrackPos;
    }

    public const int WM_SETREDRAW = 0x000B;
    public const int EM_LINESCROLL = 0x00B6;

    /// <summary>Dark scrollbars for the control and for the scrollbar child controls a DataGridView creates itself.</summary>
    public static void DarkScrollbars(Control control)
    {
        static void Theme(Control c)
        {
            void Apply() => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            if (c.IsHandleCreated) Apply();
            c.HandleCreated += (_, _) => Apply();
        }

        Theme(control);
        foreach (Control child in control.Controls)
        {
            if (child is ScrollBar) Theme(child);
        }

        control.ControlAdded += (_, e) =>
        {
            if (e.Control is ScrollBar) Theme(e.Control);
        };
    }

    public static void DarkTitleBar(Form form)
    {
        void Apply()
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
        }

        if (form.IsHandleCreated) Apply();
        else form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>True when the vertical scrollbar is at (or within a few pixels of) the bottom, or there is nothing to scroll.</summary>
    public static bool IsScrolledToBottom(Control control)
    {
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 }; // SIF_RANGE | SIF_PAGE | SIF_POS | SIF_TRACKPOS
        if (!GetScrollInfo(control.Handle, 1, ref info)) return true; // SB_VERT
        return info.Pos + (int)info.Page >= info.Max - 2;
    }

    public static void SetRedraw(Control control, bool on) => SendMessage(control.Handle, WM_SETREDRAW, on ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);

    public static void Destroy(IntPtr icon) => DestroyIcon(icon);
}

internal enum ButtonKind
{
    Normal,
    Primary,
    Danger,
}

internal static class Ui
{
    public static Button Button(string text, ButtonKind kind = ButtonKind.Normal, int width = 0)
    {
        var back = kind switch { ButtonKind.Primary => Theme.Accent, ButtonKind.Danger => Color.FromArgb(150, 52, 58), _ => Theme.Panel2 };
        var hover = kind switch { ButtonKind.Primary => Color.FromArgb(104, 160, 255), ButtonKind.Danger => Color.FromArgb(190, 66, 74), _ => Color.FromArgb(48, 54, 68) };
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = kind == ButtonKind.Primary ? Color.White : Theme.Text,
            Font = Theme.Base,
            Height = 30,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 0, 10, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AutoSize = width == 0,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        if (width > 0) button.Width = width;
        button.FlatAppearance.BorderColor = kind == ButtonKind.Normal ? Theme.Border : back;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = back;
        return button;
    }

    public static Label Label(string text, bool muted = false, Font font = null)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = muted ? Theme.Muted : Theme.Text, BackColor = Color.Transparent, Font = font ?? Theme.Base, Margin = new Padding(0, 0, 8, 0) };
    }

    public static TextBox TextBox(int width = 0)
    {
        var box = new TextBox { BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Base, Margin = new Padding(0, 2, 8, 0) };
        if (width > 0) box.Width = width;
        return box;
    }

    public static ComboBox Combo(int width)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            Width = width,
            Margin = new Padding(0, 2, 8, 0),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = TextRenderer.MeasureText("Ag", Theme.Base).Height + 4,
        };
        // Owner-drawn so the closed box and the list are dark (a flat DropDownList is white otherwise).
        combo.DrawItem += (_, e) =>
        {
            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using var back = new SolidBrush(selected ? Color.FromArgb(46, 62, 94) : Theme.Panel2);
            e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), Theme.Base, new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        return combo;
    }

    public static FlowLayoutPanel Row(params Control[] children)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.AddRange(children);
        return row;
    }

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28,
            EnableHeadersVisualStyles = false,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            BackgroundColor = Theme.Panel,
            GridColor = Theme.Border,
            Font = Theme.Base,
            ScrollBars = ScrollBars.Vertical,
            ShowCellToolTips = true,
        };
        // DataGridView does not scale its row and header heights with the form, so size them from the (DPI-scaled) font.
        var rowHeight = TextRenderer.MeasureText("Ag", Theme.Base).Height + 10;
        grid.RowTemplate.Height = rowHeight;
        grid.ColumnHeadersHeight = rowHeight + 6;
        grid.DefaultCellStyle.BackColor = Theme.Panel;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(46, 62, 94);
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(31, 34, 42);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Panel2;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
        Native.DarkScrollbars(grid);
        return grid;
    }

    /// <summary>Adds text columns. A width of 0 or less gives the column a share of the remaining space; numbers are right-aligned.</summary>
    public static void Columns(DataGridView grid, params (string Header, float Weight, bool Right)[] columns)
    {
        foreach (var (header, weight, right) in columns)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = Math.Max(1, weight), SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 40 };
            if (right)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }

            grid.Columns.Add(column);
        }
    }

    /// <summary>Replaces the rows without flicker or losing the user's scroll position.</summary>
    public static void Fill(DataGridView grid, IEnumerable<object[]> rows)
    {
        var first = grid.FirstDisplayedScrollingRowIndex;
        var selected = grid.CurrentRow?.Index ?? -1;
        grid.SuspendLayout();
        grid.Rows.Clear();
        foreach (var row in rows) grid.Rows.Add(row);
        if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        if (selected >= 0 && selected < grid.Rows.Count) grid.Rows[selected].Selected = true;
        grid.ResumeLayout();
    }

    public static Panel Card(Control inner, Padding? padding = null)
    {
        var card = new Panel { BackColor = Theme.Panel, Padding = padding ?? new Padding(12), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
        inner.Dock = DockStyle.Fill;
        card.Controls.Add(inner);
        return card;
    }

    public static Label Heading(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 6) };
    }
}

/// <summary>A caption with a big value under it.</summary>
internal sealed class StatCard : Panel
{
    private readonly Label _caption = new();
    private readonly Label _value = new();

    public StatCard(string caption)
    {
        BackColor = Theme.Panel;
        Size = new Size(150, 64);
        Margin = new Padding(0, 0, 10, 10);
        Padding = new Padding(12, 8, 8, 6);
        _caption.Text = caption;
        _caption.Font = Theme.Small;
        _caption.ForeColor = Theme.Muted;
        _caption.AutoSize = true;
        _caption.Location = new Point(12, 8);
        _value.Text = "-";
        _value.Font = Theme.Big;
        _value.ForeColor = Theme.Text;
        _value.AutoSize = false;
        _value.AutoEllipsis = true;
        _value.Location = new Point(10, 26);
        _value.Size = new Size(Width - 14, 30);
        Controls.Add(_caption);
        Controls.Add(_value);
        Resize += (_, _) => _value.Width = Math.Max(10, Width - 14);
    }

    public string Value
    {
        get => _value.Text;
        set { if (_value.Text != value) _value.Text = value; }
    }

    public Color ValueColor
    {
        set => _value.ForeColor = value;
    }
}

/// <summary>Two small lines (out and in) over the last few minutes.</summary>
internal sealed class Sparkline : Control
{
    private readonly List<double> _out = new(), _in = new();
    private const int MaxPoints = 120;

    public Sparkline()
    {
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Size = new Size(260, 46);
    }

    public void Add(double outRate, double inRate)
    {
        _out.Add(outRate);
        _in.Add(inRate);
        if (_out.Count > MaxPoints) _out.RemoveAt(0);
        if (_in.Count > MaxPoints) _in.RemoveAt(0);
        Invalidate();
    }

    public void Clear()
    {
        _out.Clear();
        _in.Clear();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Theme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var max = Math.Max(1.0, Math.Max(_out.Count > 0 ? _out.Max() : 0, _in.Count > 0 ? _in.Max() : 0));
        Draw(g, _out, max, Theme.Accent);
        Draw(g, _in, max, Theme.Green);
        if (_out.Count < 2)
        {
            TextRenderer.DrawText(g, "collecting...", Theme.Small, new Point(8, Height / 2 - 8), Theme.Muted);
        }
    }

    private void Draw(Graphics g, List<double> series, double max, Color color)
    {
        if (series.Count < 2) return;
        var points = new PointF[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            var x = 3 + (Width - 7) * (float)i / (MaxPoints - 1) + (Width - 7) * (float)(MaxPoints - series.Count) / (MaxPoints - 1);
            var y = Height - 4 - (float)(series[i] / max) * (Height - 9);
            points[i] = new PointF(x, y);
        }

        using var pen = new Pen(color, 1.6f);
        g.DrawLines(pen, points);
    }
}

/// <summary>A row of flat tab buttons with an underline on the selected one.</summary>
internal sealed class TabBar : Panel
{
    private readonly List<Button> _buttons = new();
    public event Action<int> SelectedChanged;
    public int Selected { get; private set; } = -1;

    public TabBar()
    {
        Height = 38;
        Dock = DockStyle.Top;
        BackColor = Theme.Bg;
        Padding = new Padding(8, 0, 8, 0);
    }

    public void Add(string title)
    {
        var index = _buttons.Count;
        var button = new Button
        {
            Text = title,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Bg,
            ForeColor = Theme.Muted,
            Font = Theme.Bold,
            Size = new Size(TextRenderer.MeasureText(title, Theme.Bold).Width + 30, 38),
            Location = new Point(8 + _buttons.Sum(b => b.Width), 0),
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Panel;
        button.FlatAppearance.MouseDownBackColor = Theme.Panel2;
        button.Click += (_, _) => Select(index);
        button.Paint += (_, e) =>
        {
            if (Selected != index) return;
            using var brush = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(brush, 10, button.Height - 3, button.Width - 20, 3);
        };
        _buttons.Add(button);
        Controls.Add(button);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _buttons.Count || index == Selected) return;
        Selected = index;
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].ForeColor = i == index ? Theme.Text : Theme.Muted;
            _buttons[i].Invalidate();
        }

        SelectedChanged?.Invoke(index);
    }
}

/// <summary>Round status icons drawn at runtime (no image files): grey connecting, red down, blue idle, green with players.</summary>
internal static class IconFactory
{
    private static readonly Dictionary<(TrayState, int), Icon> Cache = new();

    public static Icon Get(TrayState state, int size)
    {
        if (Cache.TryGetValue((state, size), out var cached)) return cached;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var color = Theme.ColorOf(state);
            var pad = Math.Max(1f, size / 16f);
            var rect = new RectangleF(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, rect);
            using (var ring = new Pen(Color.FromArgb(200, 12, 14, 18), Math.Max(1f, size / 14f))) g.DrawEllipse(ring, rect);
            // A small "V" for VMP, white on the colour; skipped at tiny sizes where it would only be noise.
            if (size >= 16)
            {
                using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("V", font, text, new RectangleF(0, size * 0.03f, size, size), format);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            var icon = (Icon)temp.Clone();
            Cache[(state, size)] = icon;
            return icon;
        }
        finally
        {
            Native.Destroy(handle);
        }
    }
}

/// <summary>A small modal question. With <c>requiredText</c> the OK button stays disabled until that exact text is typed.</summary>
internal sealed class PromptForm : Form
{
    public PromptForm(string title, string message, string okText, string requiredText = null, bool danger = false)
    {
        SuspendLayout();
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, requiredText == null ? 150 : 200);
        Native.DarkTitleBar(this);

        var label = new Label { Text = message, Location = new Point(18, 16), Size = new Size(424, requiredText == null ? 80 : 90), ForeColor = Theme.Text };
        Controls.Add(label);
        var ok = Ui.Button(okText, danger ? ButtonKind.Danger : ButtonKind.Primary, 110);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        var y = ClientSize.Height - 46;
        ok.Location = new Point(ClientSize.Width - 18 - 110 - 8 - 90, y);
        cancel.Location = new Point(ClientSize.Width - 18 - 90, y);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        if (requiredText != null)
        {
            var box = Ui.TextBox(424);
            box.Location = new Point(18, y - 38);
            ok.Enabled = false;
            box.TextChanged += (_, _) => ok.Enabled = box.Text == requiredText;
            Controls.Add(box);
            ActiveControl = box;
        }

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
    }
}
