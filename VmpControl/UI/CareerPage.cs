using VmpControl.Core;

namespace VmpControl.UI;

/// <summary>
/// One currency of the Career tab: the current value, an amount box with live validation, presets that only fill the box, a note, "Tell players in chat",
/// and the buttons Add, Remove and Set to. The card never talks to the server; the page does, through the events.
/// </summary>
internal sealed class CareerCard : TableLayoutPanel
{
    private readonly CareerCurrency _currency;
    private readonly WrapLabel _value, _explain, _hint, _result;
    private readonly TextBox _amount = Ui.TextBox(), _note = Ui.TextBox();
    private readonly CheckBox _announce = new() { Text = "Tell players in chat", AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Panel, Margin = Padding.Empty };
    private readonly Button _add = Ui.Button("Add", ButtonKind.Primary, 90), _remove = Ui.Button("Remove", ButtonKind.Normal, 90), _set = Ui.Button("Set to", ButtonKind.Normal, 90);
    private readonly List<Button> _presets = new();
    private bool _enabled;
    private bool _busy;
    private double? _current;
    private CareerInput _input = CareerLogic.Evaluate(CareerCurrency.Funds, "", null);

    public event Action<CareerCard, CareerMode> ChangeRequested;

    public CareerCurrency Currency => _currency;
    public double? Current => _current;
    public string AmountText => _amount.Text;
    public string NoteText => _note.Text;
    public bool Announce => _announce.Checked;
    public bool IsEnabled => _enabled;
    public CareerInput Input => _input;

    public CareerCard(CareerCurrency currency, params (string Label, double Amount)[] presets)
    {
        _currency = currency;
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        BackColor = Theme.Panel;
        Margin = Padding.Empty;
        Padding = new Padding(Spacing.M, Spacing.S + Spacing.Xs, Spacing.M, Spacing.M);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var caption = Ui.Wrap(CareerLimits.Name(currency).ToUpperInvariant(), true, FontRole.Small);
        caption.Margin = new Padding(0, 0, 0, Spacing.Xs);
        _value = Ui.Wrap("-", false, FontRole.Big);
        _explain = Ui.Wrap("", false);
        _explain.ForeColor = Theme.Amber;
        _explain.Margin = new Padding(0, Spacing.S, 0, 0);
        _explain.Visible = false;

        var amountLabel = Ui.Wrap("Amount", true, FontRole.Small);
        var noteLabel = Ui.Wrap("Note for the server log (optional, players never see it)", true, FontRole.Small);
        _amount.PlaceholderText = "for example " + (currency == CareerCurrency.Funds ? "250,000 or 1.5m" : "500 or 2k");
        _amount.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _amount.TextChanged += (_, _) => Reevaluate();
        _hint = Ui.Wrap("", true, FontRole.Small);
        _hint.Margin = new Padding(0, Spacing.Xs, 0, 0);

        var presetRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0, Spacing.S, 0, 0) };
        foreach (var (label, amount) in presets)
        {
            var preset = Ui.Button(label, ButtonKind.Normal);
            preset.Margin = new Padding(0, 0, Spacing.S, Spacing.Xs);
            preset.Click += (_, _) =>
            {
                _amount.Text = CareerFormat.Number(amount);
                _amount.SelectionStart = _amount.TextLength;
                _amount.Focus();
            };
            _presets.Add(preset);
            presetRow.Controls.Add(preset);
        }

        _note.MaxLength = CareerLimits.MaxNoteLength;
        _note.PlaceholderText = "up to " + CareerLimits.MaxNoteLength + " characters";
        _note.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Ui.Style(_announce, FontRole.Base);

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0, Spacing.M, 0, 0) };
        foreach (var (button, mode) in new[] { (_add, CareerMode.Add), (_remove, CareerMode.Remove), (_set, CareerMode.Set) })
        {
            button.Margin = new Padding(0, 0, Spacing.S, Spacing.Xs);
            button.Click += (_, _) => ChangeRequested?.Invoke(this, mode);
            buttons.Controls.Add(button);
        }

        _result = Ui.Wrap("", false);
        _result.Margin = new Padding(0, Spacing.S, 0, 0);
        _result.Visible = false;

        void AddTo(TableLayoutPanel table, Control c, int gapAbove = 0)
        {
            c.Margin = new Padding(c.Margin.Left, gapAbove, c.Margin.Right, c.Margin.Bottom);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = table.RowStyles.Count;
            table.Controls.Add(c, 0, table.RowCount - 1);
        }

        AddTo(this, caption);
        AddTo(this, _value);
        AddTo(this, _explain, Spacing.S);
        AddTo(this, amountLabel, Spacing.M);
        AddTo(this, _amount, Spacing.Xs);
        AddTo(this, _hint, Spacing.Xs);
        AddTo(this, presetRow, Spacing.S);
        AddTo(this, noteLabel, Spacing.S);
        AddTo(this, _note, Spacing.Xs);
        AddTo(this, _announce, Spacing.S);
        AddTo(this, buttons, Spacing.M);
        AddTo(this, _result, Spacing.S);
        foreach (var c in new Control[] { caption, _value, _explain, _result, _hint, amountLabel, noteLabel }) c.Dock = DockStyle.Fill;
        Reevaluate();
    }

    /// <summary>The number under the caption and whether the controls can be used. <paramref name="explanation"/> says why not (null when they can).</summary>
    public void SetState(double? value, bool enabled, string explanation)
    {
        _current = enabled ? value : null;
        _enabled = enabled;
        var text = value == null || !enabled ? "-" : CareerFormat.Number(value.Value);
        if (_value.Text != text) _value.Text = text;
        _value.ForeColor = enabled ? Theme.Text : Theme.Muted;
        Reveal(_explain, explanation);
        _amount.ReadOnly = !enabled;
        _note.ReadOnly = !enabled;
        var tint = enabled ? Theme.Text : Theme.Muted;
        _amount.ForeColor = _note.ForeColor = tint;
        _announce.Enabled = enabled;
        foreach (var p in _presets) p.Enabled = enabled;
        Reevaluate();
    }

    /// <summary>Sets a label's text and hides it when there is none. Windows Forms scales a control that was hidden during the window's first layout when it first shows, on top of the role font, so the font is set again then.</summary>
    private static void Reveal(Label label, string text)
    {
        text ??= string.Empty;
        if (label.Text != text) label.Text = text;
        var show = text.Length > 0;
        if (label.Visible == show) return;
        label.Visible = show;
        if (show && label.IsHandleCreated) Ui.ApplyDpi(label);
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    /// <summary>The result line under the buttons (green after a success, amber after an error).</summary>
    public void ShowResult(string text, bool success)
    {
        _result.ForeColor = success ? Theme.Green : Theme.Amber;
        Reveal(_result, text);
    }

    public void ClearInputs()
    {
        _amount.Clear();
        _note.Clear();
    }

    /// <summary>--screenshot: fills the boxes the way a host would.</summary>
    internal void Demo(string amount, string note, bool announce)
    {
        _amount.Text = amount;
        _note.Text = note;
        _announce.Checked = announce;
    }

    private void Reevaluate()
    {
        _input = CareerLogic.Evaluate(_currency, _amount.Text, _current);
        var hint = _enabled || !_input.Parse.Empty ? _input.Hint : string.Empty;
        _hint.ForeColor = _input.HintIsError ? Theme.Red : Theme.Muted;
        Reveal(_hint, hint);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        foreach (var (button, mode) in new[] { (_add, CareerMode.Add), (_remove, CareerMode.Remove), (_set, CareerMode.Set) })
            button.Enabled = _enabled && !_busy && _input.For(mode)?.Ok == true;

        // A flat button keeps its fill when disabled, so the blue Add button is dimmed by hand.
        var dim = Color.FromArgb(46, 62, 94);
        _add.BackColor = _add.Enabled ? Theme.Accent : dim;
        _add.FlatAppearance.BorderColor = _add.BackColor;
    }
}

/// <summary>
/// The Career tab: the shared funds and science of the server, shown and changed from here. Each change is checked against the server's limits, confirmed in a dialog
/// that says what will happen, and answered with the previous and the current value. A server without the career endpoints gets one message instead.
/// </summary>
internal sealed class CareerPage : Page
{
    private readonly CareerCard _funds, _science;
    private readonly TableLayoutPanel _cards;
    private readonly WrapLabel _intro = Ui.Wrap("The funds and science that every player in the game shares. A change is sent to the server at once and players see it immediately.", true);
    private readonly Label _tooOld = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Bg, Text = CareerText.TooOldMessage + ".\n\nUpdate the server to use Career controls here.", Visible = false };
    private CareerLoad _load;
    private string _connection;

    public CareerPage(PageContext ctx) : base(ctx, 3000)
    {
        _funds = new CareerCard(CareerCurrency.Funds, ("+100k", 100_000), ("+1M", 1_000_000));
        _science = new CareerCard(CareerCurrency.Science, ("+100", 100), ("+1000", 1000));
        _funds.ChangeRequested += async (card, mode) => await ChangeAsync(card, mode);
        _science.ChangeRequested += async (card, mode) => await ChangeAsync(card, mode);

        _cards = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _funds.Margin = new Padding(0, 0, Spacing.S, 0);
        _science.Margin = new Padding(Spacing.S, 0, 0, 0);
        _cards.Controls.Add(_funds, 0, 0);
        _cards.Controls.Add(_science, 1, 0);

        Ui.Style(_tooOld, FontRole.Big);
        // The content grows when a result or an explanation appears; the page recomputes its scroll height then (see MinContentHeight).
        _cards.SizeChanged += (_, _) => PerformLayout();
        _intro.SizeChanged += (_, _) => PerformLayout();
        var stack = new VStack();
        stack.Auto(_intro);
        stack.Auto(_cards, Spacing.M);
        stack.Fill(_tooOld);
        Body.Controls.Add(stack);
        Ctx.Actions.BusyChanged += busy =>
        {
            if (IsDisposed) return;
            _funds.SetBusy(busy);
            _science.SetBusy(busy);
        };
        Apply();
    }

    public override string Title => "Career";
    protected override bool NeedsApi => false;

    /// <summary>The height of the page's content in 96-dpi units, so a window that is too low scrolls instead of clipping a card.</summary>
    protected override int MinContentHeight => _cards == null || !_cards.Visible ? 0 : (int)Math.Ceiling((_intro.Height + _cards.Height) / Ui.Factor(this)) + Spacing.M;

    public override void OnSnapshot(PollSnapshot s)
    {
        base.OnSnapshot(s);
        var connection = CareerText.ConnectionText(s);
        if (connection == _connection) return;
        _connection = connection;
        if (connection != null) _load = null;
        Apply();
    }

    public override async Task RefreshAsync()
    {
        if (Ctx.Latest?.Api != ApiAvailability.Ok) return;
        _load = CareerText.Load(await Ctx.Session.GetCareerAsync(Ctx.Cancel));
        Apply();
    }

    /// <summary>Puts the latest answer on the cards: values, and for a card that cannot be used, the reason.</summary>
    private void Apply()
    {
        var tooOld = _load?.Kind == CareerLoadKind.TooOld && _connection == null;
        if (_tooOld.Visible != tooOld)
        {
            _cards.Visible = !tooOld;
            _intro.Visible = !tooOld;
            _tooOld.Visible = tooOld;
            if (IsHandleCreated) Ui.ApplyDpi(Body);
        }

        if (tooOld) return;

        string reason = _connection ?? (_load == null ? "Loading..." : _load.Kind == CareerLoadKind.Problem ? _load.Message : null);
        foreach (var card in new[] { _funds, _science })
        {
            if (reason != null)
            {
                card.SetState(null, false, reason);
                continue;
            }

            var state = _load.State.Of(card.Currency);
            card.SetState(state.Value, state.Available && state.Value != null, state.Available && state.Value != null ? null : CareerText.UnavailableText(card.Currency, _load.State.GameMode));
        }
    }

    private async Task ChangeAsync(CareerCard card, CareerMode mode)
    {
        var preview = card.Input.For(mode);
        if (preview == null || !preview.Ok) return;
        card.ShowResult(null, true);
        var outcome = await Ctx.Actions.ChangeCareerAsync(card.Currency, mode, preview.Amount, card.Current, card.Announce, card.NoteText);
        Show(card, outcome);
        await SafeRefreshAsync();
    }

    private void Show(CareerCard card, CareerOutcome outcome)
    {
        if (outcome.TooOld)
        {
            _load = new CareerLoad { Kind = CareerLoadKind.TooOld, Message = CareerText.TooOldMessage };
            Apply();
            return;
        }

        card.ShowResult(outcome.Message, outcome.Success);
        if (!outcome.Success) return;
        card.ClearInputs();
        if (_load?.State != null && outcome.Current != null) _load.State.Of(card.Currency).Value = outcome.Current;
        Apply();
    }

    /// <summary>--mock --screenshot: fills both cards the way a host would. Funds get a valid Add, science an amount that Remove cannot take.</summary>
    internal void DemoFill()
    {
        _funds.Demo("250,000", "Contract bonus", announce: true);
        _science.Demo("2k", "", announce: false);
    }

    /// <summary>--mock --screenshot: the confirmed Add of the filled-in funds, so the result line (or the server's error) can be seen.</summary>
    internal async Task DemoApplyAsync()
    {
        await RefreshAsync();
        var add = _funds.Input.Add;
        if (_funds.IsEnabled && add?.Ok == true) Show(_funds, await Ctx.Actions.ApplyCareerAsync(add, _funds.Announce, _funds.NoteText));
        else _funds.ShowResult("Funds are not available here, so nothing was sent.", false);
    }

    /// <summary>The previews the screenshot run shows in dialogs, taken before the change is applied.</summary>
    internal (CareerPreview FundsAdd, CareerPreview ScienceSet) DemoPreviews() =>
        (CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Add, 250_000m, _funds.Current ?? 1_200_000), CareerLogic.Preview(CareerCurrency.Science, CareerMode.Set, 1500m, _science.Current ?? 842.5));
}
