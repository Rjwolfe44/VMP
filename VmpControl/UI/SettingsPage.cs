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
        Size = new Size(96, 24);
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
        var trackH = Math.Max(10, Height - 4);
        var trackW = (int)(trackH * 1.9f);
        var track = new Rectangle(1, (Height - trackH) / 2, trackW, trackH);
        var dim = !Enabled;
        using (var path = new GraphicsPath())
        {
            path.AddArc(track.X, track.Y, trackH, trackH, 90, 180);
            path.AddArc(track.Right - trackH, track.Y, trackH, trackH, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(_on ? (dim ? Color.FromArgb(60, 80, 120) : Theme.Accent) : Theme.Panel2);
            g.FillPath(fill, path);
            using var edge = new Pen(_on ? fill.Color : Theme.Border);
            g.DrawPath(edge, path);
        }

        var knob = trackH - 6;
        var knobX = _on ? track.Right - knob - 3 : track.X + 3;
        using (var white = new SolidBrush(dim ? Color.FromArgb(150, 156, 168) : Color.White)) g.FillEllipse(white, knobX, track.Y + 3, knob, knob);
        if (Focused && ShowFocusCues) g.DrawRectangle(Pens.Gray, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, _on ? "On" : "Off", Theme.Base, new Point(track.Right + 8, (Height - Theme.Base.Height) / 2), dim ? Theme.Muted : Theme.Text, TextFormatFlags.NoPadding);
    }
}

/// <summary>Asks for the new value of a secret setting. The text box masks what is typed; the value goes to the caller once and is wiped from the dialog.</summary>
internal sealed class SecretPromptForm : Form
{
    private readonly TextBox _box = Ui.TextBox(360);

    public SecretPromptForm(string title, string message, int? maxLength)
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
        ClientSize = new Size(420, 170);
        Native.DarkTitleBar(this);
        Controls.Add(new Label { Text = message, Location = new Point(18, 16), Size = new Size(384, 56), ForeColor = Theme.Text });
        _box.Location = new Point(18, 78);
        _box.Width = 384;
        _box.UseSystemPasswordChar = true;
        if (maxLength is > 0) _box.MaxLength = maxLength.Value;
        Controls.Add(_box);
        var ok = Ui.Button("Use this value", ButtonKind.Primary, 130);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        ok.Location = new Point(ClientSize.Width - 18 - 90 - 8 - 130, 120);
        cancel.Location = new Point(ClientSize.Width - 18 - 90, 120);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _box;
        ResumeLayout(false);
    }

    public string Value => _box.Text;

    /// <summary>Overwrites the typed text so that it does not linger in the dialog after it is closed.</summary>
    public void Wipe() => _box.Text = new string('*', _box.TextLength);
}

/// <summary>One setting: label, description, the input that matches its type, default hint, modified dot with a reset link, restart badge and the error text.</summary>
internal sealed class SettingRow : Panel
{
    private readonly SettingDto _s;
    private readonly SettingsEditModel _model;
    private readonly ToolTip _tip;
    private readonly bool _readOnly;
    private readonly Label _title = new(), _desc = new(), _hint = new(), _error = new(), _badge = new(), _note = new(), _state = new();
    private readonly LinkLabel _reset = new();
    private readonly Control _input;
    private readonly Button _change, _clear;
    private bool _syncing;
    private bool _dot, _dirty;
    private float _scale = 1f;

    public string Key => _s.Key;

    public SettingRow(SettingDto setting, SettingsEditModel model, ToolTip tip, Action<string> changeSecret, Action<string> clearSecret)
    {
        _s = setting;
        _model = model;
        _tip = tip;
        _readOnly = SettingsEditModel.IsReadOnly(setting);
        BackColor = Theme.Bg;
        DoubleBuffered = true;

        _title.Text = string.IsNullOrWhiteSpace(setting.Label) ? setting.Key : setting.Label;
        _title.Font = Theme.Bold;
        _title.AutoSize = true;
        _title.ForeColor = _readOnly ? Theme.Muted : Theme.Text;
        _tip.SetToolTip(_title, setting.Key);

        _desc.Text = setting.Description ?? "";
        _desc.Font = Theme.Small;
        _desc.ForeColor = Theme.Muted;
        _desc.AutoSize = false;

        _badge.Text = "applies after restart";
        _badge.Font = Theme.Small;
        _badge.ForeColor = Theme.Amber;
        _badge.BackColor = Color.FromArgb(52, 44, 26);
        _badge.AutoSize = true;
        _badge.Padding = new Padding(4, 1, 4, 1);
        _badge.Visible = setting.RestartRequired && !_readOnly;

        _hint.Font = Theme.Small;
        _hint.ForeColor = Theme.Muted;
        _hint.AutoSize = true;
        _hint.Text = setting.Secret ? "no default" : "default: " + Truncate(SettingsEditModel.FormatValue(setting.DefaultValue()), 40);

        _reset.Text = "Reset to default";
        _reset.Font = Theme.Small;
        _reset.AutoSize = true;
        _reset.LinkColor = Theme.Accent;
        _reset.ActiveLinkColor = Color.FromArgb(140, 180, 255);
        _reset.VisitedLinkColor = Theme.Accent;
        _reset.LinkBehavior = LinkBehavior.HoverUnderline;
        _reset.BackColor = Color.Transparent;
        _reset.LinkClicked += (_, _) =>
        {
            _model.ResetToDefault(_s.Key);
            SyncInput();
        };

        _error.Font = Theme.Small;
        _error.ForeColor = Theme.Red;
        _error.AutoSize = false;
        _error.Visible = false;

        _note.Text = "edit vmp-server.json and restart";
        _note.Font = Theme.Small;
        _note.ForeColor = Theme.Muted;
        _note.AutoSize = false;
        _note.Visible = _readOnly;

        if (setting.Secret)
        {
            _state.Font = Theme.Bold;
            _state.AutoSize = true;
            _change = Ui.Button("Change...", ButtonKind.Normal);
            _clear = Ui.Button("Clear", ButtonKind.Danger);
            _change.Click += (_, _) => changeSecret(_s.Key);
            _clear.Click += (_, _) => clearSecret(_s.Key);
            _change.Enabled = _clear.Enabled = !_readOnly;
            Controls.AddRange(new Control[] { _state, _change, _clear });
        }
        else
        {
            _input = MakeInput();
            _input.Enabled = !_readOnly;
            Controls.Add(_input);
        }

        Controls.AddRange(new Control[] { _title, _badge, _desc, _hint, _reset, _error, _note });
        SyncInput();
        RefreshState();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "...";

    private Control MakeInput()
    {
        switch (_s.TypeName)
        {
            case "bool":
            {
                var toggle = new ToggleSwitch { BackColor = Theme.Bg };
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
                    Font = Theme.Base,
                    DecimalPlaces = places,
                    Increment = isInt ? 1m : (decimal)Math.Pow(10, -places),
                    ThousandsSeparator = false,
                    Minimum = _s.Min.HasValue ? (decimal)_s.Min.Value : (isInt ? int.MinValue : -1_000_000_000m),
                    Maximum = _s.Max.HasValue ? (decimal)_s.Max.Value : (isInt ? int.MaxValue : 1_000_000_000m),
                };
                number.ValueChanged += (_, _) =>
                {
                    if (_syncing) return;
                    _model.Set(_s.Key, isInt ? (object)(long)number.Value : (double)number.Value);
                };
                return number;
            }
            case "enum":
            {
                var combo = Ui.Combo(220);
                combo.Items.AddRange(_s.Options.Cast<object>().ToArray());
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (!_syncing && combo.SelectedItem is string choice) _model.Set(_s.Key, choice);
                };
                return combo;
            }
            default:
            {
                var box = Ui.TextBox(320);
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

    /// <summary>Updates what depends on the model: modified dot, reset link, pending tint, secret state, error text. Returns true when the row's height may have changed.</summary>
    public bool RefreshState()
    {
        var dirty = _model.IsDirty(_s.Key);
        var modified = _model.IsModified(_s);
        var error = _model.ErrorFor(_s.Key);
        var heightChanged = (error ?? "") != _error.Text;

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
            _desc.BackColor = _title.BackColor = _hint.BackColor = _error.BackColor = _note.BackColor = _state.BackColor = tint;
            if (_input is ToggleSwitch) _input.BackColor = tint;
        }

        if (_dot != modified || _dirty != dirty)
        {
            _dot = modified;
            _dirty = dirty;
            Invalidate();
        }

        if (_input != null && !_input.Focused && InputDiffersFromModel()) SyncInput();
        return heightChanged;
    }

    /// <summary>Positions the children for a row of this width and returns the row height.</summary>
    public int LayoutFor(int width, float scale)
    {
        _scale = scale;
        int S(int v) => (int)Math.Round(v * scale);
        var pad = S(10);
        var side = S(14);
        var dotW = S(16);
        var gap = S(24);
        var infoW = (int)((width - 2 * side - gap) * 0.54f);
        var rightX = side + infoW + gap;
        var rightW = Math.Max(S(120), width - rightX - side);

        _title.Location = new Point(side + dotW, pad);
        _badge.Location = new Point(_title.Right + S(8), pad + Math.Max(0, (_title.Height - _badge.Height) / 2));
        var descW = infoW - dotW;
        var descH = string.IsNullOrEmpty(_desc.Text) ? 0 : TextRenderer.MeasureText(_desc.Text, _desc.Font, new Size(descW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + S(2);
        _desc.SetBounds(side + dotW, _title.Bottom + S(2), descW, descH);
        var leftBottom = Math.Max(_title.Bottom, _desc.Bottom);

        var y = pad;
        if (_input != null)
        {
            _input.Location = new Point(rightX, y);
            switch (_input)
            {
                case ToggleSwitch: _input.Size = new Size(S(96), S(24)); break;
                case NumericUpDown: _input.Width = Math.Min(rightW, S(170)); break;
                case ComboBox: _input.Width = Math.Min(rightW, S(230)); break;
                default: _input.Width = Math.Min(rightW, S(340)); break;
            }

            y = _input.Bottom + S(4);
        }
        else
        {
            _state.Location = new Point(rightX, y + S(2));
            _change.Location = new Point(rightX, _state.Bottom + S(4));
            _clear.Location = new Point(_change.Right + S(8), _change.Top);
            y = _change.Bottom + S(4);
        }

        _hint.Location = new Point(rightX, y);
        _reset.Location = new Point(_hint.Right + S(12), y);
        y = _hint.Bottom + S(2);
        if (_note.Visible)
        {
            _note.SetBounds(rightX, y, rightW, TextRenderer.MeasureText(_note.Text, _note.Font, new Size(rightW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + S(2));
            y = _note.Bottom + S(2);
        }

        if (_error.Visible)
        {
            _error.SetBounds(rightX, y, rightW, TextRenderer.MeasureText(_error.Text, _error.Font, new Size(rightW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + S(2));
            y = _error.Bottom + S(2);
        }

        var height = Math.Max(leftBottom, y) + pad + 1;
        SetBounds(Left, Top, width, height);
        return height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Math.Max(3, (int)(4 * _scale));
        var cy = (int)(10 * _scale) + _title.Height / 2;
        if (_dot)
        {
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, (int)(14 * _scale) + 1, cy - r, 2 * r, 2 * r);
        }

        if (_dirty)
        {
            using var bar = new SolidBrush(Theme.Amber);
            g.FillRectangle(bar, 0, 0, Math.Max(3, (int)(3 * _scale)), Height - 1);
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
    private Panel _bannerPanel, _bottom, _list, _host;
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
        _status = Ui.Label("", true);
        _status.Margin = new Padding(8, 7, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Bg };
        _search.Margin = new Padding(0, 2, 8, 0);
        var searchLabel = Ui.Label("Search", true);
        searchLabel.Margin = new Padding(0, 8, 8, 0);
        toolbar.Controls.AddRange(new Control[] { searchLabel, _search, _modifiedOnly, refresh, _status });

        _bannerText = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(40, 28, 0), BackColor = Color.Transparent, Font = Theme.Bold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
        _restartNow = Ui.Button("Restart now", ButtonKind.Normal, 120);
        _restartNow.Dock = DockStyle.Right;
        _restartNow.Margin = new Padding(0);
        _restartNow.Click += async (_, _) => await Ctx.Actions.ControlAsync("Restart");
        _bannerPanel = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Amber, Visible = false, Padding = new Padding(0, 4, 6, 4) };
        _bannerPanel.Controls.Add(_bannerText);
        _bannerPanel.Controls.Add(_restartNow);

        _unsaved = new Label { AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Muted, Location = new Point(14, 16) };
        _save = Ui.Button("Save", ButtonKind.Primary, 100);
        _discard = Ui.Button("Discard", ButtonKind.Normal, 100);
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) => DiscardAll();
        _bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.Panel };
        _bottom.Controls.AddRange(new Control[] { _unsaved, _save, _discard });
        _bottom.Resize += (_, _) => PlaceBottomButtons();

        _sections = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 200,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            Font = Theme.Base,
        };
        _sections.Resize += (_, _) => _sections.ItemHeight = TextRenderer.MeasureText("Ag", Theme.Base).Height + 12;
        _sections.DrawItem += DrawSectionItem;
        _sections.SelectedIndexChanged += (_, _) => OnSectionChosen();
        Native.DarkScrollbars(_sections);

        _host = new Panel { BackColor = Theme.Bg, Location = new Point(0, 0) };
        _list = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(12, 0, 0, 0) };
        _list.Controls.Add(_host);
        _list.Resize += (_, _) => LayoutRows();
        Native.DarkScrollbars(_list);

        Body.Controls.Add(_list);
        Body.Controls.Add(_sections);
        Body.Controls.Add(_bottom);
        Body.Controls.Add(_bannerPanel);
        Body.Controls.Add(toolbar);
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
        var selected = (e.State & DrawItemState.Selected) != 0 && !Filtering;
        using (var back = new SolidBrush(selected ? Color.FromArgb(46, 62, 94) : Theme.Panel)) e.Graphics.FillRectangle(back, e.Bounds);
        var errors = _model.ErrorCountIn(section.Id);
        var dirty = _model.DirtyCountIn(section.Id);
        var right = errors > 0 ? "!" : dirty > 0 ? dirty.ToString(CultureInfo.InvariantCulture) : "";
        var rightColor = errors > 0 ? Theme.Red : Theme.Amber;
        var rightW = right.Length == 0 ? 0 : TextRenderer.MeasureText(right, Theme.Bold).Width + 8;
        TextRenderer.DrawText(e.Graphics, _sections.Items[e.Index].ToString(), Theme.Base, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 20 - rightW, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (right.Length > 0) TextRenderer.DrawText(e.Graphics, right, Theme.Bold, new Rectangle(e.Bounds.Right - rightW - 8, e.Bounds.Y, rightW, e.Bounds.Height), rightColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
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
                _items.Add(new Label { Text = (section?.Title ?? lastSection).ToUpperInvariant(), Font = Theme.Bold, ForeColor = Theme.Muted, AutoSize = false, TextAlign = ContentAlignment.BottomLeft, BackColor = Theme.Bg, Padding = new Padding(14, 0, 0, 4), Tag = "header" });
            }

            _items.Add(new SettingRow(s, _model, _tip, ChangeSecret, ClearSecret));
        }

        if (matches.Count == 0)
            _items.Add(new Label { Text = filtering ? "No setting matches." : "This section has no settings.", Font = Theme.Base, ForeColor = Theme.Muted, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Bg, Padding = new Padding(14, 0, 0, 0) });
        else if (matches.Count > MaxRows)
            _items.Add(new Label { Text = $"Only the first {MaxRows} matches are shown. Narrow the search to see the rest.", Font = Theme.Base, ForeColor = Theme.Muted, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Bg, Padding = new Padding(14, 0, 0, 0) });

        foreach (var c in _items) _host.Controls.Add(c);
        _host.ResumeLayout(false);
        LayoutRows();
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

    private void LayoutRows()
    {
        if (_host == null || _list == null) return;
        var scale = DeviceDpi / 96f;
        var width = Math.Max(300, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        var y = 0;
        foreach (var c in _items)
        {
            switch (c)
            {
                case SettingRow row:
                    var rowHeight = row.LayoutFor(width, scale);
                    row.SetBounds(0, y, width, rowHeight);
                    y += rowHeight;
                    break;
                case Label label:
                    var h = (int)((label.Tag as string == "header" ? 38 : 34) * scale);
                    label.SetBounds(0, y, width, h);
                    y += h;
                    break;
            }
        }

        _host.SetBounds(_list.Padding.Left, 0, width, Math.Max(y, 1));
        _list.AutoScrollMinSize = new Size(0, y);
    }

    private void OnModelChanged()
    {
        if (!_ready) return;
        var relayout = false;
        foreach (var c in _items)
        {
            if (c is SettingRow row) relayout |= row.RefreshState();
        }

        if (relayout) LayoutRows();
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

    private void PlaceBottomButtons()
    {
        if (_bottom == null) return;
        var m = (int)(14 * DeviceDpi / 96f);
        _save.Location = new Point(_bottom.ClientSize.Width - m - _save.Width, (_bottom.ClientSize.Height - _save.Height) / 2);
        _discard.Location = new Point(_save.Left - 8 - _discard.Width, _save.Top);
        _unsaved.Top = (_bottom.ClientSize.Height - _unsaved.Height) / 2;
    }

    private void UpdateBottom()
    {
        if (_bottom == null) return;
        var n = _model.DirtyCount;
        var problems = _model.ErrorCount;
        _unsaved.Text = (n == 0 ? "No unsaved changes" : n == 1 ? "1 unsaved change" : n + " unsaved changes") + (problems > 0 ? $"  -  {problems} problem{(problems == 1 ? "" : "s")} to fix" : "");
        _unsaved.ForeColor = problems > 0 ? Theme.Red : n > 0 ? Theme.Amber : Theme.Muted;
        _save.Enabled = n > 0 && !_saving;
        _discard.Enabled = n > 0 && !_saving;
        PlaceBottomButtons();
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
        LayoutRows();
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
            LayoutRows();
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
                LayoutRows();
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
            DisposeRows();
            _tip?.Dispose();
        }

        base.Dispose(disposing);
    }
}
