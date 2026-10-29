using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using VmpControl.Core;

namespace VmpControl.UI;

/// <summary>An on/off switch (a bool setting).</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _on;

    public event EventHandler CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AutoSize = true;
        Margin = Padding.Empty;
    }

    public bool Checked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Ui.Dp(this, 96), Ui.Dp(this, 24));

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Enabled) Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && Enabled) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var edge = Math.Max(1, Ui.Dp(this, 1));
        var trackH = Math.Max(10, Height - Ui.Dp(this, 4));
        var trackW = (int)(trackH * 1.9f);
        var track = new Rectangle(edge, (Height - trackH) / 2, trackW, trackH);
        var dim = !Enabled;
        using (var path = new GraphicsPath())
        {
            path.AddArc(track.X, track.Y, trackH, trackH, 90, 180);
            path.AddArc(track.Right - trackH, track.Y, trackH, trackH, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(_on ? (dim ? Color.FromArgb(60, 80, 120) : Theme.Accent) : Theme.Panel2);
            g.FillPath(fill, path);
            using var outline = new Pen(_on ? fill.Color : Theme.Border);
            g.DrawPath(outline, path);
        }

        var inset = Ui.Dp(this, 3);
        var knob = trackH - 2 * inset;
        var knobX = _on ? track.Right - knob - inset : track.X + inset;
        using (var white = new SolidBrush(dim ? Color.FromArgb(150, 156, 168) : Color.White)) g.FillEllipse(white, knobX, track.Y + inset, knob, knob);
        if (Focused && ShowFocusCues) g.DrawRectangle(Pens.Gray, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, _on ? "On" : "Off", Font, new Point(track.Right + Ui.Dp(this, Spacing.S), (Height - Ui.TextHeight(Font)) / 2), dim ? Theme.Muted : Theme.Text, TextFormatFlags.NoPadding);
    }
}

/// <summary>Asks for the new value of a secret setting. The text box masks what is typed; the value goes to the caller once and is wiped from the dialog.</summary>
internal sealed class SecretPromptForm : DialogBase
{
    private readonly TextBox _box = Ui.TextBox();

    public SecretPromptForm(string title, string message, int? maxLength) : base(title, 420)
    {
        SuspendLayout();
        AddMessage(message);
        _box.Dock = DockStyle.Fill;
        _box.UseSystemPasswordChar = true;
        if (maxLength is > 0) _box.MaxLength = maxLength.Value;
        AddContent(_box, Spacing.M);
        var ok = Ui.Button("Use this value", ButtonKind.Primary, 130);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        AddButtons(ok, cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _box;
        ResumeLayout(false);
    }

    public string Value => _box.Text;

    /// <summary>Overwrites the typed text so that it does not linger in the dialog after it is closed.</summary>
    public void Wipe() => _box.Text = new string('*', _box.TextLength);
}

/// <summary>
/// One setting: label, description, the input that matches its type, default hint, modified dot with a reset link, restart badge and the error text. Laid out as two
/// stacks side by side (what it is on the left, how to change it on the right); the row is as tall as the taller stack.
/// </summary>
internal sealed class SettingRow : TableLayoutPanel, IDpiAware
{
    private readonly SettingDto _s;
    private readonly SettingsEditModel _model;
    private readonly ToolTip _tip;
    private readonly bool _readOnly;
    private readonly Label _title, _badge, _state;
    private readonly WrapLabel _hint;
    private readonly WrapLabel _desc, _error, _note;
    private readonly LinkLabel _reset = new();
    private readonly Control _input;
    private readonly Button _change, _clear;
    private bool _syncing;
    private bool _dot, _dirty;
    private (int Min, int Max) _inputWidth;

    public string Key => _s.Key;

    public SettingRow(SettingDto setting, SettingsEditModel model, ToolTip tip, Action<string> changeSecret, Action<string> clearSecret)
    {
        _s = setting;
        _model = model;
        _tip = tip;
        _readOnly = SettingsEditModel.IsReadOnly(setting);
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = new Padding(32, Spacing.M, Spacing.L, Spacing.M);
        ColumnCount = 2;
        RowCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _title = Ui.Label(string.IsNullOrWhiteSpace(setting.Label) ? setting.Key : setting.Label, false, FontRole.Bold);
        _title.ForeColor = _readOnly ? Theme.Muted : Theme.Text;
        _tip.SetToolTip(_title, setting.Key);

        _desc = Ui.Wrap(setting.Description ?? "", true, FontRole.Small);
        _desc.Visible = !string.IsNullOrEmpty(setting.Description);

        _badge = Ui.Label("applies after restart", false, FontRole.Base);
        _badge.ForeColor = Theme.Amber;
        _badge.BackColor = Color.FromArgb(52, 44, 26);
        _badge.Padding = new Padding(Spacing.Xs, 1, Spacing.Xs, 1);
        _badge.Visible = setting.RestartRequired && !_readOnly;

        _hint = Ui.Wrap(setting.Secret ? "no default" : "default: " + Truncate(SettingsEditModel.FormatValue(setting.DefaultValue()), 40), true, FontRole.Small);

        _reset.Text = "Reset to default";
        _reset.AutoSize = true;
        _reset.Margin = Padding.Empty;
        _reset.LinkColor = Theme.Accent;
        _reset.ActiveLinkColor = Color.FromArgb(140, 180, 255);
        _reset.VisitedLinkColor = Theme.Accent;
        _reset.LinkBehavior = LinkBehavior.HoverUnderline;
        _reset.BackColor = Color.Transparent;
        Ui.Style(_reset, FontRole.Base);
        _reset.LinkClicked += (_, _) =>
        {
            _model.ResetToDefault(_s.Key);
            SyncInput();
        };

        _error = Ui.Wrap("", false, FontRole.Base);
        _error.ForeColor = Theme.Red;
        _error.Visible = false;

        _note = Ui.Wrap("edit vmp-server.json and restart", true, FontRole.Small);
        _note.Visible = _readOnly;

        // Left: the name (with the restart badge) over the description.
        var left = Stack();
        var titleLine = Line(_title, _badge);
        titleLine.Margin = Padding.Empty;
        left.Controls.Add(titleLine, 0, 0);
        _desc.Dock = DockStyle.Fill;
        _desc.Margin = new Padding(0, 2, Spacing.M, 0);
        left.Controls.Add(_desc, 0, 1);

        // Right: the input, then the default hint with its reset link, then the read-only note and the error.
        var right = Stack();
        var next = 0;
        if (setting.Secret)
        {
            _state = Ui.Label("", false, FontRole.Bold);
            _change = Ui.Button("Change...", ButtonKind.Normal);
            _clear = Ui.Button("Clear", ButtonKind.Danger);
            _clear.Margin = Padding.Empty;
            _change.Click += (_, _) => changeSecret(_s.Key);
            _clear.Click += (_, _) => clearSecret(_s.Key);
            _change.Enabled = _clear.Enabled = !_readOnly;
            var buttons = Line(_change, _clear);
            _state.Margin = new Padding(0, 0, 0, Spacing.Xs);
            right.Controls.Add(_state, 0, next++);
            right.Controls.Add(buttons, 0, next++);
        }
        else
        {
            _state = null;
            _input = MakeInput();
            _input.Enabled = !_readOnly;
            _input.Margin = Padding.Empty;
            _input.Anchor = _input is ToggleSwitch ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
            right.Controls.Add(_input, 0, next++);
        }

        var hintLine = Line(_hint, _reset, stretchFirst: true);
        hintLine.Margin = new Padding(0, Spacing.Xs, 0, 0);
        right.Controls.Add(hintLine, 0, next++);
        _note.Dock = DockStyle.Fill;
        _note.Margin = new Padding(0, Spacing.Xs, 0, 0);
        right.Controls.Add(_note, 0, next++);
        _error.Dock = DockStyle.Fill;
        _error.Margin = new Padding(0, Spacing.Xs, 0, 0);
        right.Controls.Add(_error, 0, next++);

        // Both stacks take the full width of their column but only the height they need (a taller neighbour must not spread their rows apart).
        left.Anchor = right.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(left, 0, 0);
        Controls.Add(right, 1, 0);
        SyncInput();
        RefreshState();
    }

    /// <summary>A one-column stack whose rows take the height they need.</summary>
    private static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return stack;
    }

    /// <summary>Two controls side by side, each only as wide as it needs, vertically centred on each other.</summary>
    private static TableLayoutPanel Line(Control first, Control second, bool stretchFirst = false)
    {
        var line = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Anchor = stretchFirst ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left };
        line.ColumnStyles.Add(stretchFirst ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        first.Anchor = stretchFirst ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
        first.Margin = new Padding(0, 0, Spacing.S, 0);
        second.Anchor = AnchorStyles.Left;
        second.Margin = Padding.Empty;
        line.Controls.Add(first, 0, 0);
        line.Controls.Add(second, 1, 0);
        return line;
    }

    public void ApplyDpi(int dpi)
    {
        Padding = new Padding(DpiMath.Scale(32, dpi), DpiMath.Scale(Spacing.M, dpi), DpiMath.Scale(Spacing.L, dpi), DpiMath.Scale(Spacing.M, dpi));
        if (_input != null && _inputWidth.Max > 0)
        {
            _input.MinimumSize = new Size(DpiMath.Scale(_inputWidth.Min, dpi), 0);
            _input.MaximumSize = new Size(DpiMath.Scale(_inputWidth.Max, dpi), 0);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "...";

    private Control MakeInput()
    {
        switch (_s.TypeName)
        {
            case "bool":
            {
                var toggle = new ToggleSwitch { BackColor = Theme.Bg };
                Ui.Style(toggle, FontRole.Base);
                toggle.CheckedChanged += (_, _) =>
                {
                    if (!_syncing) _model.Set(_s.Key, toggle.Checked);
                };
                return toggle;
            }
            case "int":
            case "double":
            {
                var isInt = _s.TypeName == "int";
                var places = isInt ? 0 : Math.Clamp(Math.Max(Decimals(_s.ServerValue()), Decimals(_s.DefaultValue())), 1, 6);
                var number = new NumericUpDown
                {
                    BackColor = Theme.Panel2,
                    ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.FixedSingle,
                    DecimalPlaces = places,
                    Increment = isInt ? 1m : (decimal)Math.Pow(10, -places),
                    ThousandsSeparator = false,
                    Minimum = _s.Min.HasValue ? (decimal)_s.Min.Value : (isInt ? int.MinValue : -1_000_000_000m),
                    Maximum = _s.Max.HasValue ? (decimal)_s.Max.Value : (isInt ? int.MaxValue : 1_000_000_000m),
                };
                Ui.Style(number, FontRole.Base);
                _inputWidth = (110, 170);
                number.ValueChanged += (_, _) =>
                {
                    if (_syncing) return;
                    _model.Set(_s.Key, isInt ? (object)(long)number.Value : (double)number.Value);
                };
                return number;
            }
            case "enum":
            {
                var combo = Ui.Combo(120);
                _inputWidth = (120, 230);
                combo.Items.AddRange(_s.Options.Cast<object>().ToArray());
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (!_syncing && combo.SelectedItem is string choice) _model.Set(_s.Key, choice);
                };
                return combo;
            }
            default:
            {
                var box = Ui.TextBox(120);
                _inputWidth = (120, 340);
                if (_s.MaxLength is > 0) box.MaxLength = _s.MaxLength.Value;
                box.TextChanged += (_, _) =>
                {
                    if (!_syncing) _model.Set(_s.Key, box.Text);
                };
                return box;
            }
        }
    }

    private static int Decimals(object v)
    {
        if (v is not double d) return 0;
        var text = d.ToString("0.######", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    /// <summary>Puts the model's current value into the input (after a reset, a discard or a refresh) without sending it back.</summary>
    public void SyncInput()
    {
        if (_input == null) return;
        var value = _model.Effective(_s);
        _syncing = true;
        try
        {
            switch (_input)
            {
                case ToggleSwitch t:
                    t.Checked = value is true;
                    break;
                case NumericUpDown n:
                    var wanted = value == null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    n.Value = Math.Clamp(wanted, n.Minimum, n.Maximum);
                    break;
                case ComboBox c:
                    c.SelectedItem = value as string;
                    break;
                case TextBox b:
                    var text = value as string ?? "";
                    if (b.Text != text) b.Text = text;
                    break;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool InputDiffersFromModel()
    {
        var value = _model.Effective(_s);
        return _input switch
        {
            ToggleSwitch t => t.Checked != (value is true),
            NumericUpDown n => value != null && n.Value != Math.Clamp(Convert.ToDecimal(value, CultureInfo.InvariantCulture), n.Minimum, n.Maximum),
            ComboBox c => !Equals(c.SelectedItem, value),
            TextBox b => b.Text != (value as string ?? ""),
            _ => false,
        };
    }

    /// <summary>Updates what depends on the model: modified dot, reset link, pending tint, secret state, error text. The row's height follows its content by itself.</summary>
    public void RefreshState()
    {
        var dirty = _model.IsDirty(_s.Key);
        var modified = _model.IsModified(_s);
        var error = _model.ErrorFor(_s.Key);

        _reset.Visible = !_readOnly && !_s.Secret && modified;
        if (_s.Secret)
        {
            var state = _model.SecretState(_s);
            _state.Text = state;
            _state.ForeColor = state == "set" ? Theme.Green : state == "not set" ? Theme.Muted : Theme.Amber;
            _clear.Enabled = !_readOnly && state is "set" or "new value entered";
        }

        _error.Text = error ?? "";
        _error.Visible = error != null;
        var tint = dirty ? Color.FromArgb(30, 36, 50) : Theme.Bg;
        if (BackColor != tint)
        {
            BackColor = tint;
            if (_input is ToggleSwitch) _input.BackColor = tint;
        }

        if (_dot != modified || _dirty != dirty)
        {
            _dot = modified;
            _dirty = dirty;
            Invalidate();
        }

        if (_input != null && !_input.Focused && InputDiffersFromModel()) SyncInput();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Math.Max(3, Ui.Dp(this, 4));
        var cy = Padding.Top + Ui.TextHeight(_title.Font) / 2;
        if (_dot)
        {
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, Ui.Dp(this, 16) - r, cy - r, 2 * r, 2 * r);
        }

        if (_dirty)
        {
            using var bar = new SolidBrush(Theme.Amber);
            g.FillRectangle(bar, 0, 0, Math.Max(3, Ui.Dp(this, 3)), Height - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.SetToolTip(_title, null);
        base.Dispose(disposing);
    }
}

/// <summary>
/// The server's settings, from GET /api/v1/settings: sections on the left, one row per setting on the right, edits kept locally until Save sends one PUT
/// with only the changed keys. Controls are created when the tab is first shown, and the rows are disposed whenever the list is rebuilt.
/// </summary>
internal sealed class SettingsPage : Page
{
    private const int MaxRows = 150;
    private readonly SettingsEditModel _model = new();
    private readonly RestartBanner _restart = new();
    private readonly List<Control> _items = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    private ToolTip _tip;
    private TextBox _search;
    private Button _modifiedOnly, _save, _discard, _restartNow;
    private Label _status, _unsaved, _bannerText;
    private TableLayoutPanel _bannerPanel, _host;
    private Panel _list;
    private ListBox _sections;
    private string _sectionId;
    private bool _modifiedOnlyOn, _saving, _syncingList, _ready;
    private int _signature;

    public SettingsPage(PageContext ctx) : base(ctx)
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RebuildRows();
        };
    }

    public override string Title => "Settings";

    private bool Filtering => _search != null && (_search.Text.Trim().Length > 0 || _modifiedOnlyOn);

    protected override void Build()
    {
        _ready = true;
        _tip = new ToolTip();
        _search = Ui.TextBox(260);
        _search.PlaceholderText = "Search label, key or description";
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _modifiedOnly = Ui.Button("Modified only", ButtonKind.Normal);
        _modifiedOnly.Click += (_, _) =>
        {
            _modifiedOnlyOn = !_modifiedOnlyOn;
            _modifiedOnly.BackColor = _modifiedOnlyOn ? Theme.Accent : Theme.Panel2;
            _modifiedOnly.ForeColor = _modifiedOnlyOn ? Color.White : Theme.Text;
            RebuildRows();
        };
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => await LoadAsync(force: true);
        _status = Ui.Wrap("", true);
        var toolbar = Ui.Bar(new Control[] { Ui.Label("Search", true), _search, _modifiedOnly, refresh, _status }, _status);

        _bannerText = Ui.Wrap("", false, FontRole.Bold);
        _bannerText.ForeColor = Color.FromArgb(40, 28, 0);
        _restartNow = Ui.Button("Restart now", ButtonKind.Normal, 120);
        _restartNow.Margin = Padding.Empty;
        _restartNow.Click += async (_, _) => await Ctx.Actions.ControlAsync("Restart");
        _bannerPanel = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Amber, Visible = false, Padding = new Padding(Spacing.M, Spacing.S, Spacing.S, Spacing.S), Margin = Padding.Empty };
        _bannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bannerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _bannerText.Dock = DockStyle.Fill;
        _bannerText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _restartNow.Anchor = AnchorStyles.Right;
        _bannerPanel.Controls.Add(_bannerText, 0, 0);
        _bannerPanel.Controls.Add(_restartNow, 1, 0);

        _unsaved = Ui.Wrap("", false, FontRole.Bold);
        _save = Ui.Button("Save", ButtonKind.Primary, 100);
        _discard = Ui.Button("Discard", ButtonKind.Normal, 100);
        _save.Margin = Padding.Empty;
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) => DiscardAll();
        var bottom = Ui.Bar(new Control[] { _unsaved, _discard, _save }, _unsaved);
        bottom.BackColor = Theme.Panel;
        bottom.Padding = new Padding(Spacing.L, Spacing.S + Spacing.Xs, Spacing.L, Spacing.S + Spacing.Xs);

        _sections = new ListBox
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
        };
        Ui.Style(_sections, FontRole.Base);
        Ui.MinSize(_sections, 200);
        Ui.MaxWidth(_sections, 200);
        _sections.ItemHeight = Ui.TextHeight(_sections.Font) + Ui.Dp(this, Spacing.M);
        _sections.DrawItem += DrawSectionItem;
        _sections.SelectedIndexChanged += (_, _) => OnSectionChosen();
        Native.DarkScrollbars(_sections);

        _host = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        _host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _list = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Margin = new Padding(Spacing.M, 0, 0, 0) };
        _list.Controls.Add(_host);
        Native.DarkScrollbars(_list);

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        middle.Controls.Add(_sections, 0, 0);
        middle.Controls.Add(_list, 1, 0);

        var stack = new VStack();
        stack.Auto(toolbar);
        stack.Auto(_bannerPanel, Spacing.S);
        stack.Fill(middle, Spacing.S);
        stack.Auto(bottom);
        Body.Controls.Add(stack);
        _model.Changed += OnModelChanged;
        UpdateBottom();
        UpdateBanner();
    }

    // ----- loading -----

    private string _pendingSearch;

    /// <summary>Puts this text in the search box the next time the page refreshes (which happens when its tab is selected).</summary>
    public void ShowSearch(string text) => _pendingSearch = text;

    public override async Task RefreshAsync()
    {
        if (_pendingSearch != null && _search != null)
        {
            var text = _pendingSearch;
            _pendingSearch = null;
            _search.Text = text;
        }

        await LoadAsync(force: false);
    }

    private static int Signature(SettingsSchemaDto schema)
    {
        var sb = new StringBuilder();
        foreach (var s in schema.Sections.SelectMany(x => x.Settings))
            sb.Append(s.Key).Append('=').Append(s.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : s.Value.GetRawText()).Append('|').Append(s.IsSet).Append('|').Append(s.Modified).Append(';');
        return sb.ToString().GetHashCode();
    }

    private async Task LoadAsync(bool force)
    {
        if (!_ready) return;
        var result = await Ctx.Session.GetSettingsAsync(Ctx.Cancel);
        if (result.Outcome == ApiOutcome.NotSupported)
        {
            SetNotice("This server needs the update that adds the settings endpoints.");
            return;
        }

        SetNotice(null);
        if (!result.IsOk)
        {
            _status.Text = "Could not load the settings: " + result.Error;
            return;
        }

        _status.Text = "";
        var signature = Signature(result.Value);
        var unchanged = !force && signature == _signature && _model.Schema.Sections.Count > 0;
        _signature = signature;
        _model.Load(result.Value);
        if (unchanged) return;
        RebuildSections();
        RebuildRows();
    }

    private void RebuildSections()
    {
        _syncingList = true;
        _sections.BeginUpdate();
        _sections.Items.Clear();
        foreach (var section in _model.Schema.Sections) _sections.Items.Add(string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title);
        var index = _model.Schema.Sections.FindIndex(x => x.Id == _sectionId);
        if (index < 0 && _model.Schema.Sections.Count > 0) index = 0;
        _sectionId = index >= 0 ? _model.Schema.Sections[index].Id : null;
        _sections.SelectedIndex = index;
        _sections.EndUpdate();
        _syncingList = false;
    }

    private void OnSectionChosen()
    {
        if (_syncingList || _sections.SelectedIndex < 0 || _sections.SelectedIndex >= _model.Schema.Sections.Count) return;
        _sectionId = _model.Schema.Sections[_sections.SelectedIndex].Id;
        if (Filtering)
        {
            // Choosing a section ends the search: the user wants to see that section.
            _modifiedOnlyOn = false;
            _modifiedOnly.BackColor = Theme.Panel2;
            _modifiedOnly.ForeColor = Theme.Text;
            _debounce.Stop();
            _search.Text = "";
        }

        RebuildRows();
    }

    private void DrawSectionItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _model.Schema.Sections.Count) return;
        var section = _model.Schema.Sections[e.Index];
        var dpi = Ui.DpiOf(_sections);
        var selected = (e.State & DrawItemState.Selected) != 0 && !Filtering;
        using (var back = new SolidBrush(selected ? Color.FromArgb(46, 62, 94) : Theme.Panel)) e.Graphics.FillRectangle(back, e.Bounds);
        var errors = _model.ErrorCountIn(section.Id);
        var dirty = _model.DirtyCountIn(section.Id);
        var right = errors > 0 ? "!" : dirty > 0 ? dirty.ToString(CultureInfo.InvariantCulture) : "";
        var rightColor = errors > 0 ? Theme.Red : Theme.Amber;
        var bold = Theme.Get(FontRole.Bold, dpi);
        var rightW = right.Length == 0 ? 0 : TextRenderer.MeasureText(right, bold).Width + DpiMath.Scale(Spacing.S, dpi);
        var left = DpiMath.Scale(Spacing.M, dpi);
        var gap = DpiMath.Scale(Spacing.S, dpi);
        TextRenderer.DrawText(e.Graphics, _sections.Items[e.Index].ToString(), _sections.Font, new Rectangle(e.Bounds.X + left, e.Bounds.Y, Math.Max(1, e.Bounds.Width - left - gap - rightW), e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (right.Length > 0) TextRenderer.DrawText(e.Graphics, right, bold, new Rectangle(e.Bounds.Right - rightW - gap, e.Bounds.Y, rightW, e.Bounds.Height), rightColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    // ----- rows -----

    private void DisposeRows()
    {
        foreach (var c in _items)
        {
            _host.Controls.Remove(c);
            c.Dispose();
        }

        _items.Clear();
        _host.RowStyles.Clear();
        _host.RowCount = 0;
    }

    private void RebuildRows()
    {
        if (!_ready || _model.Schema.Sections.Count == 0) return;
        var scrollTop = _list.VerticalScroll.Value;
        _host.SuspendLayout();
        DisposeRows();

        var filtering = Filtering;
        var matches = _model.Filter(_search.Text, _modifiedOnlyOn, filtering ? null : _sectionId).Take(MaxRows + 1).ToList();
        string lastSection = null;
        foreach (var s in matches.Take(MaxRows))
        {
            if (filtering && s.SectionId != lastSection)
            {
                lastSection = s.SectionId;
                var section = _model.Schema.Sections.FirstOrDefault(x => x.Id == lastSection);
                var heading = Ui.Heading((section?.Title ?? lastSection).ToUpperInvariant());
                heading.Margin = new Padding(Spacing.L, Spacing.M, 0, Spacing.Xs);
                _items.Add(heading);
            }

            _items.Add(new SettingRow(s, _model, _tip, ChangeSecret, ClearSecret));
        }

        if (matches.Count == 0) _items.Add(Message(filtering ? "No setting matches." : "This section has no settings."));
        else if (matches.Count > MaxRows) _items.Add(Message($"Only the first {MaxRows} matches are shown. Narrow the search to see the rest."));

        // These controls are built after the window's first layout: scale them once, then add them.
        var dpi = Ui.DpiOf(this);
        foreach (var c in _items)
        {
            Ui.ScaleLate(c, this);
            c.Dock = DockStyle.Fill;
            var row = _host.RowCount;
            _host.RowCount = row + 1;
            _host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _host.Controls.Add(c, 0, row);
        }

        _host.ResumeLayout(false);
        Ui.ApplyDpi(_host, dpi);
        _sections.Invalidate();
        try
        {
            _list.VerticalScroll.Value = Math.Min(scrollTop, Math.Max(0, _list.VerticalScroll.Maximum));
            _list.PerformLayout();
        }
        catch (ArgumentException)
        {
        }
    }

    private static Label Message(string text)
    {
        var label = Ui.Wrap(text, true);
        label.Margin = new Padding(Spacing.L, Spacing.M, 0, 0);
        return label;
    }

    private void OnModelChanged()
    {
        if (!_ready) return;
        foreach (var c in _items)
        {
            if (c is SettingRow row) row.RefreshState();
        }

        _sections.Invalidate();
        UpdateBottom();
        // With "Modified only" on, a row that is reset to its default stays until the next rebuild, so the row being edited never jumps away.
    }

    // ----- secrets -----

    private void ChangeSecret(string key)
    {
        var s = _model.Find(key);
        if (s == null) return;
        using var dialog = new SecretPromptForm("Change " + s.Label, "Enter the new value for \"" + s.Label + "\". It is sent to the server when you press Save and is never shown here.", s.MaxLength);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) _model.Set(key, dialog.Value);
        dialog.Wipe();
    }

    private void ClearSecret(string key)
    {
        var s = _model.Find(key);
        if (s == null) return;
        using var dialog = new PromptForm("Clear " + s.Label, "This removes \"" + s.Label + "\" on the server when you press Save. Press Discard to take it back before that.", "Clear", null, danger: true);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) _model.ClearSecret(key);
    }

    // ----- bottom bar, banner -----

    private void UpdateBottom()
    {
        if (_unsaved == null) return;
        var n = _model.DirtyCount;
        var problems = _model.ErrorCount;
        _unsaved.Text = (n == 0 ? "No unsaved changes" : n == 1 ? "1 unsaved change" : n + " unsaved changes") + (problems > 0 ? $"  -  {problems} problem{(problems == 1 ? "" : "s")} to fix" : "");
        _unsaved.ForeColor = problems > 0 ? Theme.Red : n > 0 ? Theme.Amber : Theme.Muted;
        _save.Enabled = n > 0 && !_saving;
        _discard.Enabled = n > 0 && !_saving;
    }

    private void UpdateBanner()
    {
        if (_bannerPanel == null) return;
        var visible = _restart.Visible;
        if (visible) _bannerText.Text = _restart.Text;
        if (_bannerPanel.Visible != visible) _bannerPanel.Visible = visible;
    }

    public override void ObserveSnapshot(PollSnapshot s)
    {
        if (s.Api != ApiAvailability.Ok || s.Status == null) return;
        _restart.OnStatus(s.Status.StartedUtc);
        UpdateBanner();
    }

    // ----- save / discard -----

    private void DiscardAll()
    {
        _model.DiscardAll();
        foreach (var c in _items)
        {
            if (c is SettingRow row) row.SyncInput();
        }

        _status.Text = "";
    }

    private void ShowFirstError()
    {
        var key = _model.Errors.Keys.FirstOrDefault();
        var s = key == null ? null : _model.Find(key);
        if (s == null) return;
        var index = _model.Schema.Sections.FindIndex(x => x.Id == s.SectionId);
        if (index < 0) return;
        var needsRebuild = Filtering && !_model.Filter(_search.Text, _modifiedOnlyOn, null).Contains(s);
        if (needsRebuild)
        {
            _modifiedOnlyOn = false;
            _modifiedOnly.BackColor = Theme.Panel2;
            _modifiedOnly.ForeColor = Theme.Text;
            _debounce.Stop();
            _search.Text = "";
        }

        if (_sectionId != s.SectionId || needsRebuild)
        {
            _sectionId = s.SectionId;
            _syncingList = true;
            _sections.SelectedIndex = index;
            _syncingList = false;
            RebuildRows();
        }
    }

    private async Task SaveAsync()
    {
        if (_saving || _model.DirtyCount == 0) return;
        var problems = _model.ValidateAll();
        if (problems > 0)
        {
            _status.Text = $"Fix {problems} problem{(problems == 1 ? "" : "s")} first; nothing was sent.";
            ShowFirstError();
            return;
        }

        _saving = true;
        _status.Text = "Saving...";
        UpdateBottom();
        try
        {
            var changes = _model.BuildChanges();
            var result = await Ctx.Session.PutSettingsAsync(changes, Ctx.Cancel);
            if (result.IsOk)
            {
                var saved = result.Value;
                // Only the keys go into the activity log, never the values (some of them are secrets).
                Ctx.Activity("Settings", SettingsEditModel.SaveSummary(saved));
                _restart.OnSaved(saved.RestartRequired, Ctx.Latest?.Status?.StartedUtc);
                _model.DiscardAll();
                UpdateBanner();
                await LoadAsync(force: true);
                _status.Text = "Saved.";
            }
            else if (result.StatusCode == 400 && _model.ApplyServerErrors(result.Body) > 0)
            {
                _status.Text = $"The server rejected {_model.ErrorCount} setting{(_model.ErrorCount == 1 ? "" : "s")}; nothing was changed and your edits are kept.";
                Ctx.Activity("Settings", "rejected: " + result.Error, true);
                ShowFirstError();
            }
            else
            {
                _status.Text = "Not saved: " + result.Error;
                Ctx.Activity("Settings", "not saved: " + result.Error, true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _saving = false;
            UpdateBottom();
            _sections.Invalidate();
        }
    }

    // ----- screenshot hooks (--mock --mock-edits) -----

    /// <summary>A few pending edits, one of them invalid, then Save: the page shows the validation error next to the row and sends nothing.</summary>
    internal async Task DemoEditsAsync()
    {
        await LoadAsync(force: false);
        _model.Set("general.ServerName", "Orbital Taxi Company of the Kerbin System"); // longer than the 30 characters the server allows
        _model.Set("general.Cheats", true);
        _model.Set("general.GameMode", "Science");
        _model.Set("general.MaxPlayers", 40L);
        _model.Set("general.Password", "demo-value-not-shown");
        await SaveAsync();
    }

    /// <summary>Fixes the invalid value and saves: the list refreshes from the server and the restart banner appears.</summary>
    internal async Task DemoFixAsync()
    {
        _model.Set("general.ServerName", "Orbital Taxi Co.");
        foreach (var c in _items)
        {
            if (c is SettingRow row) row.SyncInput();
        }

        await SaveAsync();
    }

    /// <summary>Shows a section, at its top or scrolled to its end. False when the schema has no such section.</summary>
    internal bool ShowSection(string sectionId, bool scrollToEnd)
    {
        var index = _model.Schema.Sections.FindIndex(x => x.Id == sectionId);
        if (index < 0) return false;
        if (_sectionId != sectionId || Filtering)
        {
            _sectionId = sectionId;
            _syncingList = true;
            _sections.SelectedIndex = index;
            _syncingList = false;
            RebuildRows();
        }

        _list.AutoScrollPosition = new Point(0, scrollToEnd ? int.MaxValue / 4 : 0);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            if (_host != null) DisposeRows();
            _tip?.Dispose();
        }

        base.Dispose(disposing);
    }
}
