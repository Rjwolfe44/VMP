using System.Diagnostics;
using System.Text;
using VmpControl.Core;

namespace VmpControl.UI;

internal sealed class PageContext
{
    public IServerSession Session { get; init; }
    public AppSettings Settings { get; init; }
    public Poller Poller { get; init; }
    public ActionService Actions { get; set; }
    /// <summary>Opens the Settings tab with this text in its search box (the Backups tab uses it for "Edit retention...").</summary>
    public Action<string> OpenSettings { get; set; }
    public PollSnapshot Latest => Poller.Latest;
    public CancellationToken Cancel => Poller.Token;

    public void Activity(string action, string result, bool failed = false) => Session.Log.Add(action, result, failed);
}

/// <summary>One tab. Only the selected tab is visible, polls (on its own timer) and repaints; the others cost nothing.</summary>
internal abstract class Page : Panel
{
    protected readonly PageContext Ctx;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _message;
    private bool _active;
    private bool _busy;
    private string _lastRefreshError;

    /// <summary>The page's own controls go in here. When the admin API is not available it is hidden and <see cref="_message"/> takes its place.</summary>
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };

    protected Page(PageContext ctx, int pollMs = 0)
    {
        Ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(Spacing.L, Spacing.M, Spacing.L, Spacing.M);
        AutoScroll = true;
        Visible = false;
        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            BackColor = Theme.Bg,
            Visible = false,
        };
        Ui.Style(_message, FontRole.Big);
        Controls.Add(Body);
        Controls.Add(_message);
        Native.DarkScrollbars(this);
        if (pollMs > 0)
        {
            _timer = new System.Windows.Forms.Timer { Interval = pollMs };
            _timer.Tick += async (_, _) => await SafeRefreshAsync();
        }
    }

    public abstract string Title { get; }
    protected virtual bool NeedsApi => true;

    /// <summary>The height (in 96-dpi units) below which the page scrolls instead of squeezing its content. 0 = never scrolls.</summary>
    protected virtual int MinContentHeight => 0;

    public bool IsActive => _active;

    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>Called after the window's DPI (or font metrics) changed, when the page has numbers of its own to recompute.</summary>
    public virtual void RefreshMetrics()
    {
    }

    public void SetActive(bool active)
    {
        _active = active;
        Visible = active;
        if (_timer != null) _timer.Enabled = active;
        if (active)
        {
            if (!_built)
            {
                _built = true;
                BuildLate();
            }

            // Windows Forms rescales the fonts of a hidden page only when it is shown, on top of ours: set the role fonts and metrics once more now that it is visible.
            if (IsHandleCreated && FindForm() != null) Ui.ApplyDpi(this);
            RefreshMetrics();
            OnSnapshot(Ctx.Latest);
            _ = SafeRefreshAsync();
        }
    }

    private bool _built;
    private string _notice;

    /// <summary>For a page that creates its controls only when it is first shown (so a tab nobody opens costs no memory). Add them to <see cref="Body"/>.</summary>
    protected virtual void Build()
    {
    }

    /// <summary>Runs <see cref="Build"/> and scales whatever it added exactly once: the window's first layout has already scaled everything that existed before.</summary>
    private void BuildLate()
    {
        var before = Body.Controls.Cast<Control>().ToHashSet();
        Build();
        var added = Body.Controls.Cast<Control>().Where(c => !before.Contains(c)).ToList();
        if (added.Count == 0) return;
        var dpi = Ui.DpiOf(this);
        foreach (var child in added) Ui.ScaleLate(child, this);
        Ui.ApplyDpi(Body, dpi);
    }

    /// <summary>Called for every status poll, also while this page is hidden (cheap bookkeeping only, no painting).</summary>
    public virtual void ObserveSnapshot(PollSnapshot s)
    {
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var min = MinContentHeight;
        if (min > 0)
        {
            var wanted = new Size(0, Ui.Dp(this, min) + Padding.Vertical);
            if (AutoScrollMinSize != wanted) AutoScrollMinSize = wanted;
        }

        base.OnLayout(levent);
    }

    /// <summary>A message that replaces the page, such as "this server needs the update" for one missing endpoint. Null removes it.</summary>
    protected void SetNotice(string text)
    {
        _notice = text;
        UpdateOverlay(Ctx.Latest, always: true);
    }

    /// <summary>A failing refresh is logged once per distinct message (it would otherwise repeat on every poll) and again after a success in between.</summary>
    public async Task SafeRefreshAsync()
    {
        if (_busy || !_active || !CanRefresh()) return;
        _busy = true;
        try
        {
            await RefreshAsync();
            _lastRefreshError = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            var text = "refresh failed: " + e.Message;
            if (!string.Equals(text, _lastRefreshError, StringComparison.Ordinal))
            {
                _lastRefreshError = text;
                Ctx.Activity(Title, text, true);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private bool CanRefresh() => !NeedsApi || Ctx.Latest == null || Ctx.Latest.Api == ApiAvailability.Ok;

    public virtual void OnSnapshot(PollSnapshot s)
    {
        if (NeedsApi) UpdateOverlay(s);
    }

    private void UpdateOverlay(PollSnapshot s, bool always = false)
    {
        string text = null;
        if (s == null || s.Api == ApiAvailability.Unknown) text = "Connecting...";
        else if (s.Api == ApiAvailability.NotSupported) text = "This server needs the update that adds the admin API.\n\nThe tray icon still works through the control script (Start, Stop, Restart).";
        else if (s.Api == ApiAvailability.Unauthorized) text = "The server refused the admin token.\n\nIt will be read again in a moment.";
        else if (s.Api == ApiAvailability.Unreachable)
            text = "The admin API is not reachable" + (string.IsNullOrEmpty(s.ApiDetail) ? "." : ": " + s.ApiDetail) + (string.IsNullOrEmpty(s.TunnelStatus) ? "" : "\n\nSSH tunnel: " + s.TunnelStatus);
        text ??= _notice;
        var showMessage = text != null;
        if (showMessage && _message.Text != text) _message.Text = text;
        _message.Visible = showMessage;
        Body.Visible = !showMessage;
    }

    /// <summary>Sets a label's text and hides it when there is none, so an empty line takes no space.</summary>
    protected static void Say(Label label, string text)
    {
        label.Text = text ?? string.Empty;
        label.Visible = !string.IsNullOrEmpty(text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class OverviewPage : Page
{
    private readonly StatCard _server = new("SERVER"), _mode = new("GAME MODE"), _version = new("VERSION"), _uptime = new("UPTIME");
    private readonly StatCard _cpu = new("SERVER CPU"), _memory = new("SERVER MEMORY"), _vessels = new("VESSELS"), _universe = new("UNIVERSE SIZE");
    private readonly StatCard _ram = new("PC RAM IN USE"), _ramFree = new("PC RAM FREE"), _disk = new("DISK FREE"), _osUptime = new("PC UPTIME");
    private readonly DataGridView _players = Ui.Grid();
    private readonly Label _playersHeading = Ui.Heading("PLAYERS");

    public OverviewPage(PageContext ctx) : base(ctx)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6, BackColor = Theme.Bg, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var i = 0; i < 5; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var first = new[] { _server, _mode, _version, _uptime, _cpu, _memory, _vessels, _universe };
        for (var i = 0; i < first.Length; i++) Place(table, first[i], i % 4, i / 4);
        AddHeading(table, Ui.Heading("SERVER PC"), 2);
        var machine = new[] { _ram, _ramFree, _disk, _osUptime };
        for (var i = 0; i < machine.Length; i++) Place(table, machine[i], i, 3);
        AddHeading(table, _playersHeading, 4);
        Ui.Columns(_players, ("Player", 40, false), ("Subspace", 15, true), ("Connected for", 25, true), ("Since", 30, true));
        table.Controls.Add(_players, 0, 5);
        table.SetColumnSpan(_players, 4);
        Body.Controls.Add(table);
    }

    private static void AddHeading(TableLayoutPanel table, Label heading, int row)
    {
        heading.Dock = DockStyle.Fill;
        heading.Margin = new Padding(0, Spacing.Xs, 0, Spacing.Xs);
        table.Controls.Add(heading, 0, row);
        table.SetColumnSpan(heading, 4);
    }

    private static void Place(TableLayoutPanel table, StatCard card, int column, int row)
    {
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, column == 3 ? 0 : Spacing.S, Spacing.S);
        table.Controls.Add(card, column, row);
    }

    public override string Title => "Overview";
    protected override bool NeedsApi => false;
    protected override int MinContentHeight => 470;

    public override void OnSnapshot(PollSnapshot s)
    {
        var status = s?.Api == ApiAvailability.Ok ? s.Status : null;
        _server.Value = status?.Settings?.ServerName ?? (s?.ProfileName ?? "-");
        _mode.Value = status?.Settings == null ? "-" : $"{status.Settings.GameMode} / {status.Settings.WarpMode}";
        _version.Value = status?.Version ?? "-";
        _uptime.Value = status == null ? "-" : Fmt.Duration(status.UptimeSeconds);
        _cpu.Value = status == null ? "-" : Fmt.Percent(status.CpuPercent);
        _memory.Value = status == null ? "-" : Fmt.Bytes(status.WorkingSetBytes);
        _vessels.Value = status == null ? "-" : Fmt.Count(status.VesselCount);
        _universe.Value = status == null ? "-" : Fmt.Bytes(status.UniverseBytes);

        var m = status?.Machine;
        if (m?.RamTotalBytes != null && m.RamFreeBytes != null)
        {
            var used = m.RamTotalBytes.Value - m.RamFreeBytes.Value;
            _ram.Value = $"{Fmt.Bytes(used)} of {Fmt.Bytes(m.RamTotalBytes)}";
            _ramFree.Value = Fmt.Bytes(m.RamFreeBytes);
        }
        else
        {
            _ram.Value = _ramFree.Value = "-";
        }

        _disk.Value = m?.DiskFreeBytes == null ? "-" : $"{Fmt.Bytes(m.DiskFreeBytes)} of {Fmt.Bytes(m.DiskTotalBytes)}";
        _osUptime.Value = Fmt.Duration(m?.OsUptimeSeconds);

        IEnumerable<object[]> rows;
        if (status != null)
        {
            rows = status.Players.Select(p => new object[] { p.Name, p.Subspace.ToString(), Fmt.Duration(p.ConnectedSeconds), Fmt.LocalTime(p.ConnectedSinceUtc) });
            _playersHeading.Text = $"PLAYERS ({status.PlayerCount} of {status.Settings?.MaxPlayers.ToString() ?? "?"})";
        }
        else
        {
            var names = s?.Script?.Names ?? new List<string>();
            rows = names.Select(n => new object[] { n, "-", "-", "-" });
            _playersHeading.Text = "PLAYERS" + (s?.Script?.Players is int count ? $" ({count})" : "");
        }

        Ui.Fill(_players, rows);
    }
}

internal sealed class NetworkPage : Page
{
    private readonly Button[] _ranges;
    private readonly DataGridView _out = Ui.Grid(), _in = Ui.Grid(), _snapshots = Ui.Grid(), _messages = Ui.Grid(), _clients = Ui.Grid();
    private readonly Label _totals = Ui.Wrap("", true);
    private readonly Label _compressionNote = Ui.Wrap("", true);
    private readonly Label _outHeading, _inHeading;
    private readonly Label _snapshotsHeading, _messagesHeading, _clientsHeading;
    private readonly Panel _scroll;
    private readonly TableLayoutPanel _body;
    private readonly Sparkline _spark = new();
    private int _range;
    private long _prevSent = -1, _prevReceived = -1;
    private double _prevUptime;

    public NetworkPage(PageContext ctx) : base(ctx, 5000)
    {
        string[] names = { "Last minute", "Last 10 minutes", "Since start" };
        _ranges = names.Select((n, i) =>
        {
            var b = Ui.Button(n, i == 0 ? ButtonKind.Primary : ButtonKind.Normal);
            b.Click += async (_, _) =>
            {
                _range = i;
                for (var k = 0; k < _ranges.Length; k++) StyleToggle(_ranges[k], k == _range);
                await SafeRefreshAsync();
            };
            return b;
        }).ToArray();

        var legend = Ui.Wrap("blue: sent   green: received", true, FontRole.Small);
        var items = new List<Control>(_ranges) { _spark, legend };
        var toolbar = Ui.Bar(items.ToArray(), legend);
        _spark.Margin = new Padding(Spacing.S, 0, Spacing.S, 0);

        Ui.Columns(_out, ("Message type", 45, false), ("Bytes", 20, true), ("Messages", 17, true), ("Bytes/s", 18, true));
        Ui.Columns(_in, ("Message type", 45, false), ("Bytes", 20, true), ("Messages", 17, true), ("Bytes/s", 18, true));
        Ui.Columns(_snapshots, ("Domain", 14, false), ("Snapshots", 12, true), ("Raw", 14, true), ("Sent", 14, true), ("Ratio", 10, true), ("Encodings", 36, false));
        Ui.Columns(_messages, ("Type", 22, false), ("Messages", 10, true), ("Raw", 11, true), ("Sent (wire)", 12, true), ("Saved", 11, true), ("Ratio", 9, true), ("Codec", 12, false), ("Level", 9, true));
        Ui.Columns(_clients, ("Player", 18, false), ("Bytes in", 13, true), ("Bytes out", 13, true), ("Wire in", 13, true), ("Wire out", 13, true), ("Latency", 10, true), ("Build", 20, false));

        var types = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var sent = Ui.Section("SENT BY THE SERVER", _out, out _outHeading);
        var received = Ui.Section("RECEIVED BY THE SERVER", _in, out _inHeading);
        received.Margin = Padding.Empty;
        types.Controls.Add(sent, 0, 0);
        types.Controls.Add(received, 1, 0);

        // Four tables do not fit the default window height, so the tables get their natural height (heading + header + the rows they usually
        // show) and the page scrolls when the window is lower than that. A taller window gives every table a proportional share.
        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg, Margin = Padding.Empty };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) _body.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        _body.Controls.Add(types, 0, 0);
        _body.Controls.Add(Ui.Section("SNAPSHOT COMPRESSION", _snapshots, out _snapshotsHeading), 0, 1);
        var messages = new VStack();
        messages.Auto(_compressionNote);
        _compressionNote.Margin = new Padding(0, 0, 0, Spacing.Xs);
        messages.Fill(_messages);
        _body.Controls.Add(Ui.Section("MESSAGE COMPRESSION", messages, out _messagesHeading), 0, 2);
        _body.Controls.Add(Ui.Section("CLIENTS", _clients, out _clientsHeading), 0, 3);
        foreach (Control c in _body.Controls) c.Margin = new Padding(0, 0, 0, Spacing.S);

        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Margin = Padding.Empty };
        _scroll.Controls.Add(_body);
        _scroll.SizeChanged += (_, _) => RefreshMetrics();
        Native.DarkScrollbars(_scroll);

        var stack = new VStack();
        stack.Auto(toolbar);
        stack.Auto(_totals, Spacing.S);
        stack.Fill(_scroll, Spacing.S);
        Body.Controls.Add(stack);
    }

    private static void StyleToggle(Button b, bool on)
    {
        b.BackColor = on ? Theme.Accent : Theme.Panel2;
        b.ForeColor = on ? Color.White : Theme.Text;
        b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
    }

    public override string Title => "Network";

    /// <summary>The share of each table is the height it needs for the rows it usually shows; the scroll area is the sum of them.</summary>
    public override void RefreshMetrics()
    {
        if (_body == null || !IsHandleCreated) return;
        var row = _out.RowTemplate.Height;
        var header = _out.ColumnHeadersHeight;
        var heading = Math.Max(_outHeading.Height, Ui.TextHeight(_outHeading.Font) + Ui.Dp(this, Spacing.Xs));
        var block = heading + header + Ui.Dp(this, Spacing.S);
        var note = Math.Max(_compressionNote.Height, Ui.TextHeight(_compressionNote.Font)) + Ui.Dp(this, Spacing.Xs);
        float[] needs = { block + 10 * row, block + 3 * row, block + 5 * row + note, block + 3 * row };
        for (var i = 0; i < needs.Length; i++)
        {
            var style = _body.RowStyles[i];
            if (Math.Abs(style.Height - needs[i]) > 0.5f) style.Height = needs[i];
        }

        var wanted = new Size(0, (int)needs.Sum());
        if (_scroll.AutoScrollMinSize != wanted) _scroll.AutoScrollMinSize = wanted;
    }

    /// <summary>--screenshot: shows the lower tables, which do not fit the default window height, or goes back to the top.</summary>
    internal void ScrollTo(bool end) => _scroll.AutoScrollPosition = new Point(0, end ? _scroll.DisplayRectangle.Height : 0);

    public override async Task RefreshAsync()
    {
        var result = await Ctx.Session.GetNetworkAsync(Ctx.Cancel);
        if (result.IsOk) Show(result.Value);
        FillClients(Ctx.Latest);
    }

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        FillClients(s);
    }

    private void FillClients(PollSnapshot s)
    {
        if (s == null || s.Api != ApiAvailability.Ok) return;
        Ui.Fill(_clients, s.Clients.Select(c => new object[]
        {
            c.Name, Fmt.Bytes(c.BytesIn), Fmt.Bytes(c.BytesOut), Fmt.Bytes(c.WireBytesIn), Fmt.Bytes(c.WireBytesOut),
            c.LatencyMs == null ? "-" : c.LatencyMs.Value.ToString("0") + " ms", c.Build ?? "-",
        }));
    }

    private void Show(NetworkDto n)
    {
        var window = _range switch { 0 => n.LastMinute, 1 => n.LastTenMinutes, _ => n.SinceStart };
        var note = "";
        if (window == null)
        {
            window = n.SinceStart;
            note = " (no closed window yet, showing since start)";
        }

        var t = n.Totals;
        _totals.Text = (t == null ? "" : $"Since start: sent {Fmt.Bytes(t.SentBytes)} in {Fmt.Count(t.SentMessages)} messages, received {Fmt.Bytes(t.ReceivedBytes)} in {Fmt.Count(t.ReceivedMessages)}.   ") +
                       $"Server up {Fmt.Duration(n.UptimeSeconds)}.{note}";
        Say(_compressionNote, MessageCompressionView.Summary(n.Compression, MessageCompressionView.Pick(n, n.SinceStart)));

        // Sparkline: rate since the previous sample, or the window's own average for the first one / after a server restart.
        if (t != null)
        {
            if (_prevSent >= 0 && n.UptimeSeconds > _prevUptime && t.SentBytes >= _prevSent && t.ReceivedBytes >= _prevReceived)
            {
                var dt = n.UptimeSeconds - _prevUptime;
                _spark.Add((t.SentBytes - _prevSent) / dt, (t.ReceivedBytes - _prevReceived) / dt);
            }
            else if (n.SinceStart != null)
            {
                _spark.Add(n.SinceStart.Out?.BytesPerSecond ?? 0, n.SinceStart.In?.BytesPerSecond ?? 0);
            }

            _prevSent = t.SentBytes;
            _prevReceived = t.ReceivedBytes;
            _prevUptime = n.UptimeSeconds;
        }

        if (window == null)
        {
            Ui.Fill(_out, Array.Empty<object[]>());
            Ui.Fill(_in, Array.Empty<object[]>());
            Ui.Fill(_snapshots, Array.Empty<object[]>());
            FillMessages(Array.Empty<MessageCompressionDto>());
            return;
        }

        FillTypes(_out, _outHeading, "SENT BY THE SERVER", window.Out);
        FillTypes(_in, _inHeading, "RECEIVED BY THE SERVER", window.In);
        var snapshots = window.Snapshots.Count > 0 ? window.Snapshots : n.SnapshotCompression;
        Ui.Fill(_snapshots, snapshots.Select(s => new object[]
        {
            s.Domain, Fmt.Count(s.Snapshots), Fmt.Bytes(s.RawBytes), Fmt.Bytes(s.WireBytes), Fmt.Ratio(s.Ratio), s.Encodings?.Summary() ?? "-",
        }));
        FillMessages(MessageCompressionView.Pick(n, window));
    }

    /// <summary>The "Message compression" table: the rows of the selected window, or one dim line when there are none.</summary>
    private void FillMessages(IReadOnlyList<MessageCompressionDto> items)
    {
        var rows = MessageCompressionView.Rows(items);
        if (rows.Count > 0)
        {
            Ui.Fill(_messages, rows);
            return;
        }

        Ui.Fill(_messages, new[] { MessageCompressionView.EmptyRow() });
        var style = _messages.Rows[0].DefaultCellStyle;
        style.ForeColor = style.SelectionForeColor = Theme.Muted;
    }

    private static void FillTypes(DataGridView grid, Label heading, string title, TrafficDirectionDto d)
    {
        if (d == null)
        {
            heading.Text = title;
            Ui.Fill(grid, Array.Empty<object[]>());
            return;
        }

        heading.Text = $"{title}  -  {Fmt.Rate(d.BytesPerSecond)}, {Fmt.Bytes(d.Bytes)}, {Fmt.Count(d.Messages)} messages";
        var rows = d.Types.Select(x => new object[] { x.Type, Fmt.Bytes(x.Bytes), Fmt.Count(x.Messages), Fmt.Rate(x.BytesPerSecond) }).ToList();
        if (rows.Count > 0) rows.Add(new object[] { "Total", Fmt.Bytes(d.Bytes), Fmt.Count(d.Messages), Fmt.Rate(d.BytesPerSecond) });
        Ui.Fill(grid, rows);
    }
}

internal sealed class LogsPage : Page
{
    private const int MaxShown = 500;
    private readonly RichTextBox _box = new();
    private readonly TextBox _filter = Ui.TextBox(120);
    private readonly Button _pause = Ui.Button("Pause");
    private readonly ComboBox _count = Ui.Combo(80);
    private readonly Label _info = Ui.Wrap("", true);
    private List<string> _fetched = new();
    private List<string> _shown = new();
    private string _signature = "";
    private bool _paused;

    public LogsPage(PageContext ctx) : base(ctx, 3000)
    {
        _count.Items.AddRange(new object[] { "100", "200", "500" });
        _count.SelectedIndex = 1;
        _count.SelectedIndexChanged += async (_, _) => { _signature = ""; await SafeRefreshAsync(); };
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? "Resume" : "Pause";
            _pause.BackColor = _paused ? Theme.Amber : Theme.Panel2;
            _pause.ForeColor = _paused ? Color.Black : Theme.Text;
            Say(_info, _paused ? "paused" : "");
        };
        _filter.TextChanged += (_, _) => Render(force: true);
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => { _signature = ""; await SafeRefreshAsync(); };

        var toolbar = Ui.Bar(new Control[] { _pause, refresh, Ui.Label("Filter", true), _filter, Ui.Label("Lines", true), _count }, _filter);

        _box.Dock = DockStyle.Fill;
        _box.ReadOnly = true;
        _box.BackColor = Theme.Panel;
        _box.ForeColor = Theme.Text;
        _box.BorderStyle = BorderStyle.None;
        _box.WordWrap = false;
        _box.DetectUrls = false;
        _box.HideSelection = false;
        _box.ScrollBars = RichTextBoxScrollBars.Both;
        Ui.Style(_box, FontRole.Mono);
        Native.DarkScrollbars(_box);

        var stack = new VStack();
        stack.Auto(toolbar);
        stack.Fill(_box, Spacing.S);
        stack.Auto(_info, Spacing.S);
        _info.Visible = false;
        Body.Controls.Add(stack);
    }

    public override string Title => "Logs";

    public override async Task RefreshAsync()
    {
        if (_paused) return;
        var lines = int.Parse((string)_count.SelectedItem, System.Globalization.CultureInfo.InvariantCulture);
        var result = await Ctx.Session.GetLogAsync(Math.Min(lines, MaxShown), Ctx.Cancel);
        if (!result.IsOk)
        {
            Say(_info, "log unavailable: " + result.Error);
            return;
        }

        _fetched = result.Value.Lines;
        var signature = _fetched.Count + "|" + (_fetched.Count > 0 ? _fetched[0] + "|" + _fetched[^1] : "");
        if (signature == _signature) return;
        _signature = signature;
        Render(force: false);
        Say(_info, $"{_shown.Count} lines, updated {DateTime.Now:HH:mm:ss}");
    }

    private void Render(bool force)
    {
        var filter = _filter.Text.Trim();
        var shown = filter.Length == 0 ? _fetched : _fetched.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var atBottom = Native.IsScrolledToBottom(_box);
        string anchor = null;
        var anchorIndex = 0;
        if (!atBottom && _box.TextLength > 0 && !force)
        {
            anchorIndex = _box.GetLineFromCharIndex(_box.GetCharIndexFromPosition(new Point(4, 4)));
            if (anchorIndex < _shown.Count) anchor = _shown[anchorIndex];
        }
        else if (!atBottom && force && _box.TextLength > 0)
        {
            anchorIndex = _box.GetLineFromCharIndex(_box.GetCharIndexFromPosition(new Point(4, 4)));
            if (anchorIndex < _shown.Count) anchor = _shown[anchorIndex];
        }

        Native.SetRedraw(_box, false);
        try
        {
            _box.Rtf = BuildRtf(shown);
            if (atBottom || anchor == null)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
            else
            {
                // Keep the same line at the top: find where it moved to (the log tail slides as new lines arrive).
                var newIndex = -1;
                var best = int.MaxValue;
                for (var i = 0; i < shown.Count; i++)
                {
                    if (shown[i] == anchor && Math.Abs(i - anchorIndex) < best)
                    {
                        best = Math.Abs(i - anchorIndex);
                        newIndex = i;
                    }
                }

                if (newIndex > 0) Native.SendMessage(_box.Handle, Native.EM_LINESCROLL, IntPtr.Zero, (IntPtr)newIndex);
            }
        }
        finally
        {
            Native.SetRedraw(_box, true);
            _box.Invalidate();
        }

        _shown = shown;
    }

    private static string BuildRtf(List<string> lines)
    {
        var sb = new StringBuilder(lines.Count * 100 + 200);
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}");
        sb.Append(@"{\colortbl;\red230\green232\blue236;\red255\green110\blue110;\red255\green184\blue77;\red138\green147\blue163;}\f0\fs19 ");
        foreach (var line in lines)
        {
            var color = line.Contains("[Error]", StringComparison.Ordinal) || line.Contains("[Fatal]", StringComparison.Ordinal) ? 2
                : line.Contains("[Warning]", StringComparison.Ordinal) ? 3
                : line.Contains("[Debug]", StringComparison.Ordinal) ? 4 : 1;
            sb.Append(@"\cf").Append(color).Append(' ');
            foreach (var c in line)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c > 126) sb.Append(@"\u").Append((short)c).Append('?');
                else if (c >= 32) sb.Append(c);
            }

            sb.Append(@"\par ");
        }

        sb.Append('}');
        return sb.ToString();
    }
}

internal sealed class BackupsPage : Page
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Button _backupNow = Ui.Button("Back up now", ButtonKind.Primary);
    private readonly Button _delete = Ui.Button("Delete selected...");
    private readonly Button _prune = Ui.Button("Prune now...");
    private readonly Button _editRetention = Ui.Button("Edit retention...");
    private readonly CheckBox _includeZips = new() { Text = "Also old .zip backups", AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Margin = Padding.Empty };
    private readonly Label _summary = Ui.Wrap("", false, FontRole.Bold);
    private readonly Label _retention = Ui.Wrap("", true);
    private readonly Label _result = Ui.Wrap("No backup made from this window yet.", true);
    private readonly Label _excluded = Ui.Wrap("The items a backup leaves out are listed here after one has run.", true);
    private BackupsDto _data;

    public BackupsPage(PageContext ctx) : base(ctx)
    {
        Ui.Style(_includeZips, FontRole.Base);
        _backupNow.Click += async (_, _) => await BackupAsync();
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => await SafeRefreshAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _prune.Click += async (_, _) => await PruneAsync();
        _editRetention.Click += (_, _) => Ctx.OpenSettings?.Invoke("backup");
        // "Edit retention..." sits at the right end; the stretch cell between it and the checkbox is an empty spacer.
        var spacer = new Panel { Height = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        var toolbar = Ui.Bar(new Control[] { _backupNow, refresh, _delete, _prune, _includeZips, spacer, _editRetention }, spacer);

        Ui.Columns(_grid, ("Name", 44, false), ("Size", 12, true), ("Created", 20, true), ("Kind", 24, false));
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        // The header block: one line each for the summary, the retention policy, the selection / last result and the items a backup leaves out.
        var stack = new VStack();
        stack.Auto(toolbar);
        stack.Auto(_summary, Spacing.M);
        stack.Auto(_retention, Spacing.Xs);
        stack.Auto(_result, Spacing.Xs);
        stack.Auto(_excluded, Spacing.Xs);
        stack.Fill(_grid, Spacing.M);
        _summary.Visible = _retention.Visible = false;
        Body.Controls.Add(stack);
        Ctx.Actions.BusyChanged += busy =>
        {
            if (IsDisposed) return;
            _backupNow.Enabled = _delete.Enabled = _prune.Enabled = !busy;
        };
    }

    public override string Title => "Backups";
    protected override bool NeedsApi => false;

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        if (s != null && s.Api != ApiAvailability.Ok && s.Api != ApiAvailability.Unknown && _grid.Rows.Count == 0)
            Say(_result, "The backup list needs the update that adds the admin API. \"Back up now\" still works through the control script.");
    }

    private List<string> SelectedNames() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => Convert.ToString(r.Cells[0].Value) ?? string.Empty).Where(n => n.Length > 0).ToList();

    public override async Task RefreshAsync()
    {
        if (Ctx.Latest != null && Ctx.Latest.Api != ApiAvailability.Ok) return;
        var result = await Ctx.Session.GetBackupsAsync(Ctx.Cancel);
        if (!result.IsOk) return;
        _data = result.Value;
        var selected = SelectedNames().ToHashSet();
        var newest = BackupManagement.NewestArchive(_data.Backups);
        Ui.Fill(_grid, _data.Backups.Select(b => new object[] { b.Name, Fmt.Bytes(b.Bytes), Fmt.LocalTime(b.CreatedUtc), b.Kind + (b.Name == newest ? " (newest, protected)" : "") }));
        foreach (DataGridViewRow row in _grid.Rows) row.Selected = selected.Contains(Convert.ToString(row.Cells[0].Value) ?? string.Empty);
        Say(_summary, BackupManagement.Summary(_data));
        Say(_retention, BackupManagement.RetentionLine(_data.Policy));
    }

    private async Task BackupAsync()
    {
        _backupNow.Enabled = false;
        _result.ForeColor = Theme.Muted;
        Say(_result, "Backing up...");
        try
        {
            var text = await Ctx.Actions.BackupAsync();
            _result.ForeColor = Ctx.Actions.LastBackup != null ? Theme.Green : Theme.Amber;
            Say(_result, text);
            var last = Ctx.Actions.LastBackup;
            if (last != null) Say(_excluded, "Left out of the backup: " + (last.Excluded.Count == 0 ? "nothing" : string.Join(", ", last.Excluded)) + (last.Skipped.Count > 0 ? ".  Skipped (in use): " + string.Join(", ", last.Skipped) : "") + (last.Pruned.Count > 0 ? ".  Pruned old backups: " + last.Pruned.Count : ""));
            await SafeRefreshAsync();
        }
        finally
        {
            _backupNow.Enabled = true;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedNames();
        var plan = _data == null ? new BackupManagement.DeletePlan() : BackupManagement.PlanDelete(_data, selected);
        if (plan.IsEmpty)
        {
            _result.ForeColor = Theme.Amber;
            Say(_result, BackupManagement.EmptyReason(plan, selected.Count));
            return;
        }

        var (text, ok) = await Ctx.Actions.DeleteBackupsAsync(plan);
        _result.ForeColor = ok ? Theme.Green : Theme.Amber;
        Say(_result, text);
        await SafeRefreshAsync();
    }

    private async Task PruneAsync()
    {
        var (text, ok) = await Ctx.Actions.PruneBackupsAsync(_includeZips.Checked);
        _result.ForeColor = ok ? Theme.Green : Theme.Amber;
        Say(_result, text);
        await SafeRefreshAsync();
    }

    /// <summary>--mock --screenshot: selects the newest (protected) backup and two older ones.</summary>
    internal BackupManagement.DeletePlan DemoSelect()
    {
        if (_grid.Rows.Count < 4) return new BackupManagement.DeletePlan();
        _grid.ClearSelection();
        foreach (var i in new[] { 0, 2, 3 }) _grid.Rows[i].Selected = true;
        _result.ForeColor = Theme.Muted;
        Say(_result, "3 backups selected. The newest archive backup is protected and is kept.");
        return BackupManagement.PlanDelete(_data, SelectedNames());
    }
}

internal sealed class ReportsPage : Page
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _info = Ui.Wrap("", true);
    private readonly Button _fetch = Ui.Button("Fetch and analyze...", ButtonKind.Primary);

    public ReportsPage(PageContext ctx) : base(ctx)
    {
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => await SafeRefreshAsync();
        _fetch.Click += (_, _) => RunFetchScript();
        _fetch.Visible = FetchAvailable;
        var toolbar = Ui.Bar(new Control[] { refresh, _fetch, _info }, _info);
        Ui.Columns(_grid, ("Time", 22, false), ("Player", 16, false), ("Reason", 12, false), ("Size", 10, true), ("File", 40, false));
        var stack = new VStack();
        stack.Auto(toolbar);
        stack.Fill(_grid, Spacing.M);
        Body.Controls.Add(stack);
    }

    private bool FetchAvailable => Ctx.Session.Profile.IsRemote && !string.IsNullOrWhiteSpace(Ctx.Settings.FetchLogsScriptPath) && File.Exists(Ctx.Settings.FetchLogsScriptPath);

    public override string Title => "Reports";

    public override async Task RefreshAsync()
    {
        _fetch.Visible = FetchAvailable;
        var result = await Ctx.Session.GetReportsAsync(Ctx.Cancel);
        if (!result.IsOk) return;
        var r = result.Value;
        _info.Text = $"{r.ZipCount} zip file{(r.ZipCount == 1 ? "" : "s")} on the server, {r.Entries.Count} report{(r.Entries.Count == 1 ? "" : "s")} listed (newest first)";
        Ui.Fill(_grid, r.Entries.Select(e => new object[] { Fmt.LocalTimeText(e.Utc), e.Player, e.Reason, Fmt.Bytes(e.Bytes), e.File }));
    }

    /// <summary>Opens a normal PowerShell window (so the user sees the progress) running the fetch script with -Analyze.</summary>
    private void RunFetchScript()
    {
        var script = Ctx.Settings.FetchLogsScriptPath;
        try
        {
            var psi = ProcessRunner.Build(ProcessRunner.PowerShellPath(), new[] { "-NoExit", "-ExecutionPolicy", "Bypass", "-File", script, "-Analyze" }, redirect: false);
            psi.CreateNoWindow = false;
            psi.WindowStyle = ProcessWindowStyle.Normal;
            psi.WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory;
            using var process = Process.Start(psi);
            Ctx.Activity("Fetch client logs", "opened a PowerShell window running " + Path.GetFileName(script) + " -Analyze");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ctx.Activity("Fetch client logs", "could not start: " + e.Message, true);
        }
    }
}

internal sealed class ActionsPage : Page
{
    private readonly ComboBox _kickPlayer = Ui.Combo(180), _logsPlayer = Ui.Combo(180);
    private readonly TextBox _say = Ui.TextBox(160), _reason = Ui.TextBox(160), _logsNote = Ui.TextBox(160);
    private readonly DataGridView _activity = Ui.Grid();
    private readonly List<Button> _all = new();
    private Button _sayButton, _kickButton, _logsButton;
    private string _playersKey = "";

    public ActionsPage(PageContext ctx) : base(ctx)
    {
        Button Add(Button b, Func<Task> action)
        {
            b.Click += async (_, _) => await action();
            _all.Add(b);
            return b;
        }

        // One aligned form: a caption column, a first input (the player), a stretching input, and a button column that is as wide as the widest button.
        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, BackColor = Theme.Bg, Margin = Padding.Empty };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Place(Control control, int column, int row, int span, bool stretch)
        {
            var last = column + span >= 4;
            control.Margin = new Padding(0, Spacing.Xs, last ? 0 : Spacing.S, Spacing.Xs);
            control.Anchor = stretch ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            form.Controls.Add(control, column, row);
            if (span > 1) form.SetColumnSpan(control, span);
        }

        FlowLayoutPanel Group(params Button[] buttons)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Bg, Margin = Padding.Empty };
            flow.Controls.AddRange(buttons);
            return flow;
        }

        var row = 0;
        void Caption(string text) => Place(Ui.Label(text, true, FontRole.Bold), 0, row, 1, false);
        void Next() { row++; }

        void Rows(int count)
        {
            for (var i = 0; i < count; i++) form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        Rows(Ctx.Session.Profile.IsRemote ? 5 : 4);
        form.RowCount = Ctx.Session.Profile.IsRemote ? 5 : 4;

        Caption("SERVER");
        Place(Group(
            Add(Ui.Button("Start", ButtonKind.Primary, 100), () => Ctx.Actions.ControlAsync("Start")),
            Add(Ui.Button("Stop", ButtonKind.Danger, 100), () => Ctx.Actions.ControlAsync("Stop")),
            Add(Ui.Button("Restart", ButtonKind.Normal, 100), () => Ctx.Actions.ControlAsync("Restart"))), 1, row, 3, false);
        Next();

        Caption("SAY TO EVERYONE");
        _sayButton = Add(Ui.Button("Send", ButtonKind.Normal, 110), async () =>
        {
            if (await Ctx.Actions.SayAsync(_say.Text)) _say.Clear();
        });
        _say.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (await Ctx.Actions.SayAsync(_say.Text)) _say.Clear();
        };
        Place(_say, 1, row, 2, true);
        Place(_sayButton, 3, row, 1, true);
        Next();

        Caption("KICK A PLAYER");
        _kickButton = Add(Ui.Button("Kick", ButtonKind.Danger, 110), () => Ctx.Actions.KickAsync(_kickPlayer.SelectedItem as string, _reason.Text));
        _reason.PlaceholderText = "reason (optional)";
        Place(_kickPlayer, 1, row, 1, false);
        Place(_reason, 2, row, 1, true);
        Place(_kickButton, 3, row, 1, true);
        Next();

        Caption("REQUEST LOGS");
        _logsButton = Add(Ui.Button("Request logs", ButtonKind.Normal, 110), async () =>
        {
            if (await Ctx.Actions.RequestLogsAsync(_logsPlayer.SelectedItem as string, _logsNote.Text)) _logsNote.Clear();
        });
        _logsButton.Enabled = false;
        _logsNote.MaxLength = RequestLogsBody.MaxNoteLength;
        _logsNote.PlaceholderText = "note for the player (optional)";
        _logsPlayer.SelectedIndexChanged += (_, _) => UpdateApiButtons();
        Place(_logsPlayer, 1, row, 1, false);
        Place(_logsNote, 2, row, 1, true);
        Place(_logsButton, 3, row, 1, true);
        Next();

        if (Ctx.Session.Profile.IsRemote)
        {
            Caption("ADVANCED");
            Place(Group(
                Add(Ui.Button("Restart server PC...", ButtonKind.Danger), () => Ctx.Actions.RestartPcAsync()),
                Add(Ui.Button("Abort restart"), () => Ctx.Actions.AbortPcRestartAsync())), 1, row, 3, false);
        }

        Ui.Columns(_activity, ("Time", 14, false), ("Action", 18, false), ("Result", 68, false));
        // Entries that arrive while the Actions tab is hidden cannot scroll the list; show the newest one when it appears.
        _activity.VisibleChanged += (_, _) => Ui.ScrollTo(_activity, Math.Max(0, _activity.Rows.Count - 1));
        Ctx.Session.Log.Added += OnActivity;
        foreach (var entry in Ctx.Session.Log.Snapshot()) AddRow(entry);

        var stack = new VStack();
        stack.Auto(form);
        var heading = Ui.Heading("ACTIVITY");
        stack.Auto(heading, Spacing.M);
        heading.Margin = new Padding(0, Spacing.M, 0, Spacing.Xs);
        stack.Fill(_activity);
        Body.Controls.Add(stack);
        Ctx.Actions.BusyChanged += busy =>
        {
            foreach (var b in _all) b.Enabled = !busy;
            if (!busy) UpdateApiButtons();
        };
    }

    public override string Title => "Actions";
    protected override bool NeedsApi => false;
    protected override int MinContentHeight => 340;

    private void OnActivity(ActivityEntry entry)
    {
        if (InvokeRequired) BeginInvoke(() => AddRow(entry));
        else AddRow(entry);
    }

    private void AddRow(ActivityEntry entry)
    {
        var index = _activity.Rows.Add(entry.Time.ToString("HH:mm:ss"), entry.Action, entry.Result);
        if (entry.Failed) _activity.Rows[index].DefaultCellStyle.ForeColor = Theme.Red;
        if (_activity.Rows.Count > 300) _activity.Rows.RemoveAt(0);
        Ui.ScrollTo(_activity, Math.Max(0, _activity.Rows.Count - 1));
    }

    private void UpdateApiButtons()
    {
        var ok = Ctx.Latest?.Api == ApiAvailability.Ok;
        _sayButton.Enabled = _kickButton.Enabled = ok && !Ctx.Actions.Busy;
        _logsButton.Enabled = ok && !Ctx.Actions.Busy && _logsPlayer.SelectedItem != null;
    }

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        UpdateApiButtons();
        var names = s?.Api == ApiAvailability.Ok ? s.Status.Players.Select(p => p.Name ?? "?").ToList() : new List<string>();
        var key = string.Join("\n", names);
        if (key == _playersKey) return;
        _playersKey = key;
        FillPlayers(_kickPlayer, names);
        FillPlayers(_logsPlayer, names);
        UpdateApiButtons();
    }

    private static void FillPlayers(ComboBox combo, List<string> names)
    {
        var selected = combo.SelectedItem as string;
        combo.Items.Clear();
        combo.Items.AddRange(names.Cast<object>().ToArray());
        combo.SelectedItem = selected != null && names.Contains(selected) ? selected : (names.Count > 0 ? names[0] : null);
    }
}
