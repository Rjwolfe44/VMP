using VmpControl.Core;

namespace VmpControl.UI;

/// <summary>Profiles (remote or host), notifications and the reports script. Edits a copy; the caller keeps it only when the dialog returns OK.</summary>
internal sealed class SettingsForm : DialogBase
{
    private readonly AppSettings _settings;
    private readonly ListBox _list = new();
    private readonly ComboBox _active = Ui.Combo(220);
    private readonly TextBox _name = Ui.TextBox(), _alias = Ui.TextBox(), _root = Ui.TextBox(), _script = Ui.TextBox(), _folder = Ui.TextBox(), _fetchScript = Ui.TextBox();
    private readonly ComboBox _mode = Ui.Combo(200);
    private readonly NumericUpDown _remotePort = Port(), _localPort = Port(), _apiPort = Port();
    private readonly CheckBox _notify = new() { Text = "Show notifications (players joined / left, server down / back)", AutoSize = true, ForeColor = Theme.Text, Margin = Padding.Empty };
    private readonly List<(Control Label, Control Input, bool? RemoteOnly)> _rows = new();
    private readonly WrapLabel _problem = new() { ForeColor = Theme.Red, BackColor = Color.Transparent, Visible = false };
    private readonly TableLayoutPanel _form = new();
    private bool _loading;
    private int _index = -1;

    public AppSettings Result => _settings;

    private static NumericUpDown Port()
    {
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        port.Width = 90;
        Ui.MinSize(port, 90);
        return Ui.Style(port, FontRole.Base);
    }

    public SettingsForm(AppSettings settings, string runningProfile) : base("VMP Control settings", 760)
    {
        _settings = settings;
        SuspendLayout();

        // Left: the profile list and the buttons that change it.
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 0, Spacing.L, 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var profiles = Ui.Label("Profiles", true);
        profiles.Margin = new Padding(0, 0, 0, Spacing.Xs);
        left.Controls.Add(profiles, 0, 0);
        left.SetColumnSpan(profiles, 2);
        _list.Dock = DockStyle.Fill;
        _list.Margin = Padding.Empty;
        _list.BackColor = Theme.Panel;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.None;
        Ui.Style(_list, FontRole.Base);
        Ui.MinSize(_list, 190, 200);
        _list.SelectedIndexChanged += (_, _) => SelectProfile(_list.SelectedIndex);
        left.Controls.Add(_list, 0, 1);
        left.SetColumnSpan(_list, 2);

        var addRemote = Ui.Button("Add remote");
        var addHost = Ui.Button("Add host");
        var remove = Ui.Button("Remove");
        addRemote.Dock = addHost.Dock = remove.Dock = DockStyle.Fill;
        addRemote.Margin = new Padding(0, Spacing.S, Spacing.Xs, 0);
        addHost.Margin = new Padding(Spacing.Xs, Spacing.S, 0, 0);
        remove.Margin = new Padding(0, Spacing.S, 0, 0);
        addRemote.Click += (_, _) => AddProfile(new Profile { Name = UniqueName("Remote server"), Mode = ProfileMode.Remote });
        addHost.Click += (_, _) => AddProfile(new Profile { Name = UniqueName("Host server"), Mode = ProfileMode.Host, ServerFolder = Profile.DefaultRemoteRoot });
        remove.Click += (_, _) =>
        {
            if (_settings.Profiles.Count <= 1 || _index < 0) return;
            var removed = _index;
            _index = -1;
            _settings.Profiles.RemoveAt(removed);
            RefreshLists(Math.Max(0, removed - 1));
        };
        left.Controls.Add(addRemote, 0, 2);
        left.Controls.Add(addHost, 1, 2);
        left.Controls.Add(remove, 0, 3);
        left.SetColumnSpan(remove, 2);

        // Right: the form of the chosen profile, then the settings that belong to the whole app.
        _form.Dock = DockStyle.Fill;
        _form.AutoSize = true;
        _form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _form.ColumnCount = 2;
        _form.BackColor = Theme.Bg;
        _form.Margin = Padding.Empty;
        _form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mode.Items.AddRange(new object[] { "Remote (over SSH)", "Host (this PC)" });
        _mode.SelectedIndexChanged += (_, _) => UpdateRows();
        _name.TextChanged += (_, _) =>
        {
            if (_loading || _index < 0) return;
            _loading = true;
            _list.Items[_index] = string.IsNullOrWhiteSpace(_name.Text) ? "(unnamed)" : _name.Text;
            _loading = false;
        };

        AddRow(_form, "Profile name", _name, null);
        AddRow(_form, "Mode", _mode, null);
        AddRow(_form, "SSH alias", _alias, true);
        AddRow(_form, "Server folder on the server PC", _root, true);
        AddRow(_form, "Server API port", _remotePort, true);
        AddRow(_form, "Local tunnel port", _localPort, true);
        AddRow(_form, "vmp-server-control.ps1", _script, true);
        AddRow(_form, "Server folder", _folder, false);
        AddRow(_form, "API port", _apiPort, false);
        foreach (var box in new[] { _name, _alias, _root, _script, _folder }) box.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        _notify.Margin = new Padding(0, Spacing.M, 0, 0);
        Ui.Style(_notify, FontRole.Base);
        var fetchLabel = Ui.Label("Reports script (fetch-client-logs.ps1)", true);
        fetchLabel.Margin = new Padding(0, Spacing.S, 0, Spacing.Xs);
        _fetchScript.Dock = DockStyle.Fill;
        var activeLabel = Ui.Label("Profile to use", true);
        activeLabel.Margin = new Padding(0, 0, Spacing.S, 0);
        var activeLine = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, Spacing.S, 0, 0) };
        activeLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        activeLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        activeLine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activeLabel.Anchor = AnchorStyles.Left;
        _active.Anchor = AnchorStyles.Left;
        activeLine.Controls.Add(activeLabel, 0, 0);
        activeLine.Controls.Add(_active, 1, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in new Control[] { _form, _notify, fetchLabel, _fetchScript, activeLine })
        {
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.Controls.Add(control, 0, right.RowCount++);
        }

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(left, 0, 0);
        main.Controls.Add(right, 1, 0);
        AddContent(main);

        Ui.Style(_problem, FontRole.Base);
        AddContent(_problem, Spacing.M);

        var ok = Ui.Button("Save", ButtonKind.Primary, 100);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => Save();
        AddButtons(ok, cancel);
        AcceptButton = null;
        CancelButton = cancel;

        _notify.Checked = settings.Notifications;
        _notify.CheckedChanged += (_, _) => _settings.Notifications = _notify.Checked;
        _fetchScript.Text = settings.FetchLogsScriptPath;
        RefreshLists(Math.Max(0, _settings.Profiles.FindIndex(p => string.Equals(p.Name, runningProfile, StringComparison.OrdinalIgnoreCase))));
        _active.SelectedIndex = Math.Max(0, _settings.Profiles.FindIndex(p => string.Equals(p.Name, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)));
        ResumeLayout(false);
    }

    /// <summary>The dialog keeps the height it needs for the Remote form (the taller one), so switching a profile's mode does not make the window jump.</summary>
    protected override void Fit()
    {
        MinimumSize = Size.Empty;
        foreach (var (label, input, _) in _rows) label.Visible = input.Visible = true;
        base.Fit();
        MinimumSize = new Size(0, GetPreferredSize(Size.Empty).Height);
        UpdateRows();
    }

    private void AddRow(TableLayoutPanel table, string label, Control input, bool? remoteOnly)
    {
        var l = Ui.Label(label, true);
        l.Margin = new Padding(0, Spacing.Xs, Spacing.M, Spacing.Xs);
        l.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(0, Spacing.Xs, 0, Spacing.Xs);
        input.Anchor = AnchorStyles.Left;
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(l, 0, row);
        table.Controls.Add(input, 1, row);
        _rows.Add((l, input, remoteOnly));
    }

    private void ShowProblem(string text)
    {
        _problem.Text = text;
        _problem.Visible = true;
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; _settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = baseName + " " + i;
        return name;
    }

    private void AddProfile(Profile profile)
    {
        CommitCurrent();
        _settings.Profiles.Add(profile);
        _index = -1;
        RefreshLists(_settings.Profiles.Count - 1);
    }

    private void RefreshLists(int select)
    {
        _loading = true;
        var activeIndex = _active.SelectedIndex;
        _list.Items.Clear();
        _active.Items.Clear();
        foreach (var p in _settings.Profiles)
        {
            _list.Items.Add(p.Name);
            _active.Items.Add(p.Name);
        }

        _active.SelectedIndex = Math.Clamp(activeIndex, 0, Math.Max(0, _active.Items.Count - 1));
        _loading = false;
        _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
    }

    private void SelectProfile(int index)
    {
        if (_loading || index < 0) return;
        CommitCurrent();
        _index = index;
        var p = _settings.Profiles[index];
        _loading = true;
        _name.Text = p.Name;
        _mode.SelectedIndex = p.IsRemote ? 0 : 1;
        _alias.Text = p.SshAlias;
        _root.Text = p.RemoteServerRoot;
        _remotePort.Value = Math.Clamp(p.RemoteApiPort, 1, 65535);
        _localPort.Value = Math.Clamp(p.LocalTunnelPort, 1, 65535);
        _script.Text = p.ControlScriptPath;
        _folder.Text = p.ServerFolder;
        _apiPort.Value = Math.Clamp(p.ApiPort, 1, 65535);
        _loading = false;
        UpdateRows();
    }

    private void UpdateRows()
    {
        var remote = _mode.SelectedIndex == 0;
        foreach (var (label, input, remoteOnly) in _rows)
        {
            var visible = remoteOnly == null || remoteOnly == remote;
            label.Visible = input.Visible = visible;
        }
    }

    private void CommitCurrent()
    {
        if (_index < 0 || _index >= _settings.Profiles.Count) return;
        var p = _settings.Profiles[_index];
        var oldName = p.Name;
        p.Name = _name.Text.Trim();
        p.Mode = _mode.SelectedIndex == 0 ? ProfileMode.Remote : ProfileMode.Host;
        p.SshAlias = _alias.Text.Trim();
        p.RemoteServerRoot = _root.Text.Trim();
        p.RemoteApiPort = (int)_remotePort.Value;
        p.LocalTunnelPort = (int)_localPort.Value;
        p.ControlScriptPath = _script.Text.Trim();
        p.ServerFolder = _folder.Text.Trim();
        p.ApiPort = (int)_apiPort.Value;
        if (!string.Equals(oldName, p.Name, StringComparison.Ordinal) && string.Equals(_settings.ActiveProfile, oldName, StringComparison.Ordinal)) _settings.ActiveProfile = p.Name;
    }

    private void Save()
    {
        CommitCurrent();
        foreach (var p in _settings.Profiles)
        {
            var problem = p.Validate();
            if (problem != null)
            {
                ShowProblem($"{p.Name}: {problem}");
                return;
            }
        }

        var dupe = _settings.Profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupe != null)
        {
            ShowProblem("Two profiles are called \"" + dupe.Key + "\".");
            return;
        }

        _settings.FetchLogsScriptPath = _fetchScript.Text.Trim();
        _settings.Notifications = _notify.Checked;
        _settings.ActiveProfile = _settings.Profiles[Math.Clamp(_active.SelectedIndex, 0, _settings.Profiles.Count - 1)].Name;
        DialogResult = DialogResult.OK;
    }
}
