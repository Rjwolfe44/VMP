using VmpControl.Core;

namespace VmpControl.UI;

/// <summary>Profiles (remote or host), notifications and the reports script. Edits a copy; the caller keeps it only when the dialog returns OK.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly ListBox _list = new();
    private readonly ComboBox _active = Ui.Combo(220);
    private readonly TextBox _name = Ui.TextBox(), _alias = Ui.TextBox(), _root = Ui.TextBox(), _script = Ui.TextBox(), _folder = Ui.TextBox(), _fetchScript = Ui.TextBox();
    private readonly ComboBox _mode = Ui.Combo(200);
    private readonly NumericUpDown _remotePort = Port(), _localPort = Port(), _apiPort = Port();
    private readonly CheckBox _notify = new() { Text = "Show notifications (players joined / left, server down / back)", AutoSize = true, ForeColor = Theme.Text };
    private readonly List<(Control Label, Control Input, bool? RemoteOnly)> _rows = new();
    private readonly Label _problem = new() { AutoSize = false, ForeColor = Theme.Red, Height = 38, Dock = DockStyle.Bottom };
    private bool _loading;
    private int _index = -1;

    public AppSettings Result => _settings;

    private static NumericUpDown Port() => new() { Minimum = 1, Maximum = 65535, Width = 90, BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 8, 0) };

    public SettingsForm(AppSettings settings, string runningProfile)
    {
        _settings = settings;
        SuspendLayout();
        Text = "VMP Control settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 560);
        Native.DarkTitleBar(this);

        _list.Location = new Point(16, 40);
        _list.Size = new Size(190, 250);
        _list.BackColor = Theme.Panel;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.Font = Theme.Base;
        _list.SelectedIndexChanged += (_, _) => SelectProfile(_list.SelectedIndex);
        Controls.Add(Ui.Label("Profiles", true));
        Controls[0].Location = new Point(16, 14);
        Controls.Add(_list);

        var addRemote = Ui.Button("Add remote");
        var addHost = Ui.Button("Add host");
        var remove = Ui.Button("Remove");
        addRemote.Location = new Point(16, 298);
        addHost.Location = new Point(110, 298);
        remove.Location = new Point(16, 334);
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
        Controls.AddRange(new Control[] { addRemote, addHost, remove });

        var form = new TableLayoutPanel { Location = new Point(230, 14), Size = new Size(534, 360), ColumnCount = 2, BackColor = Theme.Bg, AutoScroll = false };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mode.Items.AddRange(new object[] { "Remote (over SSH)", "Host (this PC)" });
        _mode.SelectedIndexChanged += (_, _) => UpdateRows();
        _name.TextChanged += (_, _) =>
        {
            if (_loading || _index < 0) return;
            _loading = true;
            _list.Items[_index] = string.IsNullOrWhiteSpace(_name.Text) ? "(unnamed)" : _name.Text;
            _loading = false;
        };

        AddRow(form, "Profile name", _name, null);
        AddRow(form, "Mode", _mode, null);
        AddRow(form, "SSH alias", _alias, true);
        AddRow(form, "Server folder on the server PC", _root, true);
        AddRow(form, "Server API port", _remotePort, true);
        AddRow(form, "Local tunnel port", _localPort, true);
        AddRow(form, "vmp-server-control.ps1", _script, true);
        AddRow(form, "Server folder", _folder, false);
        AddRow(form, "API port", _apiPort, false);
        foreach (var box in new[] { _name, _alias, _root, _script, _folder }) box.Width = 360;
        Controls.Add(form);

        _notify.Location = new Point(230, 390);
        Controls.Add(_notify);
        var fetchLabel = Ui.Label("Reports script (fetch-client-logs.ps1)", true);
        fetchLabel.Location = new Point(230, 422);
        _fetchScript.Location = new Point(230, 444);
        _fetchScript.Width = 520;
        var activeLabel = Ui.Label("Profile to use", true);
        activeLabel.Location = new Point(230, 478);
        _active.Location = new Point(330, 474);
        Controls.AddRange(new Control[] { fetchLabel, _fetchScript, activeLabel, _active });

        var ok = Ui.Button("Save", ButtonKind.Primary, 100);
        var cancel = Ui.Button("Cancel", ButtonKind.Normal, 90);
        ok.Location = new Point(ClientSize.Width - 16 - 90 - 8 - 100, ClientSize.Height - 46);
        cancel.Location = new Point(ClientSize.Width - 16 - 90, ClientSize.Height - 46);
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => Save();
        _problem.Padding = new Padding(16, 0, 0, 0);
        _problem.Width = 500;
        _problem.Location = new Point(0, ClientSize.Height - 46);
        _problem.Dock = DockStyle.None;
        Controls.AddRange(new Control[] { ok, cancel, _problem });
        AcceptButton = null;
        CancelButton = cancel;

        _notify.Checked = settings.Notifications;
        _notify.CheckedChanged += (_, _) => _settings.Notifications = _notify.Checked;
        _fetchScript.Text = settings.FetchLogsScriptPath;
        RefreshLists(Math.Max(0, _settings.Profiles.FindIndex(p => string.Equals(p.Name, runningProfile, StringComparison.OrdinalIgnoreCase))));
        _active.SelectedIndex = Math.Max(0, _settings.Profiles.FindIndex(p => string.Equals(p.Name, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)));
        ResumeLayout(false);
    }

    private void AddRow(TableLayoutPanel table, string label, Control input, bool? remoteOnly)
    {
        var l = Ui.Label(label, true);
        l.Margin = new Padding(0, 8, 8, 0);
        input.Margin = new Padding(0, 4, 0, 4);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(l, 0, row);
        table.Controls.Add(input, 1, row);
        _rows.Add((l, input, remoteOnly));
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
                _problem.Text = $"{p.Name}: {problem}";
                return;
            }
        }

        var dupe = _settings.Profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupe != null)
        {
            _problem.Text = "Two profiles are called \"" + dupe.Key + "\".";
            return;
        }

        _settings.FetchLogsScriptPath = _fetchScript.Text.Trim();
        _settings.Notifications = _notify.Checked;
        _settings.ActiveProfile = _settings.Profiles[Math.Clamp(_active.SelectedIndex, 0, _settings.Profiles.Count - 1)].Name;
        DialogResult = DialogResult.OK;
    }
}
