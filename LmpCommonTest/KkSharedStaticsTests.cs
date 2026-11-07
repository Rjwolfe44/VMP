using System.Collections.Generic;
using System.Linq;
using LmpCommon.KerbalKonstructs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    /// <summary>
    /// Rules for sharing Kerbal Konstructs statics that one player edited (docs/kk-live-sharing.md). The KK side of the
    /// client cannot run outside the game, so the identity, set and apply decisions are kept here and pinned by tests.
    /// </summary>
    [TestClass]
    public class KkSharedStaticsTests
    {
        private static KkSharedApplyFacts Ok() => new KkSharedApplyFacts
        {
            FormatValue = "1",
            Key = "uuid:abc",
            EntryUuid = "abc",
            ModelName = "pad",
            LocalModelName = "pad",
            BodyName = "Kerbin",
            EntryBytes = 2000,
            ModelAvailable = true,
            BodyAvailable = true,
            GroupExists = true
        };

        [TestMethod]
        public void Key_UsesTheUuidOnlyWhenEveryClientHasTheSameOne()
        {
            Assert.AreEqual("uuid:abc", KkSharedStaticsFormat.BuildKey("abc", true, "pad", "Kerbin", "KSC", 1, 2, 3));

            //A UUID KK made up at game start differs per client: fall back to what every client loads from the same file.
            var a = KkSharedStaticsFormat.BuildKey("random-on-client-a", false, "pad", "Kerbin", "KSC", 10.04f, -0.02f, 3f);
            var b = KkSharedStaticsFormat.BuildKey("random-on-client-b", false, "pad", "Kerbin", "KSC", 10.01f, 0.03f, 3.04f);
            Assert.AreEqual(a, b, "Float noise below 0.05 m (and -0 vs 0) must give the same identity.");
            Assert.IsTrue(KkSharedStaticsFormat.IsLocatorKey(a));
            Assert.AreNotEqual(a, KkSharedStaticsFormat.BuildKey("x", false, "pad", "Kerbin", "OtherGroup", 10f, 0f, 3f));
        }

        [TestMethod]
        public void Key_Validation()
        {
            Assert.IsTrue(KkSharedStaticsFormat.IsValidKey("uuid:1"));
            Assert.IsTrue(KkSharedStaticsFormat.IsValidKey("loc:a|b|c|0.0|0.0|0.0"));
            Assert.IsFalse(KkSharedStaticsFormat.IsValidKey("uuid:"));
            Assert.IsFalse(KkSharedStaticsFormat.IsValidKey("abc"));
            Assert.IsFalse(KkSharedStaticsFormat.IsValidKey(null));
            Assert.AreEqual("1", KkSharedStaticsFormat.UuidOf("uuid:1"));
            Assert.IsNull(KkSharedStaticsFormat.UuidOf("loc:x"));
        }

        [TestMethod]
        public void Format_NewerVersionsAreSkipped_MissingMeansFirstVersion()
        {
            Assert.IsTrue(KkSharedStaticsFormat.IsSupportedFormat(null));
            Assert.IsTrue(KkSharedStaticsFormat.IsSupportedFormat("1"));
            Assert.IsFalse(KkSharedStaticsFormat.IsSupportedFormat("2"));
            Assert.IsFalse(KkSharedStaticsFormat.IsSupportedFormat("abc"));
            var f = Ok();
            f.FormatValue = "99";
            Assert.AreEqual(KkSharedApplyDecision.SkipUnsupportedFormat, KkSharedStaticPlanner.Decide(f));
        }

        [TestMethod]
        public void Vector_ParsesKspText_Invariantly()
        {
            Assert.IsTrue(KkSharedStaticsFormat.TryParseVector("1.5,-2,3E-2", out var x, out var y, out var z));
            Assert.AreEqual(1.5f, x);
            Assert.AreEqual(-2f, y);
            Assert.AreEqual(0.03f, z, 1e-6);
            Assert.IsFalse(KkSharedStaticsFormat.TryParseVector("1,2", out _, out _, out _));
            Assert.IsFalse(KkSharedStaticsFormat.TryParseVector(null, out _, out _, out _));
        }

        [TestMethod]
        public void Decide_ReplaceOrSpawn_WhenEverythingIsThere()
        {
            var f = Ok();
            Assert.AreEqual(KkSharedApplyDecision.SpawnNew, KkSharedStaticPlanner.Decide(f));
            f.LocalFound = true;
            Assert.AreEqual(KkSharedApplyDecision.Replace, KkSharedStaticPlanner.Decide(f));
            f.LocalIdenticalToEntry = true;
            Assert.AreEqual(KkSharedApplyDecision.SkipIdentical, KkSharedStaticPlanner.Decide(f));
            Assert.IsFalse(KkSharedStaticPlanner.IsProblem(KkSharedApplyDecision.SkipIdentical));
        }

        [TestMethod]
        public void Decide_MissingModelOrBody_NeverTouchesTheLocalStatic()
        {
            //Requirement: a client without the model must not break or wipe anything; the local static stays as it is.
            var f = Ok();
            f.LocalFound = true;
            f.ModelAvailable = false;
            Assert.AreEqual(KkSharedApplyDecision.RejectMissingModel, KkSharedStaticPlanner.Decide(f));
            f.ModelAvailable = true;
            f.BodyAvailable = false;
            Assert.AreEqual(KkSharedApplyDecision.RejectMissingBody, KkSharedStaticPlanner.Decide(f));
            Assert.IsTrue(KkSharedStaticPlanner.IsProblem(KkSharedApplyDecision.RejectMissingBody));
        }

        [TestMethod]
        public void Decide_SaveGameStaticsAndGroups_AreLeftToKk()
        {
            var f = Ok();
            f.LocalFound = true;
            f.LocalIsSaveGame = true;
            Assert.AreEqual(KkSharedApplyDecision.RejectSaveGameInstance, KkSharedStaticPlanner.Decide(f));

            f = Ok();
            f.GroupIsSaveGame = true;
            Assert.AreEqual(KkSharedApplyDecision.RejectSaveGameGroup, KkSharedStaticPlanner.Decide(f), "KK deletes save-game groups with their children on load.");
        }

        [TestMethod]
        public void Decide_MissingGroup_IsNeverCreatedFromSharedData()
        {
            //A group made from a shared description would get no cfg path (KK writes it into GameData on "Save all") and,
            //spawned before KK's LoadGroups, makes KK rename its own save-game group of that name (GroupCenter.Spawn).
            var f = Ok();
            f.GroupExists = false;
            Assert.AreEqual(KkSharedApplyDecision.RejectMissingGroup, KkSharedStaticPlanner.Decide(f));
            f.LocalFound = true;
            Assert.AreEqual(KkSharedApplyDecision.RejectMissingGroup, KkSharedStaticPlanner.Decide(f));
        }

        [TestMethod]
        public void CaptureKey_IsRebuiltWhenKkGaveTheStaticANewUuid()
        {
            //Spawned from uuid:a, but KK renamed the UUID to b on a collision: reusing uuid:a would send an entry whose key and
            //UUID disagree, which replaces the good entry on the server and is refused by every receiver.
            Assert.AreEqual("uuid:b", KkSharedStaticsFormat.ResolveCaptureKey("uuid:a", null, "b", true, null));
            Assert.AreEqual("uuid:b", KkSharedStaticsFormat.ResolveCaptureKey(null, "uuid:a", "b", true, null));
            Assert.AreEqual("uuid:a", KkSharedStaticsFormat.ResolveCaptureKey("uuid:a", null, "a", true, null));
            var loc = "loc:pad|Kerbin|KSC|0.0|0.0|0.0";
            Assert.AreEqual(loc, KkSharedStaticsFormat.ResolveCaptureKey(loc, null, "b", true, null), "loc: identities do not name a UUID.");
        }

        [TestMethod]
        public void FacilityStateGuard_ToRemove_BothOrders()
        {
            var states = new List<string> { "0", "1" };
            //UUID unknown to KK: KK skips it itself, nothing to remove.
            CollectionAssert.AreEqual(new int[0], KkFacilityStateGuard.ToRemove(states, null));
            //A save-game building KK just made in OnLoad whose facility was set to None (count 0): both states would throw.
            CollectionAssert.AreEqual(new[] { 0, 1 }, KkFacilityStateGuard.ToRemove(states, 0));
            //The shared static applied before the state load gave the pack copy its facility: index 0 loads, 1 does not.
            CollectionAssert.AreEqual(new[] { 1 }, KkFacilityStateGuard.ToRemove(states, 1));
            CollectionAssert.AreEqual(new int[0], KkFacilityStateGuard.ToRemove(states, 2));
        }

        [TestMethod]
        public void Measure_IsTheSameForClientAndServerText()
        {
            //KSP (client): no own name, values at column 0, nested nodes tab-indented. Server: name, braces, deeper indent.
            var client = "VmpKey = uuid:a\nUUID = a\nLaunchSite\n{\n\tLaunchSiteName = Pad\n}\n";
            var server = "\t\tSharedStatic\n\t\t{\n\t\t\tVmpKey = uuid:a\n\t\t\tUUID = a\n\t\t\tLaunchSite\n\t\t\t{\n\t\t\t\tLaunchSiteName = Pad\n\t\t\t}\n\t\t}";
            Assert.AreEqual(KkSharedStaticsFormat.MeasureContent(client), KkSharedStaticsFormat.MeasureWrapped(server, "SharedStatic"));
        }

        [TestMethod]
        public void Decide_OrderOfChecks()
        {
            var f = Ok();
            f.Key = "nope";
            Assert.AreEqual(KkSharedApplyDecision.RejectMalformed, KkSharedStaticPlanner.Decide(f));

            f = Ok();
            f.EntryBytes = KkSharedStaticsFormat.MaxEntryBytes + 1;
            Assert.AreEqual(KkSharedApplyDecision.RejectTooLarge, KkSharedStaticPlanner.Decide(f));

            f = Ok();
            f.AlreadyAppliedSameEntry = true;
            f.ModelAvailable = false;
            Assert.AreEqual(KkSharedApplyDecision.SkipAlreadyApplied, KkSharedStaticPlanner.Decide(f), "No repeated problem lines for an entry already handled.");

            f = Ok();
            f.LocalFound = true;
            f.LocalIsBeingEdited = true;
            Assert.AreEqual(KkSharedApplyDecision.DeferEditing, KkSharedStaticPlanner.Decide(f));
        }

        [TestMethod]
        public void Set_OwnEditsWinInTheUpload_ReceivedOnesAreSentBackUnchanged()
        {
            var set = new KkSharedStaticSet();
            set.SetReceived(new[] { Kv("uuid:a", "A-server"), Kv("uuid:b", "B-server") });
            Assert.IsTrue(set.SetOwn("uuid:b", "B-mine", out _));
            Assert.IsTrue(set.SetOwn("uuid:c", "C-mine", out _));
            Assert.IsFalse(set.SetOwn("uuid:c", "C-mine", out var refusal), "Same text again is no change.");
            Assert.IsNull(refusal);

            var upload = set.ForUpload();
            CollectionAssert.AreEqual(new[] { "uuid:b", "uuid:c", "uuid:a" }, upload.Select(e => e.Key).ToArray());
            Assert.AreEqual("B-mine", upload[0].Value);
            Assert.AreEqual("A-server", upload[2].Value);
        }

        [TestMethod]
        public void Set_NewJoin_ServerCopyReplacesOwnCopiesItHas_KeepsOnesItLacks()
        {
            var set = new KkSharedStaticSet();
            set.SetOwn("uuid:a", "A-mine-old", out _);
            set.SetOwn("uuid:lost", "never-reached-server", out _);

            set.SetReceived(new[] { Kv("uuid:a", "A-newer-from-someone-else") });

            var upload = set.ForUpload().ToDictionary(e => e.Key, e => e.Value);
            Assert.AreEqual("A-newer-from-someone-else", upload["uuid:a"], "A stale own copy must not undo a newer edit.");
            Assert.AreEqual("never-reached-server", upload["uuid:lost"]);
            Assert.AreEqual(1, set.OwnCount);
        }

        [TestMethod]
        public void Set_Limits()
        {
            var set = new KkSharedStaticSet();
            Assert.IsFalse(set.SetOwn("uuid:big", new string('x', KkSharedStaticsFormat.MaxEntryBytes + 1), out var refusal));
            Assert.IsNotNull(refusal);
            Assert.IsFalse(set.SetOwn("bad", "x", out _));

            for (var i = 0; i < KkSharedStaticsFormat.MaxEntries; i++)
                Assert.IsTrue(set.SetOwn("uuid:" + i, "x", out _));
            Assert.IsFalse(set.SetOwn("uuid:extra", "x", out refusal));
            Assert.IsTrue(set.SetOwn("uuid:0", "edited", out _), "Editing an entry already shared is always allowed.");

            set.SetReceived(new[] { Kv("uuid:received", "r"), Kv("bad-key", "r") });
            Assert.AreEqual(1, set.ReceivedCount, "Received entries with a bad key are dropped.");
            Assert.AreEqual(KkSharedStaticsFormat.MaxEntries, set.ForUpload().Count);
        }

        [TestMethod]
        public void CaptureKey_KeepsALocatorIdentityAfterKkWroteTheUuidAndTheGameRestarted()
        {
            //Session 1: the pack static had no UUID in its file, so it was shared under its locator.
            var loc = KkSharedStaticsFormat.LocatorKey("pad", "Kerbin", "KSC", 1, 2, 3);
            Assert.AreEqual(loc, KkSharedStaticsFormat.ResolveCaptureKey(null, null, "random-1", false, loc));

            //Session 2 after a restart: KK wrote UUID random-1 into the player's file, so it now looks stable; the server's
            //entry with that UUID must keep the loc: identity, otherwise a second (uuid:) entry duplicates the static elsewhere.
            Assert.AreEqual(loc, KkSharedStaticsFormat.ResolveCaptureKey(null, loc, "random-1", true, "loc:moved|Kerbin|KSC|9.0|9.0|9.0"));

            Assert.AreEqual("uuid:x", KkSharedStaticsFormat.ResolveCaptureKey("uuid:x", loc, "x", true, loc), "A static spawned from an entry keeps that entry's identity.");
            Assert.AreEqual("uuid:y", KkSharedStaticsFormat.ResolveCaptureKey(null, null, "y", true, loc));
            Assert.IsNull(KkSharedStaticsFormat.ResolveCaptureKey(null, null, null, false, null));
        }

        [TestMethod]
        public void Decide_HostileEntries_CannotRewriteAnotherStatic()
        {
            var f = Ok();
            f.EntryUuid = "someone-elses-uuid";
            Assert.AreEqual(KkSharedApplyDecision.RejectKeyMismatch, KkSharedStaticPlanner.Decide(f), "uuid: identity must match the UUID inside.");

            f = Ok();
            f.LocalFound = true;
            f.LocalModelName = "runway";
            Assert.AreEqual(KkSharedApplyDecision.RejectModelMismatch, KkSharedStaticPlanner.Decide(f), "Never replace a static by a different model.");

            f = Ok();
            f.EntryUuid = null;
            Assert.AreEqual(KkSharedApplyDecision.RejectMalformed, KkSharedStaticPlanner.Decide(f));

            f = Ok();
            f.Key = "loc:pad|Kerbin|KSC|1.0|2.0|3.0";
            f.EntryUuid = "anything";
            Assert.AreEqual(KkSharedApplyDecision.SpawnNew, KkSharedStaticPlanner.Decide(f), "A loc: entry carries the sharer's own UUID.");
        }

        [TestMethod]
        public void FacilityStateGuard_OnlyIndexesTheLocalStaticHas()
        {
            //KK reads myFacilities[int.Parse(Index)] unchecked: a static with no facility must not get a state for index 0.
            Assert.IsFalse(KkFacilityStateGuard.IsLoadable("0", 0));
            Assert.IsTrue(KkFacilityStateGuard.IsLoadable("0", 1));
            Assert.IsFalse(KkFacilityStateGuard.IsLoadable("1", 1));
            Assert.IsFalse(KkFacilityStateGuard.IsLoadable("-1", 3));
            Assert.IsFalse(KkFacilityStateGuard.IsLoadable(null, 3));
            Assert.IsFalse(KkFacilityStateGuard.IsLoadable("x", 3));
        }

        [TestMethod]
        public void Set_TotalUploadSizeIsCapped()
        {
            var set = new KkSharedStaticSet();
            var big = new string('x', KkSharedStaticsFormat.MaxEntryBytes - 10);
            var received = Enumerable.Range(0, 60).Select(i => Kv("uuid:r" + i, big)).ToArray();
            set.SetReceived(received);

            var upload = set.ForUpload();
            Assert.IsTrue(upload.Sum(e => e.Value.Length) <= KkSharedStaticsFormat.MaxContainerBytes);
            Assert.IsTrue(upload.Count < 60);

            var own = new KkSharedStaticSet();
            var accepted = Enumerable.Range(0, 60).Count(i => own.SetOwn("uuid:o" + i, big, out _));
            Assert.IsTrue(accepted * big.Length <= KkSharedStaticsFormat.MaxContainerBytes && accepted < 60);
            Assert.IsTrue(own.SetOwn("uuid:o0", big + "y", out _), "Editing an accepted entry is still allowed.");
        }

        [TestMethod]
        public void Gate_LetsEachProblemThroughOnce()
        {
            var gate = new OncePerKeyGate(2);
            Assert.IsTrue(gate.ShouldLog("a"));
            Assert.IsFalse(gate.ShouldLog("a"));
            Assert.IsTrue(gate.ShouldLog("b"));
            Assert.IsFalse(gate.ShouldLog("c"), "Capacity reached: stay quiet rather than grow without bound.");
            gate.Reset();
            Assert.IsTrue(gate.ShouldLog("a"));
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);
    }
}
