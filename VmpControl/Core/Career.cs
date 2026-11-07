using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VmpControl.Core;

public enum CareerCurrency
{
    Funds,
    Science,
}

public enum CareerMode
{
    Add,
    Remove,
    Set,
}

/// <summary>The bounds of the server's career endpoints (funds: amount and result within +-1e12; science: amount up to 1e9 and the result never below 0).</summary>
public static class CareerLimits
{
    public const double FundsMax = 1e12;
    public const double ScienceMax = 1e9;
    public const int MaxNoteLength = 80;

    public static double MaxAmount(CareerCurrency c) => c == CareerCurrency.Funds ? FundsMax : ScienceMax;

    public static string Key(CareerCurrency c) => c == CareerCurrency.Funds ? "funds" : "science";

    public static string Name(CareerCurrency c) => c == CareerCurrency.Funds ? "Funds" : "Science";
}

/// <summary>The number as the window shows it: invariant culture, thousands separators, at most two decimals.</summary>
public static class CareerFormat
{
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "-";
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // no "-0"
        return rounded.ToString("#,##0.##", CultureInfo.InvariantCulture);
    }

    public static string Number(double? value) => value == null ? "-" : Number(value.Value);

    public static string Signed(double value) => (value > 0 ? "+" : "") + Number(value);
}

public sealed class AmountParseResult
{
    public bool Ok { get; init; }
    public bool Empty { get; init; }
    public decimal Value { get; init; }
    public string Error { get; init; }
}

/// <summary>
/// Reads what the host types in the amount box: plain numbers, commas as thousands separators (grouped by three), a dot for decimals and a k, m or b suffix
/// (thousand, million, billion). Always the invariant culture, so "1,5m" is an error on every PC instead of 15 million on one of them.
/// </summary>
public static class AmountParser
{
    public const string Examples = "250000, 250,000, 1.5m or 2k";

    private const int MaxIntegerDigits = 15;
    private const int MaxDecimals = 6;

    private static readonly Regex Rx = new(@"\A(?<sign>[+-])?(?<int>[0-9]{1,3}(?:,[0-9]{3})+|[0-9]*)(?:\.(?<frac>[0-9]+))?[ ]?(?<sfx>[kKmMbB])?\z", RegexOptions.CultureInvariant);

    private static AmountParseResult Fail(string error) => new() { Error = error };

    /// <summary>
    /// What the amount box shows after a preset button is clicked: the preset ADDED to what is already in the box, so clicking +100k three times
    /// gives 300,000. An empty box, or text that is not a number, simply becomes the preset.
    /// </summary>
    public static string AddPreset(string currentText, double preset)
    {
        var parsed = Parse(currentText);
        var total = parsed.Ok ? parsed.Value + (decimal)preset : (decimal)preset;
        return CareerFormat.Number((double)total);
    }

    public static AmountParseResult Parse(string text)
    {
        text = (text ?? string.Empty).Trim().Replace('−', '-');
        if (text.Length == 0) return new AmountParseResult { Empty = true, Error = "Type an amount, for example " + Examples + "." };

        var m = Rx.Match(text);
        if (!m.Success)
        {
            return text.Contains(',')
                ? Fail("Commas must separate groups of three digits (1,500,000). Use a dot for decimals (1.5m).")
                : Fail("That is not a number. Use digits, a dot for decimals and a k, m or b suffix, for example " + Examples + ".");
        }

        var integer = m.Groups["int"].Value.Replace(",", "");
        var fraction = m.Groups["frac"].Value;
        if (integer.Length == 0 && fraction.Length == 0) return Fail("That is not a number. Use digits, a dot for decimals and a k, m or b suffix, for example " + Examples + ".");
        if (integer.TrimStart('0').Length > MaxIntegerDigits) return Fail("That number is too large.");
        if (fraction.Length > MaxDecimals) return Fail("Use at most " + MaxDecimals + " decimals.");

        var value = decimal.Parse((integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? "" : "." + fraction), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        value *= char.ToLowerInvariant(m.Groups["sfx"].Value.FirstOrDefault()) switch { 'k' => 1_000m, 'm' => 1_000_000m, 'b' => 1_000_000_000m, _ => 1m };
        if (m.Groups["sign"].Value == "-") value = -value;
        if (value == 0) value = 0m; // no negative zero
        return new AmountParseResult { Ok = true, Value = value };
    }
}

/// <summary>What one of the buttons would do with the amount in the box: the number that is sent, the value before and the value after, or why it cannot be done.</summary>
public sealed class CareerPreview
{
    public CareerCurrency Currency { get; init; }
    public CareerMode Mode { get; init; }
    public bool Ok { get; init; }
    public string Error { get; init; }
    /// <summary>The amount as typed (for Remove this is the positive amount to take away).</summary>
    public decimal Amount { get; init; }
    /// <summary>The value of the request: negative for Remove (which is an "add" with a negative amount).</summary>
    public double SignedAmount { get; init; }
    public double? Previous { get; init; }
    public double? Result { get; init; }
}

/// <summary>The amount box evaluated for one currency: the parse, what each button would do, and the line shown under the box.</summary>
public sealed class CareerInput
{
    public AmountParseResult Parse { get; init; }
    public CareerPreview Add { get; init; }
    public CareerPreview Remove { get; init; }
    public CareerPreview Set { get; init; }
    public string Hint { get; init; }
    /// <summary>True when no button can be used (the hint is then shown as an error).</summary>
    public bool HintIsError { get; init; }

    public CareerPreview For(CareerMode mode) => mode switch { CareerMode.Add => Add, CareerMode.Remove => Remove, _ => Set };
}

public static class CareerLogic
{
    /// <summary>The server's rules, mirrored so the window can say no before asking: the amount limit, and the limit on the result (funds within +-1e12, science never below 0).</summary>
    public static CareerPreview Preview(CareerCurrency currency, CareerMode mode, decimal amount, double? current)
    {
        var name = CareerLimits.Name(currency);
        var lower = name.ToLowerInvariant();
        var max = CareerLimits.MaxAmount(currency);
        CareerPreview Bad(string error) => new() { Currency = currency, Mode = mode, Amount = amount, Previous = current, Error = error };

        if (mode != CareerMode.Set && amount <= 0)
        {
            if (amount < 0) return Bad(mode == CareerMode.Add ? "use Remove to take " + lower + " away" : "enter the amount to remove as a positive number");
            return Bad("enter an amount above 0");
        }

        if (mode == CareerMode.Set && currency == CareerCurrency.Science && amount < 0) return Bad("science cannot be negative");
        if (Math.Abs(amount) > (decimal)max) return Bad(lower + " amounts are limited to " + CareerFormat.Number(max) + ".");

        var signed = mode == CareerMode.Remove ? -(double)amount : (double)amount;
        double? result = mode == CareerMode.Set ? signed : current == null ? null : current.Value + signed;
        if (result != null)
        {
            if (currency == CareerCurrency.Funds && Math.Abs(result.Value) > CareerLimits.FundsMax)
                return Bad("funds must stay within +-" + CareerFormat.Number(CareerLimits.FundsMax) + " (this would make it " + CareerFormat.Number(result.Value) + ")");
            if (currency == CareerCurrency.Science && result.Value < 0)
                return Bad("science cannot go below 0: there is " + CareerFormat.Number(current) + ", so at most " + CareerFormat.Number(current) + " can be removed");
        }

        return new CareerPreview { Currency = currency, Mode = mode, Ok = true, Amount = amount, SignedAmount = signed, Previous = current, Result = result };
    }

    public static CareerInput Evaluate(CareerCurrency currency, string text, double? current)
    {
        var parse = AmountParser.Parse(text);
        if (!parse.Ok) return new CareerInput { Parse = parse, Hint = parse.Error, HintIsError = !parse.Empty };

        var add = Preview(currency, CareerMode.Add, parse.Value, current);
        var remove = Preview(currency, CareerMode.Remove, parse.Value, current);
        var set = Preview(currency, CareerMode.Set, parse.Value, current);
        var hint = new StringBuilder("= ").Append(CareerFormat.Number((double)parse.Value));
        var bad = new[] { ("Add", add), ("Remove", remove), ("Set to", set) }.Where(x => !x.Item2.Ok).ToList();
        string Sentence(string e) => e.EndsWith('.') ? e : e + ".";
        if (bad.Count == 3 && bad.Select(x => x.Item2.Error).Distinct().Count() == 1) hint.Append("   ").Append(Sentence(bad[0].Item2.Error));
        else foreach (var (label, preview) in bad) hint.Append("   ").Append(label).Append(": ").Append(Sentence(preview.Error));

        return new CareerInput { Parse = parse, Add = add, Remove = remove, Set = set, Hint = hint.ToString(), HintIsError = !add.Ok && !remove.Ok && !set.Ok };
    }
}

/// <summary>Body of <c>POST career/funds</c> and <c>POST career/science</c>. Remove is an "add" with a negative amount. The note is left out of the JSON when it is empty.</summary>
public sealed class CareerChangeBody
{
    public string Mode { get; init; }
    public double Amount { get; init; }
    public bool Announce { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Note { get; init; }

    public static CareerChangeBody Create(CareerPreview preview, bool announce, string note)
    {
        if (preview == null || !preview.Ok || double.IsNaN(preview.SignedAmount) || double.IsInfinity(preview.SignedAmount))
            throw new ArgumentException("the preview is not a valid change", nameof(preview));
        return new CareerChangeBody { Mode = preview.Mode == CareerMode.Set ? "set" : "add", Amount = preview.SignedAmount, Announce = announce, Note = CleanNote(note) };
    }

    /// <summary>Control characters and line breaks become one space, the text is trimmed, null when empty, cut to 80 characters (never inside a surrogate pair).</summary>
    public static string CleanNote(string note)
    {
        var text = Regex.Replace(note ?? string.Empty, @"[\p{C}\s]+", " ", RegexOptions.CultureInvariant).Trim();
        if (text.Length == 0) return null;
        text = TextCut.Limit(text, CareerLimits.MaxNoteLength);
        return text.Length == 0 ? null : text;
    }
}

public static class TextCut
{
    /// <summary>The text cut to <paramref name="max"/> characters, never in the middle of a surrogate pair, without trailing spaces.</summary>
    public static string Limit(string text, int max)
    {
        if (text.Length <= max) return text;
        var length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text.Substring(0, length).TrimEnd();
    }
}

public sealed class CareerCurrencyDto
{
    public bool Available { get; set; }
    public double? Value { get; set; }
}

public sealed class CareerStateDto
{
    private CareerCurrencyDto _funds = new(), _science = new();
    public string GameMode { get; set; }
    public CareerCurrencyDto Funds { get => _funds; set => _funds = value ?? new(); }
    public CareerCurrencyDto Science { get => _science; set => _science = value ?? new(); }

    public CareerCurrencyDto Of(CareerCurrency c) => c == CareerCurrency.Funds ? Funds : Science;
}

public sealed class CareerChangeResultDto
{
    public bool Ok { get; set; }
    public string Currency { get; set; }
    public double Previous { get; set; }
    public double Current { get; set; }
}

public enum CareerLoadKind
{
    Ready,
    /// <summary>The server answered 404: it has no career endpoints.</summary>
    TooOld,
    /// <summary>The server or the connection is not usable right now; <see cref="CareerLoad.Message"/> says why.</summary>
    Problem,
}

public sealed class CareerLoad
{
    public CareerLoadKind Kind { get; init; }
    public CareerStateDto State { get; init; }
    public string Message { get; init; }
}

public sealed class CareerOutcome
{
    public bool Success { get; init; }
    /// <summary>The user said no in the confirmation dialog; nothing was sent.</summary>
    public bool Cancelled { get; init; }
    public bool TooOld { get; init; }
    public string Message { get; init; }
    public double? Previous { get; init; }
    public double? Current { get; init; }
}

/// <summary>Turns the server's answers into the words of the Career tab, and the confirmation text of a change.</summary>
public static class CareerText
{
    public const string TooOldMessage = "This server is too old for Career controls";

    private static bool IsMissing<T>(ApiResult<T> r) where T : class => r.Outcome == ApiOutcome.NotSupported || (!r.IsOk && r.StatusCode == 404);

    public static CareerLoad Load(ApiResult<CareerStateDto> result)
    {
        if (result.IsOk) return new CareerLoad { Kind = CareerLoadKind.Ready, State = result.Value };
        if (IsMissing(result)) return new CareerLoad { Kind = CareerLoadKind.TooOld, Message = TooOldMessage };
        return new CareerLoad { Kind = CareerLoadKind.Problem, Message = Failure(result) };
    }

    private static string Failure<T>(ApiResult<T> r) where T : class => r.Outcome switch
    {
        ApiOutcome.Unauthorized => "The server refused the admin token. It will be read again in a moment.",
        ApiOutcome.Unreachable => "The admin API is not reachable" + (string.IsNullOrWhiteSpace(r.Error) ? "." : ": " + r.Error),
        _ => string.IsNullOrWhiteSpace(r.Error) ? "The server refused the request (status " + r.StatusCode + ")." : r.Error,
    };

    /// <summary>Why a card is disabled although the server answered: the currency does not exist in its game mode.</summary>
    public static string UnavailableText(CareerCurrency currency, string gameMode)
    {
        var mode = string.IsNullOrWhiteSpace(gameMode) ? null : gameMode.Trim();
        var exists = currency == CareerCurrency.Funds ? "Funds only exist in Career mode." : "Science only exists in Career and Science modes.";
        var inCareer = string.Equals(mode, "Career", StringComparison.OrdinalIgnoreCase);
        var inScience = string.Equals(mode, "Science", StringComparison.OrdinalIgnoreCase);
        if (mode != null && !(inCareer || (inScience && currency == CareerCurrency.Science))) return exists + " This server runs " + mode + " mode.";
        return (currency == CareerCurrency.Funds ? "Funds are" : "Science is") + " not available on this server right now. It may still be starting.";
    }

    /// <summary>Why a card is disabled while the connection is down (or not yet up).</summary>
    public static string ConnectionText(PollSnapshot s)
    {
        if (s == null || s.Api == ApiAvailability.Unknown) return "Connecting to the server...";
        return s.Api switch
        {
            ApiAvailability.NotSupported => "This server does not have the admin API yet, so Career controls are not available.",
            ApiAvailability.Unauthorized => "The server refused the admin token. It will be read again in a moment.",
            ApiAvailability.Unreachable => "The admin API is not reachable" + (string.IsNullOrEmpty(s.ApiDetail) ? "." : ": " + s.ApiDetail),
            _ => null,
        };
    }

    public static CareerOutcome Outcome(CareerPreview preview, ApiResult<CareerChangeResultDto> result)
    {
        var name = CareerLimits.Name(preview.Currency);
        if (result.IsOk)
        {
            var v = result.Value;
            if (!v.Ok) return new CareerOutcome { Message = "Not changed: unexpected answer from the server." };
            var text = preview.Mode switch
            {
                CareerMode.Add => $"Added {CareerFormat.Number((double)preview.Amount)} {name.ToLowerInvariant()}.",
                CareerMode.Remove => $"Removed {CareerFormat.Number((double)preview.Amount)} {name.ToLowerInvariant()}.",
                _ => $"{name} set to {CareerFormat.Number(v.Current)}.",
            };
            return new CareerOutcome { Success = true, Message = $"{text} {name} went from {CareerFormat.Number(v.Previous)} to {CareerFormat.Number(v.Current)}.", Previous = v.Previous, Current = v.Current };
        }

        if (IsMissing(result)) return new CareerOutcome { TooOld = true, Message = TooOldMessage + "." };
        if (result.Outcome == ApiOutcome.Unreachable && string.Equals(result.Error, "timed out", StringComparison.OrdinalIgnoreCase))
            return new CareerOutcome { Message = "No answer from the server (timed out). The change may or may not have happened; the value above shows what the server has." };
        return new CareerOutcome { Message = "Not changed: " + Failure(result) };
    }

    public static string DialogTitle(CareerPreview p) => (p.Mode switch { CareerMode.Add => "Add ", CareerMode.Remove => "Remove ", _ => "Set " }) + CareerLimits.Key(p.Currency);

    /// <summary>What the confirmation dialog says: the action, the numbers before and after, who will see it, and a stronger warning for Set.</summary>
    public static string ConfirmText(CareerPreview p, int? players, bool announce, string note)
    {
        var name = CareerLimits.Name(p.Currency);
        var lower = name.ToLowerInvariant();
        var amount = CareerFormat.Number((double)p.Amount);
        var sb = new StringBuilder();
        switch (p.Mode)
        {
            case CareerMode.Add:
            case CareerMode.Remove:
                sb.Append(p.Mode == CareerMode.Add ? "Add " : "Remove ").Append(amount).Append(' ').Append(lower).Append(".\n");
                if (p.Previous != null && p.Result != null) sb.Append(name).Append(" will go from ").Append(CareerFormat.Number(p.Previous)).Append(" to ").Append(CareerFormat.Number(p.Result)).Append('.');
                else sb.Append(name).Append(" will change by ").Append(CareerFormat.Signed(p.SignedAmount)).Append('.');
                break;
            default:
                sb.Append("Set ").Append(lower).Append(" to ").Append(amount).Append("?\n\n");
                if (p.Previous != null)
                {
                    sb.Append("This overwrites the shared ").Append(lower).Append(" for everyone: it will go from ").Append(CareerFormat.Number(p.Previous)).Append(" to ").Append(amount)
                        .Append(" (a change of ").Append(CareerFormat.Signed(p.Result.Value - p.Previous.Value)).Append("). ");
                }
                else
                {
                    sb.Append("This overwrites the shared ").Append(lower).Append(" for everyone (the current value could not be read). ");
                }

                sb.Append("The old value is not kept; to go back you would have to set it again.");
                break;
        }

        sb.Append("\n\n").Append(players switch
        {
            null => "Players in the game will see it immediately.",
            0 => "Nobody is online right now.",
            1 => "1 player is online and will see it immediately.",
            _ => players + " players are online and will see it immediately.",
        });
        if (announce) sb.Append(" A message about it goes to the chat.");
        var clean = CareerChangeBody.CleanNote(note);
        if (clean != null) sb.Append("\nNote: ").Append(clean);
        return sb.ToString();
    }

    public static string ActivityLine(CareerPreview preview, CareerOutcome outcome) => outcome.Success
        ? $"{preview.Mode.ToString().ToLowerInvariant()} {CareerFormat.Number((double)preview.Amount)}: {CareerFormat.Number(outcome.Previous)} -> {CareerFormat.Number(outcome.Current)}"
        : "failed: " + outcome.Message;
}

public enum MockCareerMode
{
    /// <summary>Game mode Career: funds and science work.</summary>
    Career,
    /// <summary>Game mode Science: funds are not available.</summary>
    Science,
    /// <summary>Game mode Sandbox: neither is available.</summary>
    Sandbox,
    /// <summary>A server without the career endpoints (404).</summary>
    Old,
    /// <summary>Career mode, but every change is refused with a 409.</summary>
    Error,
    /// <summary>The admin API does not answer.</summary>
    Offline,
}

/// <summary>The sample server's funds and science for --mock: believable numbers, the same bounds as the real server, and the states selected with --mock-career.</summary>
public sealed class MockCareer
{
    private double _funds = 1_200_000;
    private double _science = 842.5;

    public MockCareerMode Mode { get; }

    public MockCareer(MockCareerMode mode) => Mode = mode;

    public static bool TryParseMode(string text, out MockCareerMode mode) => Enum.TryParse(text, true, out mode) && Enum.IsDefined(mode);

    private string GameMode => Mode switch { MockCareerMode.Science => "Science", MockCareerMode.Sandbox => "Sandbox", _ => "Career" };

    private bool Available(CareerCurrency c) => Mode switch
    {
        MockCareerMode.Science => c == CareerCurrency.Science,
        MockCareerMode.Sandbox => false,
        _ => true,
    };

    public ApiResult<CareerStateDto> Get()
    {
        if (Mode == MockCareerMode.Old) return ApiResult<CareerStateDto>.Fail(ApiOutcome.NotSupported, "the server does not have this admin API call", 404);
        if (Mode == MockCareerMode.Offline) return ApiResult<CareerStateDto>.Fail(ApiOutcome.Unreachable, "connection refused (mock)");
        var state = new CareerStateDto
        {
            GameMode = GameMode,
            Funds = new CareerCurrencyDto { Available = Available(CareerCurrency.Funds), Value = Available(CareerCurrency.Funds) ? _funds : null },
            Science = new CareerCurrencyDto { Available = Available(CareerCurrency.Science), Value = Available(CareerCurrency.Science) ? _science : null },
        };
        return new ApiResult<CareerStateDto> { Outcome = ApiOutcome.Ok, Value = state, StatusCode = 200 };
    }

    /// <summary>What the server does with a POST: the same checks as the real one (400 for a bad amount or a result out of bounds, 409 when the currency is unavailable).</summary>
    public ApiResult<CareerChangeResultDto> Apply(CareerCurrency currency, CareerChangeBody body)
    {
        if (Mode == MockCareerMode.Old) return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.NotSupported, "the server does not have this admin API call", 404);
        if (Mode == MockCareerMode.Offline) return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Unreachable, "connection refused (mock)");
        if (Mode == MockCareerMode.Error) return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, "The server is not ready yet: the scenario data is still loading. Try again in a minute.", 409);
        if (!Available(currency))
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, $"{CareerLimits.Name(currency)} are not available in {GameMode} mode.", 409);

        var name = CareerLimits.Key(currency);
        if (body == null || (body.Mode != "add" && body.Mode != "set") || double.IsNaN(body.Amount) || double.IsInfinity(body.Amount))
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, "mode must be add or set and amount must be a number", 400);
        if (Math.Abs(body.Amount) > CareerLimits.MaxAmount(currency))
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, $"amount must be within +-{CareerFormat.Number(CareerLimits.MaxAmount(currency))} for {name}", 400);
        if (body.Note != null && body.Note.Length > CareerLimits.MaxNoteLength)
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, "note must be at most 80 characters", 400);

        var previous = currency == CareerCurrency.Funds ? _funds : _science;
        var result = body.Mode == "set" ? body.Amount : previous + body.Amount;
        if (currency == CareerCurrency.Funds && Math.Abs(result) > CareerLimits.FundsMax)
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, $"the result would be outside +-{CareerFormat.Number(CareerLimits.FundsMax)}", 400);
        if (currency == CareerCurrency.Science && result < 0)
            return ApiResult<CareerChangeResultDto>.Fail(ApiOutcome.Failed, "science cannot go below 0", 400);

        if (currency == CareerCurrency.Funds) _funds = result;
        else _science = result;
        return new ApiResult<CareerChangeResultDto> { Outcome = ApiOutcome.Ok, StatusCode = 200, Value = new CareerChangeResultDto { Ok = true, Currency = name, Previous = previous, Current = result } };
    }
}
