using System.Globalization;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

[TestClass]
public class CareerTests
{
    private const string Token = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";

    private static string Json(object body) => JsonSerializer.Serialize(body, ApiJson.Options);

    private static CareerPreview Ok(CareerCurrency c, CareerMode m, decimal amount, double? current)
    {
        var p = CareerLogic.Preview(c, m, amount, current);
        Assert.IsTrue(p.Ok, p.Error);
        return p;
    }

    // ----- amount parsing -----

    [DataTestMethod]
    [DataRow("250000", "250000")]
    [DataRow("250,000", "250000")]
    [DataRow("  1,000,000  ", "1000000")]
    [DataRow("1.5m", "1500000")]
    [DataRow("1.5 M", "1500000")]
    [DataRow("2k", "2000")]
    [DataRow("2K", "2000")]
    [DataRow("1b", "1000000000")]
    [DataRow("0.0001k", "0.1")]
    [DataRow(".5", "0.5")]
    [DataRow("+7", "7")]
    [DataRow("-5k", "-5000")]
    [DataRow("−3", "-3")]
    [DataRow("-0", "0")]
    [DataRow("000250", "250")]
    public void Parse_AcceptsPlainNumbersSeparatorsAndSuffixes(string text, string expected)
    {
        var r = AmountParser.Parse(text);
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(decimal.Parse(expected, CultureInfo.InvariantCulture), r.Value);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("abc")]
    [DataRow("1e5")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-")]
    [DataRow("k")]
    [DataRow("5.")]
    [DataRow("1.2.3")]
    [DataRow("--1")]
    [DataRow("1 000")]
    [DataRow("1,5m")]
    [DataRow("1,2345")]
    [DataRow("12,34")]
    [DataRow(",500")]
    [DataRow("٣٤")] // Arabic-Indic digits are not digits here
    [DataRow("1.1234567")]
    [DataRow("1234567890123456")]
    [DataRow("99999999999999999999999999999999")]
    public void Parse_RejectsWithAMessage(string text)
    {
        var r = AmountParser.Parse(text);
        Assert.IsFalse(r.Ok);
        Assert.IsFalse(string.IsNullOrWhiteSpace(r.Error));
        Assert.AreEqual(string.IsNullOrWhiteSpace(text), r.Empty, "only an empty box is 'empty' (no red error)");
    }

    [TestMethod]
    public void Parse_AndFormat_AreCultureIndependent()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "fr-FR", "ar-SA", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.AreEqual(1500000m, AmountParser.Parse("1.5m").Value, name);
                Assert.AreEqual(250000m, AmountParser.Parse("250,000").Value, name);
                Assert.IsFalse(AmountParser.Parse("1,5m").Ok, name);
                Assert.AreEqual("1,234,567.5", CareerFormat.Number(1234567.5), name);
                Assert.AreEqual("-1,200", CareerFormat.Number(-1200), name);
                Assert.AreEqual("2000", CareerFormat.Number(2000).Replace(",", ""), name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [TestMethod]
    public void Format_RoundsToTwoDecimals_AndNeverShowsNegativeZero()
    {
        Assert.AreEqual("0", CareerFormat.Number(-0.001));
        Assert.AreEqual("0.13", CareerFormat.Number(0.125));
        Assert.AreEqual("1,000,000,000,000", CareerFormat.Number(1e12));
        Assert.AreEqual("-", CareerFormat.Number(double.NaN));
        Assert.AreEqual("-", CareerFormat.Number((double?)null));
    }

    // ----- bounds and the preview -----

    [TestMethod]
    public void Preview_FollowsTheServersBounds()
    {
        // Funds: |amount| <= 1e12 and the result within +-1e12, both ends included.
        Assert.AreEqual(1e12, Ok(CareerCurrency.Funds, CareerMode.Set, 1_000_000_000_000m, 5).Result);
        Assert.AreEqual(-1e12, Ok(CareerCurrency.Funds, CareerMode.Set, -1_000_000_000_000m, 5).Result);
        Assert.AreEqual(1e12, Ok(CareerCurrency.Funds, CareerMode.Add, 400_000_000_000m, 600_000_000_000).Result);
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Set, 1_000_000_000_001m, 5).Ok);
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Add, 400_000_000_001m, 600_000_000_000).Ok, "the result would pass 1e12");
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Remove, 600_000_000_001m, -400_000_000_000).Ok, "the result would pass -1e12");
        Assert.AreEqual(-300.0, Ok(CareerCurrency.Funds, CareerMode.Remove, 500m, 200).Result, "funds may go negative");

        // Science: |amount| <= 1e9 and the result never below 0.
        Assert.AreEqual(1e9, Ok(CareerCurrency.Science, CareerMode.Set, 1_000_000_000m, 3).Result);
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Science, CareerMode.Add, 1_000_000_001m, 3).Ok);
        Assert.AreEqual(0.0, Ok(CareerCurrency.Science, CareerMode.Remove, 340m, 340).Result);
        var below = CareerLogic.Preview(CareerCurrency.Science, CareerMode.Remove, 341m, 340);
        Assert.IsFalse(below.Ok);
        StringAssert.Contains(below.Error, "below 0");
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Science, CareerMode.Set, -1m, 340).Ok);
    }

    [TestMethod]
    public void Preview_SignsTheAmount_AndRejectsWrongSigns()
    {
        Assert.AreEqual(250000.0, Ok(CareerCurrency.Funds, CareerMode.Add, 250000m, 1_200_000).SignedAmount);
        Assert.AreEqual(-250000.0, Ok(CareerCurrency.Funds, CareerMode.Remove, 250000m, 1_200_000).SignedAmount);
        Assert.AreEqual(950000.0, Ok(CareerCurrency.Funds, CareerMode.Remove, 250000m, 1_200_000).Result);
        Assert.AreEqual(5.0, Ok(CareerCurrency.Funds, CareerMode.Set, 5m, 1_200_000).SignedAmount);
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Add, 0m, 1).Ok);
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Add, -5m, 1).Ok, "Add with a minus sign is what Remove is for");
        Assert.IsFalse(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Remove, -5m, 1).Ok);
        Assert.IsTrue(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Set, 0m, 1).Ok, "setting to 0 is allowed");

        // Without a known current value only the amount limits can be checked.
        var unknown = Ok(CareerCurrency.Funds, CareerMode.Add, 5m, null);
        Assert.IsNull(unknown.Result);
    }

    [TestMethod]
    public void Evaluate_ExplainsWhichButtonsCannotBeUsed()
    {
        var empty = CareerLogic.Evaluate(CareerCurrency.Science, "", 340);
        Assert.IsTrue(empty.Parse.Empty);
        Assert.IsFalse(empty.HintIsError, "an empty box is a prompt, not an error");

        var bad = CareerLogic.Evaluate(CareerCurrency.Science, "12x", 340);
        Assert.IsTrue(bad.HintIsError);
        Assert.IsNull(bad.Add, "no button works while the text is not a number");

        var one = CareerLogic.Evaluate(CareerCurrency.Science, "1k", 340);
        Assert.IsTrue(one.Add.Ok && one.Set.Ok && !one.Remove.Ok);
        Assert.IsFalse(one.HintIsError);
        StringAssert.StartsWith(one.Hint, "= 1,000");
        StringAssert.Contains(one.Hint, "Remove:");

        var huge = CareerLogic.Evaluate(CareerCurrency.Science, "2b", 340);
        Assert.IsTrue(huge.HintIsError);
        Assert.IsFalse(huge.Hint.Contains("Add:"), "one shared reason is said once");
    }

    // ----- the request -----

    [TestMethod]
    public void Body_HasTheContractShape()
    {
        Assert.AreEqual("{\"mode\":\"add\",\"amount\":250000,\"announce\":false}", Json(CareerChangeBody.Create(Ok(CareerCurrency.Funds, CareerMode.Add, 250000m, 1), false, null)));
        Assert.AreEqual("{\"mode\":\"add\",\"amount\":-250000,\"announce\":true,\"note\":\"oops\"}", Json(CareerChangeBody.Create(Ok(CareerCurrency.Funds, CareerMode.Remove, 250000m, 300000), true, "  oops ")));
        Assert.AreEqual("{\"mode\":\"set\",\"amount\":1500000,\"announce\":false}", Json(CareerChangeBody.Create(Ok(CareerCurrency.Funds, CareerMode.Set, 1.5m * 1_000_000m, 1), false, "   ")));
        Assert.AreEqual("{\"mode\":\"add\",\"amount\":0.5,\"announce\":false}", Json(CareerChangeBody.Create(Ok(CareerCurrency.Science, CareerMode.Add, 0.5m, 1), false, null)));
        Assert.ThrowsException<ArgumentException>(() => CareerChangeBody.Create(CareerLogic.Preview(CareerCurrency.Funds, CareerMode.Add, -1m, 1), false, null));
    }

    [TestMethod]
    public void Note_IsCleanedAndCutTo80Characters()
    {
        Assert.AreEqual("a b c", CareerChangeBody.CleanNote(" a\r\nb\tc "));
        Assert.AreEqual(new string('a', 80), CareerChangeBody.CleanNote(new string('a', 200)));
        Assert.AreEqual(new string('a', 79), CareerChangeBody.CleanNote(new string('a', 79) + "\U0001F600 tail"), "the emoji would be cut in half");
        Assert.IsNull(CareerChangeBody.CleanNote("\n \t"));
    }

    // ----- the answers -----

    private static ApiResult<T> Fail<T>(ApiOutcome outcome, string error, int status) where T : class => ApiResult<T>.Fail(outcome, error, status);

    [TestMethod]
    public void Outcome_Success_ShowsPreviousAndCurrent()
    {
        var preview = Ok(CareerCurrency.Funds, CareerMode.Add, 250000m, 1_200_000);
        var result = new ApiResult<CareerChangeResultDto> { Outcome = ApiOutcome.Ok, StatusCode = 200, Value = new CareerChangeResultDto { Ok = true, Currency = "funds", Previous = 1_200_000, Current = 1_450_000 } };
        var o = CareerText.Outcome(preview, result);
        Assert.IsTrue(o.Success);
        Assert.AreEqual("Added 250,000 funds. Funds went from 1,200,000 to 1,450,000.", o.Message);
        Assert.AreEqual(1_450_000.0, o.Current);

        var set = CareerText.Outcome(Ok(CareerCurrency.Science, CareerMode.Set, 100m, 5), new ApiResult<CareerChangeResultDto> { Outcome = ApiOutcome.Ok, Value = new CareerChangeResultDto { Ok = true, Previous = 5, Current = 100 } });
        Assert.AreEqual("Science set to 100. Science went from 5 to 100.", set.Message);

        var refused = CareerText.Outcome(preview, new ApiResult<CareerChangeResultDto> { Outcome = ApiOutcome.Ok, Value = new CareerChangeResultDto { Ok = false } });
        Assert.IsFalse(refused.Success);
    }

    [TestMethod]
    public void Outcome_Errors_KeepTheServersMessage_AndOldServersAreTold()
    {
        var preview = Ok(CareerCurrency.Funds, CareerMode.Add, 5m, 1);

        var bad = CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Failed, "the result would be outside +-1,000,000,000,000", 400));
        Assert.IsFalse(bad.Success);
        Assert.AreEqual("Not changed: the result would be outside +-1,000,000,000,000", bad.Message);

        var conflict = CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Failed, "Funds are not available in Science mode.", 409));
        Assert.AreEqual("Not changed: Funds are not available in Science mode.", conflict.Message);
        Assert.IsFalse(conflict.TooOld);

        // The client turns a 404 "not found" into NotSupported; any other 404 is just as much "no such endpoint" for these calls.
        foreach (var old in new[] { Fail<CareerChangeResultDto>(ApiOutcome.NotSupported, "the server does not have this admin API call", 404), Fail<CareerChangeResultDto>(ApiOutcome.Failed, "no route", 404) })
        {
            var o = CareerText.Outcome(preview, old);
            Assert.IsTrue(o.TooOld);
            StringAssert.StartsWith(o.Message, "This server is too old for Career controls");
        }

        StringAssert.Contains(CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Unreachable, "timed out", 0)).Message, "may or may not");
        StringAssert.Contains(CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Unreachable, "connection refused", 0)).Message, "connection refused");
        StringAssert.Contains(CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Unauthorized, "x", 401)).Message, "admin token");
        StringAssert.Contains(CareerText.Outcome(preview, Fail<CareerChangeResultDto>(ApiOutcome.Failed, null, 500)).Message, "500");
    }

    [TestMethod]
    public void Load_MapsTheStateAndTheFailures()
    {
        var json = "{\"gameMode\":\"Science\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":true,\"value\":842.5}}";
        var state = ApiJson.TryParse<CareerStateDto>(json);
        var load = CareerText.Load(new ApiResult<CareerStateDto> { Outcome = ApiOutcome.Ok, Value = state });
        Assert.AreEqual(CareerLoadKind.Ready, load.Kind);
        Assert.IsFalse(load.State.Funds.Available);
        Assert.IsNull(load.State.Funds.Value);
        Assert.AreEqual(842.5, load.State.Of(CareerCurrency.Science).Value);
        Assert.AreEqual("Funds only exist in Career mode. This server runs Science mode.", CareerText.UnavailableText(CareerCurrency.Funds, "Science"));
        Assert.AreEqual("Science only exists in Career and Science modes. This server runs Sandbox mode.", CareerText.UnavailableText(CareerCurrency.Science, "Sandbox"));
        StringAssert.Contains(CareerText.UnavailableText(CareerCurrency.Funds, "Career"), "not available");

        Assert.AreEqual(CareerLoadKind.TooOld, CareerText.Load(Fail<CareerStateDto>(ApiOutcome.NotSupported, "the server does not have this admin API call", 404)).Kind);
        var notReady = CareerText.Load(Fail<CareerStateDto>(ApiOutcome.Failed, "The server is not ready yet.", 409));
        Assert.AreEqual(CareerLoadKind.Problem, notReady.Kind);
        Assert.AreEqual("The server is not ready yet.", notReady.Message);

        Assert.IsNotNull(ApiJson.TryParse<CareerStateDto>("{}").Funds, "a missing block is an unavailable currency, not a crash");
    }

    // ----- the confirmation text -----

    [TestMethod]
    public void ConfirmText_SaysPlainlyWhatWillHappen()
    {
        var add = Ok(CareerCurrency.Funds, CareerMode.Add, 250000m, 1_200_000);
        Assert.AreEqual("Add 250,000 funds.\nFunds will go from 1,200,000 to 1,450,000.\n\n3 players are online and will see it immediately.", CareerText.ConfirmText(add, 3, false, null));
        Assert.AreEqual("Add funds", CareerText.DialogTitle(add));

        var withExtras = CareerText.ConfirmText(add, 1, true, "bonus");
        StringAssert.Contains(withExtras, "1 player is online");
        StringAssert.Contains(withExtras, "chat");
        StringAssert.EndsWith(withExtras, "Note: bonus");
        StringAssert.Contains(CareerText.ConfirmText(add, 0, false, null), "Nobody is online");
        StringAssert.Contains(CareerText.ConfirmText(add, null, false, null), "Players in the game will see it immediately");

        var remove = CareerText.ConfirmText(Ok(CareerCurrency.Science, CareerMode.Remove, 40m, 340), 2, false, null);
        StringAssert.StartsWith(remove, "Remove 40 science.\nScience will go from 340 to 300.");

        var set = CareerText.ConfirmText(Ok(CareerCurrency.Funds, CareerMode.Set, 5_000_000m, 1_200_000), 2, false, null);
        StringAssert.StartsWith(set, "Set funds to 5,000,000?");
        StringAssert.Contains(set, "overwrites the shared funds for everyone");
        StringAssert.Contains(set, "from 1,200,000 to 5,000,000 (a change of +3,800,000)");
        StringAssert.Contains(set, "not kept");
    }

    // ----- the real client against a fake server -----

    private static (LiveSession Session, string Folder) Live(int port)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vmpcontrol-career-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Config"));
        File.WriteAllText(Path.Combine(folder, "Config", "admin-token.txt"), Token);
        return (new LiveSession(new Profile { Name = "t", Mode = ProfileMode.Host, ServerFolder = folder, ApiPort = port }), folder);
    }

    [TestMethod]
    public async Task TheSession_CallsTheCareerEndpoints_AndMapsTheAnswers()
    {
        var seen = new List<string>();
        using var server = new ApiClientTests.FakeServer();
        server.Handler = (m, p) =>
        {
            seen.Add(m + " " + p);
            return p.EndsWith("/career") ? (200, "{\"gameMode\":\"Career\",\"funds\":{\"available\":true,\"value\":1200000},\"science\":{\"available\":true,\"value\":842.5}}", 0)
                : p.EndsWith("/career/funds") ? (200, "{\"ok\":true,\"currency\":\"funds\",\"previous\":1200000,\"current\":950000}", 0)
                : (409, "{\"error\":\"Science is not available in Sandbox mode.\"}", 0);
        };
        var (session, folder) = Live(server.Port);
        using (session)
        {
            try
            {
                var state = CareerText.Load(await session.GetCareerAsync(CancellationToken.None));
                Assert.AreEqual(CareerLoadKind.Ready, state.Kind);
                Assert.AreEqual(1_200_000.0, state.State.Funds.Value);
                Assert.AreEqual("Bearer " + Token, server.LastAuth);

                var remove = Ok(CareerCurrency.Funds, CareerMode.Remove, 250000m, 1_200_000);
                var changed = await session.ChangeCareerAsync(CareerCurrency.Funds, CareerChangeBody.Create(remove, true, "refund"), CancellationToken.None);
                Assert.AreEqual("{\"mode\":\"add\",\"amount\":-250000,\"announce\":true,\"note\":\"refund\"}", server.LastBody);
                Assert.AreEqual(950000.0, CareerText.Outcome(remove, changed).Current);

                var science = Ok(CareerCurrency.Science, CareerMode.Add, 100m, 1);
                var refused = CareerText.Outcome(science, await session.ChangeCareerAsync(CareerCurrency.Science, CareerChangeBody.Create(science, false, null), CancellationToken.None));
                Assert.AreEqual("Not changed: Science is not available in Sandbox mode.", refused.Message);

                CollectionAssert.AreEqual(new[] { "GET /api/v1/career", "POST /api/v1/career/funds", "POST /api/v1/career/science" }, seen);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [TestMethod]
    public async Task AnOldServer_AnswersWith404_AndTheTabSaysSo()
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (404, "{\"error\":\"not found\"}", 0) };
        var (session, folder) = Live(server.Port);
        using (session)
        {
            try
            {
                Assert.AreEqual(CareerLoadKind.TooOld, CareerText.Load(await session.GetCareerAsync(CancellationToken.None)).Kind);
                var preview = Ok(CareerCurrency.Funds, CareerMode.Add, 5m, 1);
                var outcome = CareerText.Outcome(preview, await session.ChangeCareerAsync(CareerCurrency.Funds, CareerChangeBody.Create(preview, false, null), CancellationToken.None));
                Assert.IsTrue(outcome.TooOld);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    // ----- the sample server -----

    [TestMethod]
    public void MockCareer_AppliesChangesLocally_WithTheRealBounds()
    {
        var mock = new MockCareer(MockCareerMode.Career);
        var before = mock.Get().Value;
        Assert.AreEqual("Career", before.GameMode);

        var add = mock.Apply(CareerCurrency.Funds, CareerChangeBody.Create(Ok(CareerCurrency.Funds, CareerMode.Add, 250000m, before.Funds.Value), false, null));
        Assert.IsTrue(add.IsOk);
        Assert.AreEqual(before.Funds.Value + 250000, add.Value.Current);
        Assert.AreEqual(add.Value.Current, mock.Get().Value.Funds.Value);

        // Bypass the client-side preview: the sample server enforces the bounds itself, with a 400.
        var tooMuch = mock.Apply(CareerCurrency.Science, new CareerChangeBody { Mode = "add", Amount = -1e6 });
        Assert.AreEqual(400, tooMuch.StatusCode);
        Assert.AreEqual(400, mock.Apply(CareerCurrency.Funds, new CareerChangeBody { Mode = "set", Amount = 2e12 }).StatusCode);

        Assert.AreEqual(409, new MockCareer(MockCareerMode.Science).Apply(CareerCurrency.Funds, new CareerChangeBody { Mode = "add", Amount = 1 }).StatusCode);
        Assert.IsFalse(new MockCareer(MockCareerMode.Sandbox).Get().Value.Science.Available);
        Assert.AreEqual(CareerLoadKind.TooOld, CareerText.Load(new MockCareer(MockCareerMode.Old).Get()).Kind);
        Assert.AreEqual(409, new MockCareer(MockCareerMode.Error).Apply(CareerCurrency.Funds, new CareerChangeBody { Mode = "add", Amount = 1 }).StatusCode);
        Assert.AreEqual(ApiOutcome.Unreachable, new MockCareer(MockCareerMode.Offline).Get().Outcome);
    }

    [TestMethod]
    public void CommandLine_MockCareerSwitch()
    {
        Assert.AreEqual(MockCareerMode.Career, CommandLineOptions.Parse(new[] { "--mock" }).CareerMock);
        Assert.AreEqual(MockCareerMode.Old, CommandLineOptions.Parse(new[] { "--mock", "--mock-career", "OLD" }).CareerMock);
        Assert.AreEqual(0, CommandLineOptions.Parse(new[] { "--mock", "--mock-career", "sandbox" }).Problems.Count);
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--mock", "--mock-career", "nonsense" }).Problems.Count);
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--mock", "--mock-career", "7" }).Problems.Count, "a number is not a mode");
        Assert.AreEqual(1, CommandLineOptions.Parse(new[] { "--mock-career" }).Problems.Count);
    }
}
