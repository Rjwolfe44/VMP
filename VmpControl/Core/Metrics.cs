using System.Net.Sockets;

namespace VmpControl.Core;

/// <summary>The one spacing scale of the window, in 96-dpi units (a control is laid out in these units and Windows Forms scales them to the monitor).</summary>
public static class Spacing
{
    public const int Xs = 4;
    public const int S = 8;
    public const int M = 12;
    public const int L = 16;
}

/// <summary>Pure DPI arithmetic. Everything in the window is authored in 96-dpi units; custom painting and a few late-built controls scale by these helpers.</summary>
public static class DpiMath
{
    public const int BaseDpi = 96;

    public static float Factor(int dpi) => dpi <= 0 ? 1f : dpi / (float)BaseDpi;

    public static int Scale(int units, int dpi) => (int)Math.Round(units * Factor(dpi), MidpointRounding.AwayFromZero);

    public static float Scale(float units, int dpi) => units * Factor(dpi);

    /// <summary>
    /// The point size that makes a font look like <paramref name="nominalPoints"/> on a window of <paramref name="windowDpi"/>. A Windows Forms font in points is turned into pixels at
    /// the system DPI, so a window on another monitor needs its points rescaled by windowDpi / systemDpi.
    /// </summary>
    public static float FontPoints(float nominalPoints, int windowDpi, int systemDpi) => systemDpi <= 0 || windowDpi <= 0 ? nominalPoints : nominalPoints * windowDpi / systemDpi;

    /// <summary>Height of a grid row: the text height plus the same breathing room above and below, in units.</summary>
    public static int RowHeight(int fontHeight, int dpi) => fontHeight + 2 * Scale(Spacing.Xs, dpi);

    /// <summary>Height of a grid header: a row plus a little more.</summary>
    public static int HeaderHeight(int fontHeight, int dpi) => RowHeight(fontHeight, dpi) + Scale(Spacing.Xs, dpi);
}

/// <summary>Wrap-width arithmetic for labels that must wrap to what their parent offers.</summary>
public static class TextWrap
{
    /// <summary>The width a wrapping label may use: what the parent offers minus the label's own horizontal margins, never below <paramref name="minimum"/>.</summary>
    public static int WrapWidth(int available, int horizontalMargins, int minimum = 40) => Math.Max(minimum, available - Math.Max(0, horizontalMargins));

    /// <summary>True for a width that means "no limit" (zero, negative, or a huge sentinel such as int.MaxValue) when a layout asks for a preferred size.</summary>
    public static bool IsUnbounded(int proposedWidth) => proposedWidth <= 0 || proposedWidth >= 100_000;
}

/// <summary>Size limits of the app's own dialogs.</summary>
public static class DialogSizing
{
    public const double MaxHeightShare = 0.7;

    /// <summary>A dialog is at most 70% of the working area high.</summary>
    public static int MaxHeight(int workingAreaHeight) => Math.Max(0, workingAreaHeight) * 7 / 10;

    /// <summary>
    /// The height of a scrolling list inside a dialog: the whole content when it fits, else what is left of the dialog's maximum height after the fixed parts
    /// (but not less than <paramref name="minimum"/>, and never more than the content).
    /// </summary>
    public static int ListHeight(int contentHeight, int fixedHeight, int maxDialogHeight, int minimum)
    {
        var content = Math.Max(0, contentHeight);
        var room = Math.Max(minimum, maxDialogHeight - Math.Max(0, fixedHeight));
        return Math.Min(content, room);
    }

    /// <summary>Position that centres a window of <paramref name="size"/> on a parent rectangle, kept inside the working area.</summary>
    public static (int X, int Y) CenterOn(int parentX, int parentY, int parentWidth, int parentHeight, int width, int height, int areaX, int areaY, int areaWidth, int areaHeight)
    {
        var x = parentX + (parentWidth - width) / 2;
        var y = parentY + (parentHeight - height) / 2;
        x = Math.Max(areaX, Math.Min(x, areaX + areaWidth - width));
        y = Math.Max(areaY, Math.Min(y, areaY + areaHeight - height));
        return (x, y);
    }
}

public enum ErrorKind
{
    /// <summary>A refresh or poll failed (network, disk, timeout, cancelled, disposed): log it, never interrupt the user.</summary>
    Background,

    /// <summary>A bug in the app: tell the user once per distinct message and keep running.</summary>
    Unexpected,

    /// <summary>The process may be unusable: always tell the user.</summary>
    Fatal,
}

public static class ErrorPolicy
{
    public static ErrorKind Classify(Exception e)
    {
        switch (e)
        {
            case null:
                return ErrorKind.Background;
            case OutOfMemoryException or AccessViolationException or StackOverflowException or InsufficientExecutionStackException or BadImageFormatException:
                return ErrorKind.Fatal;
            case AggregateException a when a.InnerExceptions.Count > 0:
                return a.InnerExceptions.Select(Classify).Max();
            case OperationCanceledException or TimeoutException or IOException or HttpRequestException or SocketException or ObjectDisposedException or System.Text.Json.JsonException:
                return ErrorKind.Background;
            default:
                return e.InnerException != null && Classify(e.InnerException) == ErrorKind.Background ? ErrorKind.Background : ErrorKind.Unexpected;
        }
    }

    public static string Describe(Exception e) => e == null ? "" : e.GetType().Name + ": " + e.Message;
}

/// <summary>Says "first time" once for every distinct message, so a failure that repeats on every poll is reported once. A bounded memory.</summary>
public sealed class DistinctMessageGate
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public DistinctMessageGate(int capacity = 200) => _capacity = Math.Max(1, capacity);

    public bool FirstTime(string message)
    {
        message ??= "";
        if (_seen.Contains(message)) return false;
        if (_seen.Count >= _capacity) _seen.Clear();
        _seen.Add(message);
        return true;
    }

    /// <summary>Forgets one message, so the next failure with the same text is reported again (called when the thing recovered).</summary>
    public void Forget(string message) => _seen.Remove(message ?? "");
}
