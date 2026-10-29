using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using VmpControl.Core;

namespace VmpControl.UI;

internal static class Memory
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>Hands back everything that can be re-read later. Called once each time the window goes to the tray, never in a loop.</summary>
    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VmpControl";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Everything that changes the server (or asks to). Both the Actions tab and the tray menu go through here, so the confirmations and the activity log are identical.</summary>
internal sealed class ActionService
{
    private readonly PageContext _ctx;
    private readonly Func<Form> _owner;

    public bool Busy { get; private set; }
    public BackupResultDto LastBackup { get; private set; }
    public event Action<bool> BusyChanged;

    public ActionService(PageContext ctx, Func<Form> owner)
    {
        _ctx = ctx;
        _owner = owner;
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        BusyChanged?.Invoke(busy);
    }

    private bool Confirm(string title, string message, string okText, bool danger, string required = null)
    {
        using var dialog = new PromptForm(title, message, okText, required, danger);
        var owner = _owner();
        if (owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized) return dialog.ShowDialog(owner) == DialogResult.OK;
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.TopMost = true;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    private (int? Count, List<string> Names) Players()
    {
        var s = _ctx.Latest;
        if (s == null) return (null, new List<string>());
        if (s.Api == ApiAvailability.Ok) return (s.Status.PlayerCount, s.Status.Players.Select(p => p.Name ?? "?").ToList());
        return (s.Script?.Players, s.Script?.Names ?? new List<string>());
    }

    private static string Summarize(ProcResult r)
    {
        if (r.StartError != null) return r.StartError;
        if (r.TimedOut) return "timed out";
        var text = (r.Output + "\n" + r.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (text.Length > 200) text = text.Substring(0, 200);
        return r.ExitCode == 0 ? (text.Length == 0 ? "done" : "done: " + text) : $"failed (exit {r.ExitCode}){(text.Length == 0 ? "" : ": " + text)}";
    }

    /// <summary>Start, Stop or Restart through the control script, after asking (and naming the players who would be disconnected).</summary>
    public async Task ControlAsync(string action)
    {
        if (Busy) return;
        if (action is "Stop" or "Restart")
        {
            var (count, names) = Players();
            var verb = action == "Stop" ? "Stop" : "Restart";
            var who = count is > 0 ? $"{count} player{(count == 1 ? " is" : "s are")} online{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")}. {verb}ing the server disconnects {(count == 1 ? "them" : "them")}.\n\n{verb} the server anyway?"
                : count == 0 ? $"Nobody is online.\n\n{verb} the server?"
                : $"Could not check who is online.\n\n{verb} the server?";
            if (!Confirm(verb + " server", who, verb, danger: true))
            {
                _ctx.Activity(action, "cancelled");
                return;
            }
        }

        SetBusy(true);
        _ctx.Activity(action, "started...");
        try
        {
            var result = await _ctx.Session.RunControlAsync(action, _ctx.Cancel);
            _ctx.Activity(action, Summarize(result), !result.Ok);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
            _ctx.Poller.RefreshNow();
        }
    }

    /// <summary>Backs the universe up (through the API when it answers, else the control script). Returns a one-line result for the Backups tab.</summary>
    public async Task<string> BackupAsync()
    {
        if (Busy) return "Busy with another action.";
        SetBusy(true);
        LastBackup = null;
        _ctx.Activity("Backup", "started...");
        try
        {
            if (_ctx.Latest?.Api == ApiAvailability.Ok)
            {
                var r = await _ctx.Session.CreateBackupAsync(_ctx.Cancel);
                if (!r.IsOk)
                {
                    _ctx.Activity("Backup", "failed: " + r.Error, true);
                    return "Backup failed: " + r.Error;
                }

                LastBackup = r.Value;
                var text = $"{r.Value.FileName}: {Fmt.Bytes(r.Value.RawBytes)} -> {Fmt.Bytes(r.Value.CompressedBytes)} ({Fmt.Ratio(r.Value.Ratio)}) in {r.Value.Seconds:0.0} s, {Fmt.Count(r.Value.Files)} files{(r.Value.Verified ? ", verified" : "")}";
                _ctx.Activity("Backup", text);
                return text;
            }

            var script = await _ctx.Session.RunControlAsync("Backup", _ctx.Cancel);
            var line = Summarize(script);
            _ctx.Activity("Backup (control script)", line, !script.Ok);
            return "Backup through the control script: " + line;
        }
        catch (OperationCanceledException)
        {
            return "Cancelled.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task<bool> SayAsync(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;
        if (_ctx.Latest?.Api != ApiAvailability.Ok)
        {
            _ctx.Activity("Say", "needs the admin API", true);
            return false;
        }

        var r = await _ctx.Session.SayAsync(text, _ctx.Cancel);
        _ctx.Activity("Say", r.IsOk ? "sent: " + text : "failed: " + r.Error, !r.IsOk);
        return r.IsOk;
    }

    public async Task KickAsync(string name, string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _ctx.Activity("Kick", "pick a player first", true);
            return;
        }

        if (!Confirm("Kick player", $"Kick {name}?" + (string.IsNullOrWhiteSpace(reason) ? "" : "\n\nReason: " + reason.Trim()), "Kick", danger: true))
        {
            _ctx.Activity("Kick", "cancelled");
            return;
        }

        var r = await _ctx.Session.KickAsync(name, (reason ?? "").Trim(), _ctx.Cancel);
        _ctx.Activity("Kick " + name, r.IsOk ? "kicked" : "failed: " + r.Error, !r.IsOk);
    }

    /// <summary>Asks one connected player's game to send its logs to the server. Returns true when the server accepted the request.</summary>
    public async Task<bool> RequestLogsAsync(string name, string note)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _ctx.Activity("Request logs", "pick a player first", true);
            return false;
        }

        if (Busy) return false;
        if (_ctx.Latest?.Api != ApiAvailability.Ok)
        {
            _ctx.Activity("Request logs", "needs the admin API", true);
            return false;
        }

        SetBusy(true);
        try
        {
            var r = await _ctx.Session.RequestLogsAsync(name, note, _ctx.Cancel);
            var (text, failed) = RequestLogsText.Describe(name, r);
            _ctx.Activity("Request logs " + name, text, failed);
            return !failed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task RestartPcAsync()
    {
        var profile = _ctx.Session.Profile;
        if (!profile.IsRemote) return;
        var (count, names) = Players();
        var message = $"This restarts the server PC \"{profile.SshAlias}\" ({profile.Name}) in 30 seconds. The VMP server goes down with it" +
                      (count is > 0 ? $" and {count} player{(count == 1 ? "" : "s")} ({string.Join(", ", names)}) lose the connection" : "") +
                      ".\n\nType RESTART to confirm. \"Abort restart\" cancels it during the 30 seconds.";
        if (!Confirm("Restart server PC", message, "Restart PC", danger: true, required: "RESTART"))
        {
            _ctx.Activity("Restart server PC", "cancelled");
            return;
        }

        var r = await _ctx.Session.RestartPcAsync(_ctx.Cancel);
        _ctx.Activity("Restart server PC", r.Ok ? "scheduled in 30 s on " + profile.SshAlias : Summarize(r), !r.Ok);
    }

    public async Task AbortPcRestartAsync()
    {
        var r = await _ctx.Session.AbortRestartPcAsync(_ctx.Cancel);
        _ctx.Activity("Abort restart", r.Ok ? "aborted" : Summarize(r), !r.Ok);
    }
}

internal sealed class MainForm : Form
{
    private readonly CommandLineOptions _options;
    private readonly string _settingsPath;
    private readonly EventWaitHandle _showEvent;
    private readonly bool _screenshotMode;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miStart = new("Start"), _miStop = new("Stop"), _miRestart = new("Restart"), _miBackup = new("Back up now"), _miAuto = new("Start with Windows");
    private readonly NotificationTracker _notifications = new();
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = 1500 };
    private readonly TabBar _tabs = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Panel };
    private readonly Label _title = new() { AutoSize = true, Font = Theme.Big, ForeColor = Theme.Text, Location = new Point(46, 6), BackColor = Color.Transparent };
    private readonly Label _subtitle = new() { AutoSize = true, Font = Theme.Base, ForeColor = Theme.Muted, Location = new Point(48, 36), BackColor = Color.Transparent };
    private readonly Label _banner = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Color.FromArgb(40, 28, 0), BackColor = Theme.Amber, Font = Theme.Base, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Muted, BackColor = Theme.Panel, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };
    private readonly List<Page> _pages = new();

    private AppSettings _settings;
    private IServerSession _session;
    private Poller _poller;
    private PageContext _ctx;
    private TrayState? _trayState;
    private string _trayTip;
    private Color _dot = Theme.Grey;
    private bool _exiting;
    private bool _startHidden;
    private TaskCompletionSource _first;

    public MainForm(CommandLineOptions options, AppSettings settings, string settingsPath, Profile profile, EventWaitHandle showEvent)
    {
        _options = options;
        _settings = settings;
        _settingsPath = settingsPath;
        _showEvent = showEvent;
        _screenshotMode = options.ScreenshotFolder != null;
        _startHidden = options.Minimized && !_screenshotMode;

        // No layout until the whole tree exists, so the first layout pass scales everything to the monitor's DPI in one go.
        SuspendLayout();
        Text = "VMP Control";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        // Lay out in 96-dpi units and let Windows Forms scale the whole tree to the monitor.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(860, 560);
        StartPosition = _screenshotMode ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (_screenshotMode) Location = new Point(10, 10);
        DoubleBuffered = true;
        Native.DarkTitleBar(this);

        _header.Controls.Add(_title);
        _header.Controls.Add(_subtitle);
        _header.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_dot);
            e.Graphics.FillEllipse(brush, 18, 21, 18, 18);
        };

        Controls.Add(_content);
        Controls.Add(_tabs);
        Controls.Add(_banner);
        Controls.Add(_header);
        Controls.Add(_status);

        _tabs.SelectedChanged += _ => ActivateCurrentPage();

        if (!_screenshotMode)
        {
            var menu = new ContextMenuStrip();
            _miStart.Click += (_, _) => _ = _ctx.Actions.ControlAsync("Start");
            _miStop.Click += (_, _) => _ = _ctx.Actions.ControlAsync("Stop");
            _miRestart.Click += (_, _) => _ = _ctx.Actions.ControlAsync("Restart");
            _miBackup.Click += (_, _) => _ = _ctx.Actions.BackupAsync();
            _miAuto.Click += (_, _) => ToggleAutostart();
            var open = new ToolStripMenuItem("Open") { Font = new Font(Theme.Base, FontStyle.Bold) };
            open.Click += (_, _) => ShowWindow();
            var settingsItem = new ToolStripMenuItem("Settings...");
            settingsItem.Click += (_, _) => OpenSettings();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (_, _) => ExitApp();
            menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), _miStart, _miStop, _miRestart, _miBackup, new ToolStripSeparator(), settingsItem, _miAuto, new ToolStripSeparator(), exit });
            menu.Opening += (_, _) =>
            {
                _miAuto.Checked = Autostart.IsEnabled();
                var busy = _ctx.Actions.Busy;
                _miStart.Enabled = _miStop.Enabled = _miRestart.Enabled = _miBackup.Enabled = !busy;
            };
            _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "VMP Control" };
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
            };
            ApplyTray(TrayState.Connecting, "VMP Control - connecting...");
        }

        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (!WindowActive) Memory.Trim();
        };

        if (_showEvent != null)
        {
            ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            {
                try
                {
                    BeginInvoke(ShowWindow);
                }
                catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
                {
                }
            }, null, Timeout.Infinite, false);
        }

        BuildSession(profile);
        if (_startHidden) HandleCreated += (_, _) => TrimWhenSettled();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => TearDownSession(disposeTray: true);
        VisibleChanged += (_, _) =>
        {
            if (Visible) ActivateCurrentPage();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) foreach (var p in _pages) p.SetActive(false);
            else if (Visible) ActivateCurrentPage();
        };
        ResumeLayout(false);
    }

    public bool WindowActive => Visible && WindowState != FormWindowState.Minimized;

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }

        base.SetVisibleCore(value);
    }

    private void BuildSession(Profile profile)
    {
        _session = _options.Mock ? new MockSession(profile) : new LiveSession(profile);
        var poller = new Poller(_session, () => WindowActive);
        _poller = poller;
        _ctx = new PageContext { Session = _session, Settings = _settings, Poller = poller };
        _ctx.Actions = new ActionService(_ctx, () => this);
        _pages.AddRange(new Page[] { new OverviewPage(_ctx), new NetworkPage(_ctx), new LogsPage(_ctx), new BackupsPage(_ctx), new ReportsPage(_ctx), new ActionsPage(_ctx), new SettingsPage(_ctx) });
        _first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_tabs.Selected < 0)
        {
            foreach (var page in _pages) _tabs.Add(page.Title);
        }

        foreach (var page in _pages)
        {
            // Pages built after the form was first laid out (a settings change) are not covered by the form's own auto-scaling.
            if (IsHandleCreated) page.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
            _content.Controls.Add(page);
        }

        poller.Updated += OnSnapshot;
        _ctx.Actions.BusyChanged += busy => _status.Text = busy ? "Working..." : _status.Text;
        _session.Log.Added += e => { if (!IsDisposed) _status.Text = $"{e.Time:HH:mm:ss}  {e.Action}: {e.Result}"; };
        _title.Text = profile.Name;
        _subtitle.Text = (_options.Mock ? "Mock data - " : "") + "connecting...";
        var wanted = _pages.FindIndex(p => string.Equals(p.Title, _options.Tab, StringComparison.OrdinalIgnoreCase));
        _tabs.Select(wanted >= 0 && _tabs.Selected < 0 ? wanted : Math.Max(0, _tabs.Selected));
        ActivateCurrentPage();
        poller.Start();
    }

    private void TearDownSession(bool disposeTray)
    {
        _poller?.Stop();
        foreach (var page in _pages)
        {
            page.SetActive(false);
            _content.Controls.Remove(page);
            page.Dispose();
        }

        _pages.Clear();
        _session?.Dispose();
        _session = null;
        if (disposeTray && _tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }

    private void ActivateCurrentPage()
    {
        if (_pages.Count == 0) return;
        for (var i = 0; i < _pages.Count; i++) _pages[i].SetActive(WindowActive && i == _tabs.Selected);
    }

    private void OnSnapshot(PollSnapshot s)
    {
        var state = TrayStateMapper.Map(s);
        ApplyTray(state, TrayStateMapper.Tooltip(s, _session.Profile.Name));
        foreach (var page in _pages) page.ObserveSnapshot(s);
        foreach (var message in _notifications.Process(s))
        {
            if (_tray != null && _settings.Notifications) _tray.ShowBalloonTip(4000, s.Status?.Settings?.ServerName ?? _session.Profile.Name, message, ToolTipIcon.Info);
        }

        if (WindowActive)
        {
            UpdateHeader(s, state);
            if (_tabs.Selected >= 0 && _tabs.Selected < _pages.Count) _pages[_tabs.Selected].OnSnapshot(s);
        }

        if (s.Api != ApiAvailability.Unknown && (s.Api == ApiAvailability.Ok || s.Script != null)) _first?.TrySetResult();
    }

    private void ApplyTray(TrayState state, string tip)
    {
        if (_tray == null) return;
        if (_trayState != state)
        {
            _trayState = state;
            var size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 32);
            _tray.Icon = IconFactory.Get(state, size);
            Icon = IconFactory.Get(state, 32);
        }

        if (_trayTip != tip)
        {
            _trayTip = tip;
            _tray.Text = tip.Length > 127 ? tip.Substring(0, 127) : tip;
        }
    }

    private void UpdateHeader(PollSnapshot s, TrayState state)
    {
        _dot = Theme.ColorOf(state);
        _header.Invalidate();
        var name = s.Status?.Settings?.ServerName ?? _session.Profile.Name;
        _title.Text = name;
        var where = $"{_session.Profile.Name} ({(_session.Profile.IsRemote ? "remote" : "host")})";
        var text = state switch
        {
            TrayState.Connecting => "connecting...",
            TrayState.Down => s.Script?.Running == null ? "server unreachable" : "server is not running",
            _ => s.Api == ApiAvailability.Ok ? $"{s.Status.PlayerCount} online, up {Fmt.Duration(s.Status.UptimeSeconds)}" : $"running, {(s.Script?.Players is int n ? n + " online" : "players unknown")}",
        };
        _subtitle.Text = (_options.Mock ? "MOCK DATA - " : "") + where + " - " + text;

        string banner = null;
        if (s.Api == ApiAvailability.NotSupported) banner = "This server does not have the admin API yet. Start, Stop and Restart work; the other tabs need the server update.";
        else if (s.Api == ApiAvailability.Unauthorized) banner = "The server refused the admin token. Reading it again...";
        else if (s.Api == ApiAvailability.Unreachable && s.Script?.Running == true) banner = "The server is running but its admin API is not reachable" + (string.IsNullOrEmpty(s.TunnelStatus) ? "." : " (SSH tunnel: " + s.TunnelStatus + ").");
        if (banner == null) _banner.Visible = false;
        else
        {
            _banner.Text = banner;
            _banner.Visible = true;
        }
    }

    public void ShowWindow()
    {
        if (_screenshotMode) return;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        ActivateCurrentPage();
        if (_poller.Latest != null) UpdateHeader(_poller.Latest, TrayStateMapper.Map(_poller.Latest));
        _poller.RefreshNow();
    }

    private void HideToTray()
    {
        Hide();
        foreach (var page in _pages) page.SetActive(false);
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_exiting || _screenshotMode || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        HideToTray();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
        Application.Exit();
    }

    private void ToggleAutostart()
    {
        try
        {
            var enable = !Autostart.IsEnabled();
            Autostart.Set(enable);
            _ctx.Activity("Start with Windows", enable ? "on" : "off");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _ctx.Activity("Start with Windows", "could not change: " + e.Message, true);
        }
    }

    private AppSettings CloneSettings() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, AppSettings.JsonOptions), AppSettings.JsonOptions);

    private async Task SaveDialogShotAsync(Form dialog, string path)
    {
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(10, 10);
        dialog.Show();
        await Task.Delay(300);
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
        bitmap.Save(path, ImageFormat.Png);
        dialog.Close();
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(CloneSettings(), _session.Profile.Name);
        var owner = WindowActive ? this : null;
        if (owner == null)
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.TopMost = true;
        }

        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        _settings = dialog.Result;
        try
        {
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Activity("Settings", "could not save: " + e.Message, true);
        }

        TearDownSession(disposeTray: false);
        _trayState = null;
        BuildSession(_settings.Pick(_settings.ActiveProfile));
    }

    /// <summary>--screenshot: waits for the first answer, renders every tab to <paramref name="folder"/>\&lt;tab&gt;.png, then exits.</summary>
    public async Task<int> RunScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var arrived = await Task.WhenAny(_first.Task, Task.Delay(TimeSpan.FromSeconds(40)));
        var code = arrived == _first.Task ? 0 : 2;
        if (code != 0) Console.Error.WriteLine("No answer from the server within 40 seconds; rendering what is there.");
        void Snap(string name)
        {
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            _tabs.Select(i);
            await Task.Delay(250);
            await _pages[i].SafeRefreshAsync();
            if (_pages[i] is NetworkPage) await Task.Delay(1200);
            await _pages[i].SafeRefreshAsync();
            await Task.Delay(350);
            if (_poller.Latest != null)
            {
                UpdateHeader(_poller.Latest, TrayStateMapper.Map(_poller.Latest));
                _pages[i].OnSnapshot(_poller.Latest);
            }

            Snap(_pages[i].Title.ToLowerInvariant());
            if (_pages[i] is NetworkPage network)
            {
                // The Network page scrolls (four tables); network-bottom.png shows the lower two.
                network.ScrollTo(end: true);
                await Task.Delay(300);
                Snap("network-bottom");
                network.ScrollTo(end: false);
            }
        }

        // The Settings page opens on its first section; a server with a Compression section gets that section rendered too (settings-compression.png).
        if (_pages.OfType<SettingsPage>().FirstOrDefault() is { } compressionTab)
        {
            _tabs.Select(_pages.IndexOf(compressionTab));
            await Task.Delay(300);
            if (compressionTab.ShowSection("compression", scrollToEnd: false))
            {
                await Task.Delay(400);
                Snap("settings-compression");
                compressionTab.ShowSection("general", scrollToEnd: false);
            }
        }

        // --mock --mock-edits: the Settings page with pending edits and a validation error, then after a successful save (restart banner).
        if (_options.Mock && _options.MockEdits && _pages.OfType<SettingsPage>().FirstOrDefault() is { } settingsPage)
        {
            _tabs.Select(_pages.IndexOf(settingsPage));
            await Task.Delay(300);
            await settingsPage.DemoEditsAsync();
            await Task.Delay(500);
            Snap("settings-edits");
            settingsPage.ShowSection("general", scrollToEnd: true);
            await Task.Delay(300);
            Snap("settings-secrets");
            settingsPage.ShowSection("general", scrollToEnd: false);
            await settingsPage.DemoFixAsync();
            await Task.Delay(600);
            Snap("settings-saved");
            settingsPage.ShowSection("adminApi", scrollToEnd: false);
            await Task.Delay(300);
            Snap("settings-adminapi");
        }

        // The two dialogs too, so a layout problem in them shows up here and not on the first click.
        using (var settings = new SettingsForm(CloneSettings(), _session.Profile.Name)) await SaveDialogShotAsync(settings, Path.Combine(folder, "settings-dialog.png"));
        using (var prompt = new PromptForm("Restart server PC", "This restarts the server PC \"trainpc\" (Train PC) in 30 seconds. The VMP server goes down with it and 2 players (Vlad, Jeb) lose the connection.\n\nType RESTART to confirm.", "Restart PC", "RESTART", true))
            await SaveDialogShotAsync(prompt, Path.Combine(folder, "restart-dialog.png"));

        _exiting = true;
        Close();
        Application.Exit();
        return code;
    }

    /// <summary>--minimized: after the first answer, hand back the start-up memory once (the window never opened, so there is no "hide" to trigger it).</summary>
    private async void TrimWhenSettled()
    {
        await Task.WhenAny(_first.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        await Task.Delay(1500);
        if (!WindowActive) Memory.Trim();
    }
}
