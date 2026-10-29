using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VmpControl.Core;

namespace VmpControl.UI;

// Scaling strategy (one, everywhere):
//  * Every Form uses AutoScaleMode.Dpi and is authored in 96-dpi units; Windows Forms scales the tree once, when the form is first laid out.
//  * Sizes come from AutoSize rows/columns and Dock/Anchor, not from pixel positions. The few constants (Padding, Margin, MinimumSize) are units.
//  * Controls created after that first layout (the Settings page, its rows, dialogs' late parts) go through Ui.ScaleLate exactly once.
//  * Custom painting and GetPreferredSize overrides multiply their units by Ui.Dp(control, units), i.e. by DeviceDpi / 96.
//  * Fonts have a role (FontRole); Ui.ApplyDpi re-applies the role fonts, grid row heights and list item heights for the window's current DPI.
//    It runs after the first layout and again whenever the window moves to a monitor with another scale (DpiChanged).

internal enum FontRole
{
    Base,
    Bold,
    Big,
    Small,
    Mono,
}

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

    private static readonly Dictionary<(FontRole, int), Font> FontCache = new();

    private static (string Family, float Points) Spec(FontRole role) => role switch
    {
        FontRole.Bold => ("Segoe UI Semibold", 9.5f),
        FontRole.Big => ("Segoe UI Semibold", 15f),
        FontRole.Small => ("Segoe UI", 8.5f),
        FontRole.Mono => ("Consolas", 9.5f),
        _ => ("Segoe UI", 9.5f),
    };

    /// <summary>The font of a role for a window of this DPI (cached; never disposed, there are only a handful).</summary>
    public static Font Get(FontRole role, int dpi)
    {
        var key = (role, dpi);
        if (FontCache.TryGetValue(key, out var cached)) return cached;
        var (family, points) = Spec(role);
        var font = new Font(family, DpiMath.FontPoints(points, dpi, Native.SystemDpi));
        FontCache[key] = font;
        return font;
    }

    public static Font Base => Get(FontRole.Base, Native.SystemDpi);
    public static Font Bold => Get(FontRole.Bold, Native.SystemDpi);
    public static Font Big => Get(FontRole.Big, Native.SystemDpi);
    public static Font Small => Get(FontRole.Small, Native.SystemDpi);
    public static Font Mono => Get(FontRole.Mono, Native.SystemDpi);

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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

    private static int _systemDpi;

    /// <summary>The DPI the process was started with (96 when DPI-unaware). A Font in points becomes pixels at this DPI whatever monitor the window is on.</summary>
    public static int SystemDpi
    {
        get
        {
            if (_systemDpi == 0)
            {
                try
                {
                    _systemDpi = (int)GetDpiForSystem();
                }
                catch (EntryPointNotFoundException)
                {
                    _systemDpi = 0;
                }

                if (_systemDpi <= 0) _systemDpi = 96;
            }

            return _systemDpi;
        }
    }

    public static int WindowDpi(Control control)
    {
        if (!control.IsHandleCreated) return control.DeviceDpi;
        var dpi = (int)GetDpiForWindow(control.Handle);
        return dpi > 0 ? dpi : control.DeviceDpi;
    }

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

/// <summary>A control that keeps numbers of its own (padding, input limits) and recomputes them when the window's DPI is known or changes.</summary>
internal interface IDpiAware
{
    void ApplyDpi(int dpi);
}

internal enum ButtonKind
{
    Normal,
    Primary,
    Danger,
}

/// <summary>
/// A label that wraps to the width its parent (or the table column it sits in) offers instead of running off the edge. Its MaximumSize is bound to that width and
/// recomputed whenever the parent is resized, so the layout reserves exactly the height the wrapped text needs.
/// </summary>
internal class WrapLabel : Label
{
    private Control _watched;

    /// <summary>False for a label that is sized by hand (the dialog message): then no maximum is managed here.</summary>
    public bool AutoWidth { get; set; } = true;

    public WrapLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
        Margin = Padding.Empty;
    }

    /// <summary>The width of the cell this label sits in, less its margins. A percent column is worked out from the table's current width, not from the last layout.</summary>
    private int Available()
    {
        if (Parent == null) return 0;
        var total = Parent.ClientSize.Width - Parent.Padding.Horizontal;
        if (Parent is TableLayoutPanel table)
        {
            var position = table.GetPositionFromControl(this);
            var widths = table.GetColumnWidths();
            var styles = table.ColumnStyles;
            if (position.Column >= 0 && position.Column < widths.Length)
            {
                float fixedSum = 0, percentTotal = 0;
                for (var i = 0; i < widths.Length; i++)
                {
                    var percent = i < styles.Count && styles[i].SizeType == SizeType.Percent;
                    if (percent) percentTotal += styles[i].Width;
                    else fixedSum += widths[i];
                }

                var remaining = Math.Max(0, total - fixedSum);
                float cell = 0;
                var span = Math.Max(1, table.GetColumnSpan(this));
                for (var i = position.Column; i < Math.Min(widths.Length, position.Column + span); i++)
                {
                    var percent = i < styles.Count && styles[i].SizeType == SizeType.Percent;
                    cell += percent && percentTotal > 0 ? remaining * styles[i].Width / percentTotal : widths[i];
                }

                total = (int)cell;
            }
        }

        return total > 0 ? TextWrap.WrapWidth(total, Margin.Horizontal, 20) : 0;
    }

    private void UpdateLimit()
    {
        if (!AutoWidth) return;
        var width = Available();
        var limit = width > 0 ? new Size(width, 0) : Size.Empty;
        if (MaximumSize != limit) MaximumSize = limit;
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (_watched != null) _watched.SizeChanged -= OnWatchedSizeChanged;
        _watched = Parent;
        if (_watched != null) _watched.SizeChanged += OnWatchedSizeChanged;
        UpdateLimit();
    }

    private void OnWatchedSizeChanged(object sender, EventArgs e) => UpdateLimit();

    protected override void OnMarginChanged(EventArgs e)
    {
        base.OnMarginChanged(e);
        UpdateLimit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _watched != null) _watched.SizeChanged -= OnWatchedSizeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>A column of full-width rows: <see cref="Auto"/> rows take the height they need, one <see cref="Fill"/> row takes the rest.</summary>
internal sealed class VStack : TableLayoutPanel
{
    public VStack()
    {
        Dock = DockStyle.Fill;
        ColumnCount = 1;
        BackColor = Theme.Bg;
        Margin = Padding.Empty;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public T Auto<T>(T control, int gapAbove = 0) where T : Control => Add(control, new RowStyle(SizeType.AutoSize), gapAbove);

    public T Fill<T>(T control, int gapAbove = 0) where T : Control => Add(control, new RowStyle(SizeType.Percent, 100), gapAbove);

    private T Add<T>(T control, RowStyle style, int gapAbove) where T : Control
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, gapAbove, 0, 0);
        var row = RowCount;
        RowCount = row + 1;
        RowStyles.Add(style);
        Controls.Add(control, 0, row);
        return control;
    }
}

internal static class Ui
{
    private static readonly ConditionalWeakTable<Control, object> Roles = new();
    private static readonly ConditionalWeakTable<Control, int[]> Limits = new(); // min width, max width, min height (units)

    // ----- DPI -----

    /// <summary>The DPI of the window a control sits in (the control's own DeviceDpi while it is not in a window yet).</summary>
    public static int DpiOf(Control control)
    {
        var form = control.FindForm();
        if (form != null) return form.DeviceDpi;
        return CurrentDpi > 0 ? CurrentDpi : control.DeviceDpi;
    }

    /// <summary>The DPI of the main window, kept up to date by it. A control that is created before it is in a window (a late-built settings row) takes its metrics from this.</summary>
    public static int CurrentDpi { get; set; }

    public static int Dp(Control control, int units) => DpiMath.Scale(units, DpiOf(control));

    public static float Factor(Control control) => DpiMath.Factor(DpiOf(control));

    /// <summary>
    /// Scales a control tree that was authored in 96-dpi units and added after its window's first layout (which already scaled everything that existed then).
    /// Call it exactly once per tree, before adding it to its parent.
    /// </summary>
    public static void ScaleLate(Control built, Control host)
    {
        var factor = Factor(host);
        if (Math.Abs(factor - 1f) < 0.01f) return;
        built.Scale(new SizeF(factor, factor));
    }

    /// <summary>Gives a control a font role; <see cref="ApplyDpi"/> keeps the font right for the monitor the window is on.</summary>
    public static T Style<T>(T control, FontRole role) where T : Control
    {
        Roles.AddOrUpdate(control, role);
        control.Font = Theme.Get(role, DpiOf(control));
        return control;
    }

    /// <summary>A minimum width and/or height in units; kept right for the monitor by <see cref="ApplyDpi"/> (a MinimumSize is not scaled reliably on its own).</summary>
    public static T MinSize<T>(T control, int widthUnits, int heightUnits = 0) where T : Control
    {
        var limits = Limits.GetValue(control, _ => new int[3]);
        limits[0] = widthUnits;
        limits[2] = heightUnits;
        control.MinimumSize = new Size(widthUnits, heightUnits);
        return control;
    }

    /// <summary>A maximum width in units (see <see cref="MinSize"/>).</summary>
    public static T MaxWidth<T>(T control, int widthUnits) where T : Control
    {
        var limits = Limits.GetValue(control, _ => new int[3]);
        limits[1] = widthUnits;
        control.MaximumSize = new Size(widthUnits, control.MaximumSize.Height);
        return control;
    }

    /// <summary>Re-applies role fonts, grid and list metrics to a control tree for this DPI (0 = the DPI of the window the root is in), then lays it out.</summary>
    public static void ApplyDpi(Control root, int dpi = 0)
    {
        if (dpi <= 0) dpi = DpiOf(root);
        root.SuspendLayout();
        try
        {
            Walk(root, dpi);
        }
        finally
        {
            root.ResumeLayout(true);
        }
    }

    private static void Walk(Control control, int dpi)
    {
        if (Roles.TryGetValue(control, out var role))
        {
            var font = Theme.Get((FontRole)role, dpi);
            if (!ReferenceEquals(control.Font, font)) control.Font = font;
        }

        if (Limits.TryGetValue(control, out var limits))
        {
            if (limits[0] > 0 || limits[2] > 0) control.MinimumSize = new Size(DpiMath.Scale(limits[0], dpi), DpiMath.Scale(limits[2], dpi));
            if (limits[1] > 0) control.MaximumSize = new Size(DpiMath.Scale(limits[1], dpi), control.MaximumSize.Height);
        }

        if (control is IDpiAware aware) aware.ApplyDpi(dpi);

        switch (control)
        {
            case DataGridView grid:
                FitGrid(grid, dpi);
                break;
            case ComboBox { DrawMode: DrawMode.OwnerDrawFixed } combo:
                var itemHeight = TextHeight(combo.Font) + DpiMath.Scale(Spacing.Xs, dpi);
                if (combo.ItemHeight != itemHeight)
                {
                    combo.ItemHeight = itemHeight;
                    // The box's height follows its item height; tell the table it sits in that its preferred height changed.
                    combo.Parent?.PerformLayout(combo, nameof(ComboBox.ItemHeight));
                }

                break;
            case ListBox { DrawMode: DrawMode.OwnerDrawFixed } list:
                list.ItemHeight = TextHeight(list.Font) + DpiMath.Scale(Spacing.M, dpi);
                break;
        }

        foreach (Control child in control.Controls) Walk(child, dpi);
    }

    public static int TextHeight(Font font) => TextRenderer.MeasureText("Ag", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;

    // ----- factories -----

    public static Button Button(string text, ButtonKind kind = ButtonKind.Normal, int minWidth = 0)
    {
        var back = kind switch { ButtonKind.Primary => Theme.Accent, ButtonKind.Danger => Color.FromArgb(150, 52, 58), _ => Theme.Panel2 };
        var hover = kind switch { ButtonKind.Primary => Color.FromArgb(104, 160, 255), ButtonKind.Danger => Color.FromArgb(190, 66, 74), _ => Color.FromArgb(48, 54, 68) };
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = kind == ButtonKind.Primary ? Color.White : Theme.Text,
            Margin = new Padding(0, 0, Spacing.S, 0),
            Padding = new Padding(Spacing.M, 0, Spacing.M, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        Style(button, FontRole.Base);
        MinSize(button, minWidth, 30);
        button.FlatAppearance.BorderColor = kind == ButtonKind.Normal ? Theme.Border : back;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = back;
        return button;
    }

    /// <summary>A one-line label (it never wraps). Use <see cref="Wrap"/> for text that can be long.</summary>
    public static Label Label(string text, bool muted = false, FontRole role = FontRole.Base)
    {
        var label = new Label { Text = text, AutoSize = true, UseMnemonic = false, ForeColor = muted ? Theme.Muted : Theme.Text, BackColor = Color.Transparent, Margin = Padding.Empty };
        return Style(label, role);
    }

    /// <summary>A label that wraps to the available width.</summary>
    public static WrapLabel Wrap(string text, bool muted = false, FontRole role = FontRole.Base)
    {
        var label = new WrapLabel { Text = text, ForeColor = muted ? Theme.Muted : Theme.Text, BackColor = Color.Transparent };
        return Style(label, role);
    }

    public static TextBox TextBox(int width = 0)
    {
        var box = new TextBox { BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        if (width > 0)
        {
            box.Width = width;
            MinSize(box, width);
        }

        return Style(box, FontRole.Base);
    }

    public static ComboBox Combo(int width)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            Width = width,
            Margin = Padding.Empty,
            DrawMode = DrawMode.OwnerDrawFixed,
        };
        Style(combo, FontRole.Base);
        MinSize(combo, width);
        combo.ItemHeight = TextHeight(combo.Font) + DpiMath.Scale(Spacing.Xs, DpiOf(combo));
        // Owner-drawn so the closed box and the list are dark (a flat DropDownList is white otherwise).
        combo.DrawItem += (_, e) =>
        {
            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using var back = new SolidBrush(selected ? Color.FromArgb(46, 62, 94) : Theme.Panel2);
            e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index < 0) return;
            var inset = Dp(combo, 4);
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, new Rectangle(e.Bounds.X + inset, e.Bounds.Y, e.Bounds.Width - inset, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        return combo;
    }

    /// <summary>
    /// A row of controls, each in its own column and vertically centred. The <paramref name="stretch"/> control (if any) takes the free width; the others take what they need,
    /// separated by one small gap.
    /// </summary>
    public static TableLayoutPanel Bar(Control[] items, Control stretch = null)
    {
        var bar = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = items.Length, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var last = i == items.Length - 1;
            item.Margin = new Padding(0, 0, last ? 0 : Spacing.S, 0);
            item.Anchor = ReferenceEquals(item, stretch) ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            bar.ColumnStyles.Add(ReferenceEquals(item, stretch) ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(item, i, 0);
        }

        return bar;
    }

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            BackgroundColor = Theme.Panel,
            GridColor = Theme.Border,
            ScrollBars = ScrollBars.Vertical,
            ShowCellToolTips = true,
        };
        Style(grid, FontRole.Base);
        grid.DefaultCellStyle.BackColor = Theme.Panel;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(46, 62, 94);
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(31, 34, 42);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Panel2;
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
        Native.DarkScrollbars(grid);
        FitGrid(grid, DpiOf(grid));
        return grid;
    }

    /// <summary>
    /// A DataGridView does not scale its row heights, header height, cell padding or column minimum with the form, so they come from the font and the DPI.
    /// </summary>
    public static void FitGrid(DataGridView grid, int dpi)
    {
        var font = Theme.Get(FontRole.Base, dpi);
        var bold = Theme.Get(FontRole.Bold, dpi);
        grid.Font = font;
        grid.ColumnHeadersDefaultCellStyle.Font = bold;
        var textHeight = TextHeight(font);
        var row = DpiMath.RowHeight(textHeight, dpi);
        grid.RowTemplate.Height = row;
        grid.ColumnHeadersHeight = DpiMath.HeaderHeight(textHeight, dpi);
        var padding = new Padding(DpiMath.Scale(Spacing.Xs, dpi), 0, DpiMath.Scale(Spacing.Xs, dpi), 0);
        grid.DefaultCellStyle.Padding = padding;
        grid.ColumnHeadersDefaultCellStyle.Padding = padding;
        foreach (DataGridViewColumn column in grid.Columns) column.MinimumWidth = DpiMath.Scale(40, dpi);
        if (grid.Rows.Count > 0 && grid.Rows.Count <= 600 && grid.Rows[0].Height != row)
        {
            foreach (DataGridViewRow r in grid.Rows) r.Height = row;
        }
    }

    /// <summary>Adds text columns. The weight is the column's share of the width; numbers are right-aligned.</summary>
    public static void Columns(DataGridView grid, params (string Header, float Weight, bool Right)[] columns)
    {
        foreach (var (header, weight, right) in columns)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = Math.Max(1, weight), SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = DpiMath.Scale(40, DpiOf(grid)) };
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
        ScrollTo(grid, first);
        if (selected >= 0 && selected < grid.Rows.Count) grid.Rows[selected].Selected = true;
        grid.ResumeLayout();
    }

    /// <summary>
    /// Scrolls a grid so that the row is the first displayed one. A grid on a tab that is not showing (or still laid out with no
    /// height) has "no room to display rows" and the property throws; the scroll position does not matter then, so skip it.
    /// </summary>
    public static void ScrollTo(DataGridView grid, int index)
    {
        if (index < 0 || index >= grid.Rows.Count) return;
        if (!grid.Visible || grid.ClientSize.Height <= grid.ColumnHeadersHeight + grid.RowTemplate.Height) return;
        try
        {
            grid.FirstDisplayedScrollingRowIndex = index;
        }
        catch (InvalidOperationException)
        {
            // Still no room (for example while the window is being resized): leave the position alone.
        }
    }

    /// <summary>A muted, bold, upper-case caption above a block of controls.</summary>
    public static Label Heading(string text) => Style(new WrapLabel { Text = text, ForeColor = Theme.Muted, BackColor = Color.Transparent }, FontRole.Bold);

    /// <summary>A heading over a body that takes the rest of the height.</summary>
    public static VStack Section(string heading, Control body, out Label label)
    {
        label = Heading(heading);
        var stack = new VStack { Margin = new Padding(0, 0, Spacing.S, 0) };
        stack.Auto(label);
        label.Margin = new Padding(0, 0, 0, Spacing.Xs);
        stack.Fill(body);
        return stack;
    }
}

/// <summary>A caption with a big value under it. Both wrap rather than being cut off, so a narrow window makes a taller tile instead of a clipped one.</summary>
internal sealed class StatCard : TableLayoutPanel
{
    private readonly WrapLabel _value;

    public StatCard(string caption)
    {
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        BackColor = Theme.Panel;
        Margin = Padding.Empty;
        Padding = new Padding(Spacing.M, Spacing.S, Spacing.M, Spacing.S);

        var captionLabel = Ui.Wrap(caption, true, FontRole.Small);
        captionLabel.Dock = DockStyle.Fill;
        captionLabel.Margin = new Padding(0, 0, 0, Spacing.Xs);
        _value = Ui.Wrap("-", false, FontRole.Big);
        _value.Dock = DockStyle.Fill;
        Controls.Add(captionLabel, 0, 0);
        Controls.Add(_value, 0, 1);
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

/// <summary>The coloured status dot in the header.</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Theme.Grey;

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = true;
        Margin = new Padding(0, 0, Spacing.M, 0);
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var side = Ui.Dp(this, 18);
        return new Size(side, side);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
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
        AutoSize = true;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Ui.Dp(this, 260), Ui.Dp(this, 46));

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
            var font = Theme.Get(FontRole.Small, Ui.DpiOf(this));
            TextRenderer.DrawText(g, "collecting...", font, new Point(Ui.Dp(this, 8), (Height - Ui.TextHeight(font)) / 2), Theme.Muted, TextFormatFlags.NoPadding);
        }
    }

    private void Draw(Graphics g, List<double> series, double max, Color color)
    {
        if (series.Count < 2) return;
        var scale = Ui.Factor(this);
        var inset = 3 * scale;
        var width = Width - 2 * inset - 1;
        var height = Height - 2 * inset - 1;
        var points = new PointF[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            var x = inset + width * (i + MaxPoints - series.Count) / (MaxPoints - 1);
            var y = Height - inset - (float)(series[i] / max) * height;
            points[i] = new PointF(x, y);
        }

        using var pen = new Pen(color, Math.Max(1.5f, 1.6f * scale));
        g.DrawLines(pen, points);
    }
}

/// <summary>A row of flat tab buttons with an underline on the selected one.</summary>
internal sealed class TabBar : FlowLayoutPanel
{
    private readonly List<Button> _buttons = new();
    public event Action<int> SelectedChanged;
    public int Selected { get; private set; } = -1;

    public TabBar()
    {
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        FlowDirection = FlowDirection.LeftToRight;
        BackColor = Theme.Bg;
        Margin = Padding.Empty;
        Padding = new Padding(Spacing.L - Spacing.M, 0, Spacing.L, 0);
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
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Spacing.M, Spacing.S, Spacing.M, Spacing.S + Spacing.Xs),
            Margin = Padding.Empty,
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false,
        };
        Ui.Style(button, FontRole.Bold);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Panel;
        button.FlatAppearance.MouseDownBackColor = Theme.Panel2;
        button.Click += (_, _) => Select(index);
        button.Paint += (_, e) =>
        {
            if (Selected != index) return;
            var inset = Ui.Dp(button, Spacing.S + 2);
            var thickness = Math.Max(2, Ui.Dp(button, 3));
            using var brush = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(brush, inset, button.Height - thickness, Math.Max(1, button.Width - 2 * inset), thickness);
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

/// <summary>
/// The shell of every dialog the app builds: dark, fixed-size-to-content (AutoSize, grow and shrink), a message that scrolls instead of making the dialog taller than
/// 70% of the working area, buttons right-aligned in a bottom bar, centred on its owner. Authored in 96-dpi units like the main window.
/// </summary>
internal abstract class DialogBase : Form
{
    private readonly TableLayoutPanel _root = new();
    private readonly FlowLayoutPanel _buttons = new();
    private readonly int _contentWidth;
    private Panel _scroll;
    private WrapLabel _message;
    private int _messageRow = -1;

    protected DialogBase(string title, int contentWidthUnits)
    {
        _contentWidth = contentWidthUnits;
        SuspendLayout();
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Ui.Style(this, FontRole.Base);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Spacing.L);
        Native.DarkTitleBar(this);

        _root.AutoSize = true;
        _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.BackColor = Theme.Bg;
        _root.Margin = Padding.Empty;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, contentWidthUnits));

        _buttons.AutoSize = true;
        _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;
        _buttons.BackColor = Theme.Bg;
        _buttons.Anchor = AnchorStyles.Right;
        _buttons.Margin = new Padding(0, Spacing.L, 0, 0);
        Controls.Add(_root);
        ResumeLayout(false);
    }

    /// <summary>The message: wraps to the dialog width and scrolls when it is longer than the dialog may be.</summary>
    protected void AddMessage(string text)
    {
        _message = new WrapLabel { Text = text ?? string.Empty, ForeColor = Theme.Text, BackColor = Theme.Bg, Dock = DockStyle.None, AutoWidth = false, AutoSize = false };
        Ui.Style(_message, FontRole.Base);
        _scroll = new Panel { AutoScroll = true, Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };
        _scroll.HorizontalScroll.Visible = false;
        Native.DarkScrollbars(_scroll);
        _scroll.Controls.Add(_message);
        _messageRow = AddRow(_scroll, new RowStyle(SizeType.Absolute, 60));
    }

    protected int AddRow(Control control, RowStyle style, int gapAbove = 0)
    {
        var row = _root.RowCount;
        _root.RowCount = row + 1;
        _root.RowStyles.Add(style);
        control.Margin = new Padding(0, gapAbove, 0, 0);
        _root.Controls.Add(control, 0, row);
        return row;
    }

    protected void AddContent(Control control, int gapAbove = 0)
    {
        control.Dock = DockStyle.Fill;
        AddRow(control, new RowStyle(SizeType.AutoSize), gapAbove);
    }

    /// <summary>The last row: the OK (first) and Cancel buttons, right-aligned. Call once, after the content.</summary>
    protected void AddButtons(Button ok, Button cancel)
    {
        ok.Margin = new Padding(0, 0, Spacing.S, 0);
        cancel.Margin = Padding.Empty;
        _buttons.Controls.Add(cancel);
        _buttons.Controls.Add(ok);
        var row = _root.RowCount;
        _root.RowCount = row + 1;
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(_buttons, 0, row);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Fit();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated) BeginInvoke(new Action(Fit));
    }

    private Screen TargetScreen() => Owner != null ? Screen.FromControl(Owner) : Screen.FromPoint(Cursor.Position);

    protected virtual void Fit()
    {
        Ui.ApplyDpi(this);
        _root.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, Ui.Dp(this, _contentWidth));
        if (_scroll != null) FitMessage();
        PerformLayout();
        var area = TargetScreen().WorkingArea;
        var parent = Owner != null && Owner.Visible ? Owner.Bounds : area;
        var (x, y) = DialogSizing.CenterOn(parent.X, parent.Y, parent.Width, parent.Height, Width, Height, area.X, area.Y, area.Width, area.Height);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(x, y);
    }

    private void FitMessage()
    {
        var width = Ui.Dp(this, _contentWidth);
        var natural = _message.GetPreferredSize(new Size(width, 0)).Height;
        _root.RowStyles[_messageRow] = new RowStyle(SizeType.Absolute, natural);
        _message.SetBounds(0, 0, width, natural);
        PerformLayout();
        var maxHeight = DialogSizing.MaxHeight(TargetScreen().WorkingArea.Height);
        var fixedHeight = GetPreferredSize(Size.Empty).Height - natural;
        var height = DialogSizing.ListHeight(natural, fixedHeight, maxHeight, Ui.Dp(this, 96));
        if (height < natural)
        {
            // Scrolling: the text gives up the width of the scrollbar so it does not need a horizontal one.
            var textWidth = Math.Max(Ui.Dp(this, 100), width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi));
            var wrapped = _message.GetPreferredSize(new Size(textWidth, 0)).Height;
            _message.SetBounds(0, 0, textWidth, wrapped);
            _root.RowStyles[_messageRow] = new RowStyle(SizeType.Absolute, height);
        }
    }
}

/// <summary>A small modal question. With <c>requiredText</c> the OK button stays disabled until that exact text is typed.</summary>
internal sealed class PromptForm : DialogBase
{
    public PromptForm(string title, string message, string okText, string requiredText = null, bool danger = false) : base(title, 460)
    {
        SuspendLayout();
        AddMessage(message);
        var ok = Ui.Button(okText, danger ? ButtonKind.Danger : ButtonKind.Primary, 110);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        if (requiredText != null)
        {
            var box = Ui.TextBox();
            box.Dock = DockStyle.Fill;
            ok.Enabled = false;
            box.TextChanged += (_, _) => ok.Enabled = box.Text == requiredText;
            AddContent(box, Spacing.M);
            ActiveControl = box;
        }

        AddButtons(ok, cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
    }
}
