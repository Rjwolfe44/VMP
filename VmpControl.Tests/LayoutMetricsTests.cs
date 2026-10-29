using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

[TestClass]
public class DpiMathTests
{
    [TestMethod]
    public void Scale_RoundsToWholePixels_AndKeepsUnitsAt100Percent()
    {
        Assert.AreEqual(8, DpiMath.Scale(8, 96));
        Assert.AreEqual(14, DpiMath.Scale(8, 168)); // 175%
        Assert.AreEqual(10, DpiMath.Scale(8, 120)); // 125%: 10.0
        Assert.AreEqual(9, DpiMath.Scale(6, 144)); // 150%
        Assert.AreEqual(5, DpiMath.Scale(4, 120)); // 5.0
        Assert.AreEqual(5, DpiMath.Scale(3, 168)); // 5.25
    }

    [TestMethod]
    public void Factor_ToleratesAnUnknownDpi()
    {
        Assert.AreEqual(1f, DpiMath.Factor(0));
        Assert.AreEqual(1.75f, DpiMath.Factor(168), 0.0001f);
    }

    [TestMethod]
    public void FontPoints_RescaleByWindowDpiOverSystemDpi()
    {
        // A font in points becomes pixels at the system DPI, so a 100% monitor on a 175% system needs 96/168 of the points.
        Assert.AreEqual(9.5f * 96 / 168, DpiMath.FontPoints(9.5f, 96, 168), 0.0001f);
        Assert.AreEqual(9.5f, DpiMath.FontPoints(9.5f, 168, 168), 0.0001f);
        Assert.AreEqual(9.5f, DpiMath.FontPoints(9.5f, 0, 168), 0.0001f);
    }

    [TestMethod]
    public void RowAndHeaderHeights_GrowWithFontAndDpi_AndTheHeaderIsTaller()
    {
        Assert.AreEqual(17 + 8, DpiMath.RowHeight(17, 96));
        Assert.AreEqual(30 + 14, DpiMath.RowHeight(30, 168));
        Assert.IsTrue(DpiMath.HeaderHeight(30, 168) > DpiMath.RowHeight(30, 168));
    }

    [TestMethod]
    public void SpacingScale_IsFourEightTwelveSixteen()
    {
        CollectionAssert.AreEqual(new[] { 4, 8, 12, 16 }, new[] { Spacing.Xs, Spacing.S, Spacing.M, Spacing.L });
    }
}

[TestClass]
public class TextWrapTests
{
    [TestMethod]
    public void WrapWidth_SubtractsMargins_AndNeverGoesBelowTheMinimum()
    {
        Assert.AreEqual(380, TextWrap.WrapWidth(400, 20));
        Assert.AreEqual(40, TextWrap.WrapWidth(30, 0));
        Assert.AreEqual(20, TextWrap.WrapWidth(10, 5, 20));
        Assert.AreEqual(400, TextWrap.WrapWidth(400, -8)); // a negative margin is ignored
    }

    [TestMethod]
    public void IsUnbounded_RecognisesZeroAndHugeSentinels()
    {
        Assert.IsTrue(TextWrap.IsUnbounded(0));
        Assert.IsTrue(TextWrap.IsUnbounded(int.MaxValue));
        Assert.IsFalse(TextWrap.IsUnbounded(640));
    }
}

[TestClass]
public class DialogSizingTests
{
    [TestMethod]
    public void MaxHeight_IsSeventyPercentOfTheWorkingArea()
    {
        Assert.AreEqual(756, DialogSizing.MaxHeight(1080));
        Assert.AreEqual(0, DialogSizing.MaxHeight(-5));
    }

    [TestMethod]
    public void ListHeight_TakesOnlyTheSpaceItNeeds_UntilTheDialogIsFull()
    {
        Assert.AreEqual(200, DialogSizing.ListHeight(200, 300, 756, 80)); // fits: exactly the content
        Assert.AreEqual(456, DialogSizing.ListHeight(2000, 300, 756, 80)); // long: what is left of the maximum
        Assert.AreEqual(80, DialogSizing.ListHeight(2000, 740, 756, 80)); // almost no room: the minimum
        Assert.AreEqual(30, DialogSizing.ListHeight(30, 740, 756, 80)); // never more than the content
    }

    [TestMethod]
    public void CenterOn_CentersOnTheParent_AndStaysInsideTheWorkingArea()
    {
        Assert.AreEqual((450, 400), DialogSizing.CenterOn(100, 100, 1000, 800, 300, 200, 0, 0, 1920, 1080));
        // A parent near the left edge of a monitor: the dialog is pushed back inside.
        Assert.AreEqual((0, 0), DialogSizing.CenterOn(-100, -100, 200, 200, 400, 300, 0, 0, 1920, 1080));
        Assert.AreEqual((1520, 780), DialogSizing.CenterOn(1800, 1000, 200, 200, 400, 300, 0, 0, 1920, 1080));
    }
}

[TestClass]
public class ErrorHandlingTests
{
    [TestMethod]
    public void Classify_SeparatesBackgroundFailuresFromBugsAndFatalErrors()
    {
        Assert.AreEqual(ErrorKind.Background, ErrorPolicy.Classify(new HttpRequestException("down")));
        Assert.AreEqual(ErrorKind.Background, ErrorPolicy.Classify(new TaskCanceledException()));
        Assert.AreEqual(ErrorKind.Background, ErrorPolicy.Classify(new IOException("disk")));
        Assert.AreEqual(ErrorKind.Background, ErrorPolicy.Classify(new InvalidOperationException("outer", new TimeoutException())));
        Assert.AreEqual(ErrorKind.Unexpected, ErrorPolicy.Classify(new NullReferenceException()));
        Assert.AreEqual(ErrorKind.Fatal, ErrorPolicy.Classify(new OutOfMemoryException()));
        Assert.AreEqual(ErrorKind.Fatal, ErrorPolicy.Classify(new AggregateException(new IOException(), new OutOfMemoryException())));
    }

    [TestMethod]
    public void Gate_ReportsEachDistinctMessageOnce_AndAgainAfterForget()
    {
        var gate = new DistinctMessageGate(3);
        Assert.IsTrue(gate.FirstTime("a"));
        Assert.IsFalse(gate.FirstTime("a"));
        Assert.IsTrue(gate.FirstTime("b"));
        gate.Forget("a");
        Assert.IsTrue(gate.FirstTime("a"));
    }

    [TestMethod]
    public void Gate_IsBounded_SoALongRunNeverGrowsWithoutLimit()
    {
        var gate = new DistinctMessageGate(2);
        Assert.IsTrue(gate.FirstTime("1"));
        Assert.IsTrue(gate.FirstTime("2"));
        Assert.IsTrue(gate.FirstTime("3")); // the memory was full and starts over
        Assert.IsFalse(gate.FirstTime("3"));
    }

    [TestMethod]
    public void Describe_NamesTheTypeAndTheMessage()
    {
        Assert.AreEqual("IOException: nope", ErrorPolicy.Describe(new IOException("nope")));
    }
}

[TestClass]
public class TestingSwitchTests
{
    [TestMethod]
    public void DpiUnaware_Screen_AndWindow_AreParsed()
    {
        var o = CommandLineOptions.Parse(new[] { "--dpi-unaware", "--screen", "1", "--window", "Minimum" });
        Assert.IsTrue(o.DpiUnaware);
        Assert.AreEqual(1, o.Screen);
        Assert.AreEqual("minimum", o.WindowMode);
        Assert.AreEqual(0, o.Problems.Count);

        var none = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.IsFalse(none.DpiUnaware);
        Assert.IsNull(none.Screen);
        Assert.IsNull(none.WindowMode);
    }

    [TestMethod]
    public void BadValues_AreReported()
    {
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--screen", "left" }).Problems.Count);
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--screen", "-1" }).Problems.Count);
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--window", "huge" }).Problems.Count);
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--screen" }).Problems.Count);
    }
}
