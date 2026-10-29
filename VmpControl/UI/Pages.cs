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

    /// <summary>The page's own controls go in here. When the admin API is not available it is hidden and <see cref="_message"/> takes its place.</summary>
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };

    protected Page(PageContext ctx, int pollMs = 0)
    {
        Ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(14, 12, 14, 8);
        Visible = false;
        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            BackColor = Theme.Bg,
            Font = Theme.Big,
            Visible = false,
        };
        Controls.Add(Body);
        Controls.Add(_message);
        if (pollMs > 0)
        {
            _timer = new System.Windows.Forms.Timer { Interval = pollMs };
            _timer.Tick += async (_, _) => await SafeRefreshAsync();
        }
    }

    public abstract string Title { get; }
    protected virtual bool NeedsApi => true;
    public bool IsActive => _active;

    public virtual Task RefreshAsync() => Task.CompletedTask;

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
                Build();
                ScaleBuilt();
            }

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

    /// <summary>Called for every status poll, also while this page is hidden (cheap bookkeeping only, no painting).</summary>
    public virtual void ObserveSnapshot(PollSnapshot s)
    {
    }

    /// <summary>Controls added after the form was first laid out missed its DPI scaling; scale them once, by the form's own factor.</summary>
    private void ScaleBuilt()
    {
        var form = FindForm();
        if (form == null || !form.IsHandleCreated || DeviceDpi == 96 || Math.Abs(form.CurrentAutoScaleDimensions.Width - form.AutoScaleDimensions.Width) > 0.5f) return; // not scaled yet: the form's first layout will scale this page too
        var factor = new SizeF(DeviceDpi / 96f, DeviceDpi / 96f);
        foreach (Control child in Body.Controls) child.Scale(factor);
    }

    /// <summary>A message that replaces the page, such as "this server needs the update" for one missing endpoint. Null removes it.</summary>
    protected void SetNotice(string text)
    {
        _notice = text;
        UpdateOverlay(Ctx.Latest, always: true);
    }

    public async Task SafeRefreshAsync()
    {
        if (_busy || !_active || !CanRefresh()) return;
        _busy = true;
        try
        {
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Ctx.Activity(Title, "refresh failed: " + e.Message, true);
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

    protected static Panel Section(string heading, Control body, out Label label)
    {
        label = new Label { Text = heading, Dock = DockStyle.Top, Height = 26, Font = Theme.Bold, ForeColor = Theme.Muted, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(2, 0, 0, 4) };
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), BackColor = Theme.Bg };
        body.Dock = DockStyle.Fill;
        panel.Controls.Add(body);
        panel.Controls.Add(label);
        return panel;
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
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6, BackColor = Theme.Bg };
        for (var i = 0; i < 4; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var first = new[] { _server, _mode, _version, _uptime, _cpu, _memory, _vessels, _universe };
        for (var i = 0; i < first.Length; i++) Place(table, first[i], i % 4, i / 4);
        var machineHeading = Ui.Heading("SERVER PC");
        table.Controls.Add(machineHeading, 0, 2);
        table.SetColumnSpan(machineHeading, 4);
        var machine = new[] { _ram, _ramFree, _disk, _osUptime };
        for (var i = 0; i < machine.Length; i++) Place(table, machine[i], i, 3);
        table.Controls.Add(_playersHeading, 0, 4);
        table.SetColumnSpan(_playersHeading, 4);
        Ui.Columns(_players, ("Player", 40, false), ("Subspace", 15, true), ("Connected for", 25, true), ("Since", 30, true));
        _players.Margin = new Padding(0, 0, 10, 0);
        table.Controls.Add(_players, 0, 5);
        table.SetColumnSpan(_players, 4);
        Body.Controls.Add(table);
    }

    private static void Place(TableLayoutPanel table, StatCard card, int column, int row)
    {
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 10, 10);
        table.Controls.Add(card, column, row);
    }

    public override string Title => "Overview";
    protected override bool NeedsApi => false;

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
    private readonly DataGridView _out = Ui.Grid(), _in = Ui.Grid(), _snapshots = Ui.Grid(), _clients = Ui.Grid();
    private readonly Label _totals = Ui.Label("", true);
    private readonly Label _outHeading, _inHeading;
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

        _spark.Margin = new Padding(20, 0, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, BackColor = Theme.Bg };
        toolbar.Controls.AddRange(_ranges);
        toolbar.Controls.Add(_spark);
        toolbar.Controls.Add(new Label { Text = "blue: sent  green: received", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Margin = new Padding(6, 14, 0, 0) });
        _totals.Dock = DockStyle.Top;
        _totals.AutoSize = false;
        _totals.Height = 24;
        _totals.TextAlign = ContentAlignment.MiddleLeft;

        Ui.Columns(_out, ("Message type", 45, false), ("Bytes", 20, true), ("Messages", 17, true), ("Bytes/s", 18, true));
        Ui.Columns(_in, ("Message type", 45, false), ("Bytes", 20, true), ("Messages", 17, true), ("Bytes/s", 18, true));
        Ui.Columns(_snapshots, ("Domain", 14, false), ("Snapshots", 12, true), ("Raw", 14, true), ("Sent", 14, true), ("Ratio", 10, true), ("Encodings", 36, false));
        Ui.Columns(_clients, ("Player", 18, false), ("Bytes in", 13, true), ("Bytes out", 13, true), ("Wire in", 13, true), ("Wire out", 13, true), ("Latency", 10, true), ("Build", 20, false));

        var types = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
        types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        types.Controls.Add(Section("SENT BY THE SERVER", _out, out _outHeading), 0, 0);
        types.Controls.Add(Section("RECEIVED BY THE SERVER", _in, out _inHeading), 1, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        body.Controls.Add(types, 0, 0);
        body.Controls.Add(Section("SNAPSHOT COMPRESSION", _snapshots, out _), 0, 1);
        body.Controls.Add(Section("CLIENTS", _clients, out _), 0, 2);

        Body.Controls.Add(body);
        Body.Controls.Add(_totals);
        Body.Controls.Add(toolbar);
    }

    private static void StyleToggle(Button b, bool on)
    {
        b.BackColor = on ? Theme.Accent : Theme.Panel2;
        b.ForeColor = on ? Color.White : Theme.Text;
        b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
    }

    public override string Title => "Network";

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
            return;
        }

        FillTypes(_out, _outHeading, "SENT BY THE SERVER", window.Out);
        FillTypes(_in, _inHeading, "RECEIVED BY THE SERVER", window.In);
        var snapshots = window.Snapshots.Count > 0 ? window.Snapshots : n.SnapshotCompression;
        Ui.Fill(_snapshots, snapshots.Select(s => new object[]
        {
            s.Domain, Fmt.Count(s.Snapshots), Fmt.Bytes(s.RawBytes), Fmt.Bytes(s.WireBytes), Fmt.Ratio(s.Ratio), s.Encodings?.Summary() ?? "-",
        }));
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
    private readonly TextBox _filter = Ui.TextBox(240);
    private readonly Button _pause = Ui.Button("Pause");
    private readonly ComboBox _count = Ui.Combo(80);
    private readonly Label _info = Ui.Label("", true);
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
            _info.Text = _paused ? "paused" : "";
        };
        _filter.TextChanged += (_, _) => Render(force: true);
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => { _signature = ""; await SafeRefreshAsync(); };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Bg };
        toolbar.Controls.Add(_pause);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(Ui.Label("Filter", true));
        toolbar.Controls.Add(_filter);
        toolbar.Controls.Add(Ui.Label("Lines", true));
        toolbar.Controls.Add(_count);
        toolbar.Controls.Add(_info);
        foreach (Control c in toolbar.Controls)
        {
            if (c is Label) c.Margin = new Padding(6, 7, 6, 0);
        }

        _box.Dock = DockStyle.Fill;
        _box.ReadOnly = true;
        _box.BackColor = Theme.Panel;
        _box.ForeColor = Theme.Text;
        _box.BorderStyle = BorderStyle.None;
        _box.Font = Theme.Mono;
        _box.WordWrap = false;
        _box.DetectUrls = false;
        _box.HideSelection = false;
        _box.ScrollBars = RichTextBoxScrollBars.Both;
        Native.DarkScrollbars(_box);
        Body.Controls.Add(_box);
        Body.Controls.Add(toolbar);
    }

    public override string Title => "Logs";

    public override async Task RefreshAsync()
    {
        if (_paused) return;
        var lines = int.Parse((string)_count.SelectedItem, System.Globalization.CultureInfo.InvariantCulture);
        var result = await Ctx.Session.GetLogAsync(Math.Min(lines, MaxShown), Ctx.Cancel);
        if (!result.IsOk)
        {
            _info.Text = "log unavailable: " + result.Error;
            return;
        }

        _fetched = result.Value.Lines;
        var signature = _fetched.Count + "|" + (_fetched.Count > 0 ? _fetched[0] + "|" + _fetched[^1] : "");
        if (signature == _signature) return;
        _signature = signature;
        Render(force: false);
        _info.Text = $"{_shown.Count} lines, updated {DateTime.Now:HH:mm:ss}";
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
    private readonly Label _result = Ui.Label("No backup made from this window yet.", true);
    private readonly Label _excluded = Ui.Label("The items a backup leaves out are listed here after one has run.", true);

    public BackupsPage(PageContext ctx) : base(ctx)
    {
        _backupNow.Click += async (_, _) => await BackupAsync();
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => await SafeRefreshAsync();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Bg };
        toolbar.Controls.Add(_backupNow);
        toolbar.Controls.Add(refresh);

        _result.AutoSize = false;
        _excluded.AutoSize = false;
        _result.Dock = DockStyle.Top;
        _excluded.Dock = DockStyle.Top;
        _result.Height = 44;
        _excluded.Height = 40;
        Ui.Columns(_grid, ("Name", 50, false), ("Size", 14, true), ("Created", 24, true), ("Kind", 12, false));
        _grid.Dock = DockStyle.Fill;
        Body.Controls.Add(_grid);
        Body.Controls.Add(_excluded);
        Body.Controls.Add(_result);
        Body.Controls.Add(toolbar);
    }

    public override string Title => "Backups";
    protected override bool NeedsApi => false;

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        if (s != null && s.Api != ApiAvailability.Ok && s.Api != ApiAvailability.Unknown && _grid.Rows.Count == 0)
            _result.Text = "The backup list needs the update that adds the admin API. \"Back up now\" still works through the control script.";
    }

    public override async Task RefreshAsync()
    {
        if (Ctx.Latest != null && Ctx.Latest.Api != ApiAvailability.Ok) return;
        var result = await Ctx.Session.GetBackupsAsync(Ctx.Cancel);
        if (!result.IsOk) return;
        Ui.Fill(_grid, result.Value.Backups.Select(b => new object[] { b.Name, Fmt.Bytes(b.Bytes), Fmt.LocalTime(b.CreatedUtc), b.Kind }));
    }

    private async Task BackupAsync()
    {
        _backupNow.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "Backing up...";
        try
        {
            var text = await Ctx.Actions.BackupAsync();
            _result.ForeColor = Ctx.Actions.LastBackup != null ? Theme.Green : Theme.Amber;
            _result.Text = text;
            var last = Ctx.Actions.LastBackup;
            if (last != null) _excluded.Text = "Left out of the backup: " + (last.Excluded.Count == 0 ? "nothing" : string.Join(", ", last.Excluded)) + (last.Skipped.Count > 0 ? ".  Skipped (in use): " + string.Join(", ", last.Skipped) : "") + (last.Pruned.Count > 0 ? ".  Pruned old backups: " + last.Pruned.Count : "");
            await SafeRefreshAsync();
        }
        finally
        {
            _backupNow.Enabled = true;
        }
    }
}

internal sealed class ReportsPage : Page
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _info = Ui.Label("", true);
    private readonly Button _fetch = Ui.Button("Fetch and analyze...", ButtonKind.Primary);

    public ReportsPage(PageContext ctx) : base(ctx)
    {
        var refresh = Ui.Button("Refresh");
        refresh.Click += async (_, _) => await SafeRefreshAsync();
        _fetch.Click += (_, _) => RunFetchScript();
        _fetch.Visible = FetchAvailable;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Bg };
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_fetch);
        _info.Margin = new Padding(8, 7, 0, 0);
        toolbar.Controls.Add(_info);
        Ui.Columns(_grid, ("Time", 22, false), ("Player", 16, false), ("Reason", 12, false), ("Size", 10, true), ("File", 40, false));
        Body.Controls.Add(_grid);
        Body.Controls.Add(toolbar);
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
    private readonly ComboBox _kickPlayer = Ui.Combo(180);
    private readonly TextBox _say = Ui.TextBox(420), _reason = Ui.TextBox(260);
    private readonly DataGridView _activity = Ui.Grid();
    private readonly List<Button> _all = new();
    private Button _sayButton, _kickButton;
    private string _playersKey = "";

    public ActionsPage(PageContext ctx) : base(ctx)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };

        Button Add(Button b, Func<Task> action)
        {
            b.Click += async (_, _) => await action();
            _all.Add(b);
            return b;
        }

        flow.Controls.Add(Ui.Heading("SERVER"));
        flow.Controls.Add(Ui.Row(
            Add(Ui.Button("Start", ButtonKind.Primary, 100), () => Ctx.Actions.ControlAsync("Start")),
            Add(Ui.Button("Stop", ButtonKind.Danger, 100), () => Ctx.Actions.ControlAsync("Stop")),
            Add(Ui.Button("Restart", ButtonKind.Normal, 100), () => Ctx.Actions.ControlAsync("Restart"))));

        flow.Controls.Add(Ui.Heading("SAY TO EVERYONE"));
        _sayButton = Add(Ui.Button("Send"), async () =>
        {
            if (await Ctx.Actions.SayAsync(_say.Text)) _say.Clear();
        });
        _say.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (await Ctx.Actions.SayAsync(_say.Text)) _say.Clear();
        };
        flow.Controls.Add(Ui.Row(_say, _sayButton));

        flow.Controls.Add(Ui.Heading("KICK A PLAYER"));
        _kickButton = Add(Ui.Button("Kick", ButtonKind.Danger), () => Ctx.Actions.KickAsync(_kickPlayer.SelectedItem as string, _reason.Text));
        _reason.PlaceholderText = "reason (optional)";
        flow.Controls.Add(Ui.Row(_kickPlayer, _reason, _kickButton));

        if (Ctx.Session.Profile.IsRemote)
        {
            flow.Controls.Add(Ui.Heading("ADVANCED"));
            flow.Controls.Add(Ui.Row(
                Add(Ui.Button("Restart server PC...", ButtonKind.Danger), () => Ctx.Actions.RestartPcAsync()),
                Add(Ui.Button("Abort restart"), () => Ctx.Actions.AbortPcRestartAsync())));
        }

        flow.Controls.Add(Ui.Heading("ACTIVITY"));

        Ui.Columns(_activity, ("Time", 14, false), ("Action", 18, false), ("Result", 68, false));
        _activity.Dock = DockStyle.Fill;
        Ctx.Session.Log.Added += OnActivity;
        foreach (var entry in Ctx.Session.Log.Snapshot()) AddRow(entry);
        Body.Controls.Add(_activity);
        Body.Controls.Add(flow);
        Ctx.Actions.BusyChanged += busy =>
        {
            foreach (var b in _all) b.Enabled = !busy;
            if (!busy) UpdateApiButtons();
        };
    }

    public override string Title => "Actions";
    protected override bool NeedsApi => false;

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
        _activity.FirstDisplayedScrollingRowIndex = Math.Max(0, _activity.Rows.Count - 1);
    }

    private void UpdateApiButtons()
    {
        var ok = Ctx.Latest?.Api == ApiAvailability.Ok;
        _sayButton.Enabled = _kickButton.Enabled = ok && !Ctx.Actions.Busy;
    }

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        UpdateApiButtons();
        var names = s?.Api == ApiAvailability.Ok ? s.Status.Players.Select(p => p.Name ?? "?").ToList() : new List<string>();
        var key = string.Join("\n", names);
        if (key == _playersKey) return;
        _playersKey = key;
        var selected = _kickPlayer.SelectedItem as string;
        _kickPlayer.Items.Clear();
        _kickPlayer.Items.AddRange(names.Cast<object>().ToArray());
        _kickPlayer.SelectedItem = selected != null && names.Contains(selected) ? selected : (names.Count > 0 ? names[0] : null);
    }
}
