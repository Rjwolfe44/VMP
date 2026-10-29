using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.PersistentSync;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Client;
using Server.System;
using Server.System.PersistentSync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ServerPersistentSyncTest
{
    /// <summary>
    /// Regression tests for the 2026-10-23 stall: the client "Vlad" stopped receiving every PersistentSync snapshot
    /// at 02:51:34 local (after ~30 Science/ScienceSubjects snapshots in 3 s) for 50+ minutes while the connection and
    /// all other message types kept working; later KSC re-stages of the stale facility snapshot reverted the player's
    /// own LaunchPad upgrade. Covers the client-side detector (shared LmpCommon logic), the facility re-stage merge,
    /// the request wire format and the server-side channel reset.
    /// </summary>
    [TestClass]
    public class PersistentSyncSnapshotStallRecoveryTests
    {
        private static readonly ServerMessageFactory ServerFactory = new ServerMessageFactory();
        private static readonly DateTime LastSnapshot = new DateTime(2026, 10, 23, 6, 51, 34, 361, DateTimeKind.Utc);

        [TestInitialize]
        public void Setup()
        {
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
        }

        #region Client liveness watchdog

        [TestMethod]
        public void Watchdog_QuietStream_IsProbedAndHealthyReplyKeepsItHealthy()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot);

            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(LastSnapshot.AddSeconds(59)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendProbe, watchdog.Evaluate(LastSnapshot.AddSeconds(60)));
            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(LastSnapshot.AddSeconds(61)), "probe outstanding, still within the reply timeout");

            Assert.IsNull(watchdog.OnSnapshotReceived(LastSnapshot.AddSeconds(61)), "a healthy reply is not a recovery");
            Assert.IsFalse(watchdog.IsStallSuspected);
            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(LastSnapshot.AddSeconds(100)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendProbe, watchdog.Evaluate(LastSnapshot.AddSeconds(121)));
        }

        [TestMethod]
        public void Watchdog_UnansweredRequest_AsksForChannelResetWithBackoff_AndSnapshotEndsStall()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot);
            var requestAt = LastSnapshot.AddSeconds(5);
            watchdog.OnRequestSent(requestAt, "Request");

            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(requestAt.AddSeconds(19)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendRecovery, watchdog.Evaluate(requestAt.AddSeconds(20)));
            Assert.IsTrue(watchdog.IsStallSuspected);
            Assert.AreEqual(1, watchdog.RecoveryAttempts);

            // Recovery unanswered too: next attempt only after the 30 s backoff, then 60 s.
            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(requestAt.AddSeconds(45)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendRecovery, watchdog.Evaluate(requestAt.AddSeconds(50)));
            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(requestAt.AddSeconds(105)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendRecovery, watchdog.Evaluate(requestAt.AddSeconds(110)));
            Assert.AreEqual(3, watchdog.RecoveryAttempts);

            var stalledFor = watchdog.OnSnapshotReceived(requestAt.AddSeconds(111));
            Assert.IsTrue(stalledFor.HasValue);
            Assert.AreEqual(111d, stalledFor.Value.TotalSeconds, 0.001);
            Assert.IsFalse(watchdog.IsStallSuspected);
            Assert.AreEqual(0, watchdog.RecoveryAttempts);
        }

        [TestMethod]
        public void Watchdog_RecoveryBackoffIsCapped()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot);
            watchdog.OnRequestSent(LastSnapshot, "Request");

            var now = LastSnapshot.AddSeconds(20);
            var recoveryTimes = new List<DateTime>();
            while (now < LastSnapshot.AddHours(1))
            {
                if (watchdog.Evaluate(now) == PersistentSyncLivenessAction.SendRecovery)
                {
                    recoveryTimes.Add(now);
                }

                now = now.AddSeconds(1);
            }

            var gaps = recoveryTimes.Zip(recoveryTimes.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
            CollectionAssert.AreEqual(new[] { 30d, 60d, 120d, 240d }, gaps.Take(4).ToArray());
            Assert.IsTrue(gaps.Skip(4).All(g => Math.Abs(g - 300d) < 0.001), "backoff caps at 5 minutes");
        }

        [TestMethod]
        public void Watchdog_IntentWithoutEcho_ProbesInsteadOfDeclaringStall()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot);
            var intentAt = LastSnapshot.AddSeconds(10);
            watchdog.OnIntentSent(intentAt);

            Assert.AreEqual(PersistentSyncLivenessAction.None, watchdog.Evaluate(intentAt.AddSeconds(14)));
            Assert.AreEqual(PersistentSyncLivenessAction.SendProbe, watchdog.Evaluate(intentAt.AddSeconds(15)));
            Assert.IsFalse(watchdog.IsStallSuspected, "a no-op intent legitimately gets no echo");

            watchdog.OnSnapshotReceived(intentAt.AddSeconds(16));
            Assert.IsFalse(watchdog.IsStallSuspected);
        }

        [TestMethod]
        public void Watchdog_NewRequestsDoNotPostponeTheOldestUnansweredOne()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot);
            watchdog.OnRequestSent(LastSnapshot, "Request");
            watchdog.OnRequestSent(LastSnapshot.AddSeconds(10), "Request");
            watchdog.OnRequestSent(LastSnapshot.AddSeconds(19), "Request");

            Assert.AreEqual(PersistentSyncLivenessAction.SendRecovery, watchdog.Evaluate(LastSnapshot.AddSeconds(20)));
        }

        /// <summary>
        /// Replays the client side of the incident: last snapshot 02:51:34, the player researches a tech at 02:53:06
        /// and upgrades the LaunchPad at 03:21:04, nothing arrives afterwards. Before the fix nothing ever noticed;
        /// now the client asks for a channel reset within about 80 s of the last snapshot.
        /// </summary>
        [TestMethod]
        public void Watchdog_IncidentTimeline_DetectsTheStallWithinTwoMinutes()
        {
            var watchdog = new PersistentSyncLivenessWatchdog();
            watchdog.Reset(LastSnapshot.AddMinutes(-48));
            watchdog.OnSnapshotReceived(LastSnapshot);

            DateTime? firstRecovery = null;
            for (var now = LastSnapshot; now < LastSnapshot.AddMinutes(55) && !firstRecovery.HasValue; now = now.AddSeconds(1))
            {
                if (now == LastSnapshot.AddSeconds(92))
                {
                    watchdog.OnIntentSent(now); // 02:53:06 Technology intent
                }

                var action = watchdog.Evaluate(now);
                if (action == PersistentSyncLivenessAction.SendRecovery)
                {
                    firstRecovery = now;
                }
            }

            Assert.IsTrue(firstRecovery.HasValue);
            Assert.IsTrue(firstRecovery.Value - LastSnapshot <= TimeSpan.FromSeconds(80),
                $"stall detected after {(firstRecovery.Value - LastSnapshot).TotalSeconds}s");
        }

        #endregion

        #region Facility re-stage must not regress a local upgrade

        private static Dictionary<string, int> Revision5Levels() => new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SpaceCenter/Administration"] = 0,
            ["SpaceCenter/AstronautComplex"] = 1,
            ["SpaceCenter/LaunchPad"] = 1,
            ["SpaceCenter/MissionControl"] = 1,
            ["SpaceCenter/VehicleAssemblyBuilding"] = 1
        };

        [TestMethod]
        public void FacilityReStage_IncidentReplay_KeepsThePlayersLaunchPadUpgrade()
        {
            var tracker = new UpgradeableFacilityLocalUpgradeTracker();
            var revision5 = Revision5Levels();
            var received = LastSnapshot.AddMinutes(-2); // 02:49:30 revision 5 applied
            Assert.AreEqual(0, tracker.OnServerSnapshot(revision5, 5, received).Count);

            // 03:21:04: player upgrades the LaunchPad; the server's revision 6 and its reply never arrive.
            tracker.NoteLocalUpgrade("SpaceCenter/LaunchPad", 2, knownRevisionAtSend: 5, nowUtc: LastSnapshot.AddMinutes(29.5));

            // 03:42:50 and 03:44:35: KSC GUI ready re-stages the cached revision 5.
            foreach (var reStageAt in new[] { 51.27, 53.0 })
            {
                var kept = new List<string>();
                var staged = tracker.MergeOver(revision5, kept);
                Assert.AreEqual(2, staged["SpaceCenter/LaunchPad"], $"re-stage at +{reStageAt} min must not revert the upgrade");
                Assert.AreEqual(1, staged["SpaceCenter/VehicleAssemblyBuilding"]);
                Assert.AreEqual(1, kept.Count);
            }

            Assert.AreEqual(1, revision5["SpaceCenter/LaunchPad"], "the cached server map itself is not modified");
        }

        [TestMethod]
        public void FacilityReStage_LocalUpgradeIsConfirmedByServerSnapshot()
        {
            var tracker = new UpgradeableFacilityLocalUpgradeTracker();
            var sentAt = LastSnapshot;
            tracker.NoteLocalUpgrade("SpaceCenter/LaunchPad", 2, 5, sentAt);

            var revision6 = Revision5Levels();
            revision6["SpaceCenter/LaunchPad"] = 2;
            var resolved = tracker.OnServerSnapshot(revision6, 6, sentAt.AddSeconds(1));

            Assert.AreEqual(1, resolved.Count);
            StringAssert.Contains(resolved[0], "confirmed");
            Assert.AreEqual(0, tracker.PendingCount);
            CollectionAssert.AreEquivalent(revision6, tracker.MergeOver(revision6, null));
        }

        [TestMethod]
        public void FacilityReStage_UnrelatedNewerSnapshotDoesNotRevertUntilGraceExpires()
        {
            var tracker = new UpgradeableFacilityLocalUpgradeTracker();
            var sentAt = LastSnapshot;
            tracker.NoteLocalUpgrade("SpaceCenter/LaunchPad", 2, 5, sentAt);

            // Another player's change, broadcast before the server processed ours.
            var revision6 = Revision5Levels();
            revision6["SpaceCenter/MissionControl"] = 2;
            Assert.AreEqual(0, tracker.OnServerSnapshot(revision6, 6, sentAt.AddSeconds(1)).Count);
            Assert.AreEqual(2, tracker.MergeOver(revision6, null)["SpaceCenter/LaunchPad"]);

            // A newer revision still without our level, well after we sent it: the server did not take it.
            var resolved = tracker.OnServerSnapshot(revision6, 7, sentAt.AddSeconds(61));
            Assert.AreEqual(1, resolved.Count);
            StringAssert.Contains(resolved[0], "superseded");
            Assert.AreEqual(1, tracker.MergeOver(revision6, null)["SpaceCenter/LaunchPad"]);
        }

        [TestMethod]
        public void FacilityReStage_SameRevisionEchoNeverSupersedesTheLocalUpgrade()
        {
            var tracker = new UpgradeableFacilityLocalUpgradeTracker();
            tracker.NoteLocalUpgrade("SpaceCenter/LaunchPad", 2, 5, LastSnapshot);

            // Re-sends of the revision we already had (probe echoes, recovery resends of a stale server) prove nothing.
            tracker.OnServerSnapshot(Revision5Levels(), 5, LastSnapshot.AddMinutes(30));
            Assert.AreEqual(1, tracker.PendingCount);
            Assert.AreEqual(2, tracker.MergeOver(Revision5Levels(), null)["SpaceCenter/LaunchPad"]);
        }

        [TestMethod]
        public void FacilityReStage_NeverLowersAndMatchesIdsLeniently()
        {
            var tracker = new UpgradeableFacilityLocalUpgradeTracker();
            tracker.NoteLocalUpgrade("LaunchPad", 1, 5, LastSnapshot);
            tracker.NoteLocalUpgrade("SpaceCenter\\Runway", 1, 5, LastSnapshot);

            var server = Revision5Levels();
            server["SpaceCenter/LaunchPad"] = 2;
            var merged = tracker.MergeOver(server, null);

            Assert.AreEqual(2, merged["SpaceCenter/LaunchPad"], "server level above the local one wins");
            Assert.AreEqual(1, merged["SpaceCenter\\Runway"], "facility missing from the server map is added under the local id");
            Assert.AreEqual("launchpad", UpgradeableFacilityLocalUpgradeTracker.NormalizeFacilityKey("SpaceCenter\\LaunchPad"));
            Assert.AreEqual("launchpad", UpgradeableFacilityLocalUpgradeTracker.NormalizeFacilityKey(" spacecenter/LaunchPad "));
        }

        #endregion

        #region Request wire format

        [TestMethod]
        public void RequestMessage_RoundTripsResetSnapshotChannel()
        {
            foreach (var reset in new[] { true, false })
            {
                var data = new PersistentSyncRequestMsgData
                {
                    DomainCount = 2,
                    Domains = new[] { PersistentSyncDomainId.Funds, PersistentSyncDomainId.UpgradeableFacilities },
                    ResetSnapshotChannel = reset
                };

                var read = RoundTrip(data);
                Assert.AreEqual(2, read.DomainCount);
                Assert.AreEqual(PersistentSyncDomainId.Funds, read.Domains[0]);
                Assert.AreEqual(PersistentSyncDomainId.UpgradeableFacilities, read.Domains[1]);
                Assert.AreEqual(reset, read.ResetSnapshotChannel);
            }
        }

        [TestMethod]
        public void RequestMessage_FromOlderClientWithoutTrailingByte_ReadsAsNoReset()
        {
            var data = new PersistentSyncRequestMsgData
            {
                DomainCount = 1,
                Domains = new[] { PersistentSyncDomainId.Science },
                ResetSnapshotChannel = true
            };

            var outgoing = CreateOutgoing();
            data.Serialize(outgoing);
            // Older clients wrote exactly the same bytes minus the trailing flags byte and supported-encodings byte.
            var olderClientBytes = outgoing.LengthBytes - 2;
            Assert.AreEqual(1, outgoing.Data[olderClientBytes], "the flags byte is second to last, so older servers never read it");
            Assert.AreEqual(0, outgoing.Data[olderClientBytes + 1], "supported encodings is the last byte");

            // A reused pooled instance still holding values from an earlier message.
            var pooled = new PersistentSyncRequestMsgData { ResetSnapshotChannel = true, ForceFullSnapshot = true, SupportedEncodings = 7 };
            pooled.Deserialize(CreateIncoming(outgoing.Data, olderClientBytes));
            Assert.AreEqual(1, pooled.DomainCount);
            Assert.AreEqual(PersistentSyncDomainId.Science, pooled.Domains[0]);
            Assert.IsFalse(pooled.ResetSnapshotChannel);
            Assert.IsFalse(pooled.ForceFullSnapshot);
            Assert.AreEqual(0, pooled.SupportedEncodings, "a client that never reports encodings is sent raw payloads");
        }

        [TestMethod]
        public void RequestMessage_FromClientWithFlagsButNoEncodings_ReadsAsRawOnly()
        {
            var data = new PersistentSyncRequestMsgData { DomainCount = 1, Domains = new[] { PersistentSyncDomainId.Science }, ResetSnapshotChannel = true };
            var outgoing = CreateOutgoing();
            data.Serialize(outgoing);

            var pooled = new PersistentSyncRequestMsgData { SupportedEncodings = 7 };
            pooled.Deserialize(CreateIncoming(outgoing.Data, outgoing.LengthBytes - 1));
            Assert.IsTrue(pooled.ResetSnapshotChannel);
            Assert.AreEqual(0, pooled.SupportedEncodings);
        }

        [TestMethod]
        public void RequestMessage_RoundTripsFullSnapshotFlagAndSupportedEncodings()
        {
            foreach (var force in new[] { true, false })
            foreach (var reset in new[] { true, false })
            {
                var data = new PersistentSyncRequestMsgData
                {
                    DomainCount = 1,
                    Domains = new[] { PersistentSyncDomainId.Contracts },
                    ResetSnapshotChannel = reset,
                    ForceFullSnapshot = force,
                    SupportedEncodings = (byte)(SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd | SnapshotEncodings.CapZstdDictionary)
                };

                var read = RoundTrip(data);
                Assert.AreEqual(reset, read.ResetSnapshotChannel);
                Assert.AreEqual(force, read.ForceFullSnapshot);
                Assert.AreEqual(7, read.SupportedEncodings);
            }
        }

        private static PersistentSyncRequestMsgData RoundTrip(PersistentSyncRequestMsgData data)
        {
            var outgoing = CreateOutgoing();
            data.Serialize(outgoing);
            Assert.AreEqual(data.GetMessageSize(), outgoing.LengthBytes, "GetMessageSize must cover the trailing flag");
            var read = new PersistentSyncRequestMsgData();
            read.Deserialize(CreateIncoming(outgoing.Data, outgoing.LengthBytes));
            return read;
        }

        private static NetOutgoingMessage CreateOutgoing()
        {
            return (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), nonPublic: true);
        }

        private static NetIncomingMessage CreateIncoming(byte[] buffer, int lengthBytes)
        {
            var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), nonPublic: true);
            incoming.Data = buffer.Take(lengthBytes).ToArray();
            incoming.LengthBytes = lengthBytes;
            return incoming;
        }

        #endregion

        #region Server channel reset and drift diagnostics

        [TestMethod]
        public void ChannelRouter_SnapshotsUseChannel23UntilReset_OtherMessagesUnaffected()
        {
            var client = CreateClient("Vlad");
            var other = CreateClient("Friend");
            var snapshot = CreateSnapshotMessage(PersistentSyncDomainId.Science, 305);
            var warp = ServerFactory.CreateNew<WarpSrvMsg>(null);

            Assert.AreEqual(23, PersistentSyncSnapshotChannelRouter.ResolveChannel(client, snapshot));
            Assert.AreEqual(warp.Channel, PersistentSyncSnapshotChannelRouter.ResolveChannel(client, warp));

            Assert.AreEqual(24, PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test"));
            Assert.AreEqual(24, PersistentSyncSnapshotChannelRouter.ResolveChannel(client, snapshot));
            Assert.AreEqual(warp.Channel, PersistentSyncSnapshotChannelRouter.ResolveChannel(client, warp));
            Assert.AreEqual(23, PersistentSyncSnapshotChannelRouter.ResolveChannel(other, snapshot), "other clients keep their channel");
        }

        [TestMethod]
        public void ChannelRouter_UsesOnlySpareChannelsAndStopsWhenExhausted()
        {
            var client = CreateClient("Vlad");
            var usedByOtherServerMessages = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23 };
            var seen = new List<int>();
            for (var i = 0; i < PersistentSyncSnapshotChannelRouter.RecoveryChannels.Length + 3; i++)
            {
                seen.Add(PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test", LastSnapshot.AddMinutes(i)));
            }

            Assert.IsTrue(seen.All(c => c >= 0 && c < 32 && !usedByOtherServerMessages.Contains(c)));
            var distinct = seen.Distinct().ToArray();
            Assert.AreEqual(PersistentSyncSnapshotChannelRouter.RecoveryChannels.Length, distinct.Length);
            Assert.AreEqual(seen.Last(), PersistentSyncSnapshotChannelRouter.GetSnapshotChannel(client));
        }

        [TestMethod]
        public void ChannelRouter_ResetRequestsInQuickSuccessionUseOneChannel()
        {
            var client = CreateClient("Vlad");
            Assert.AreEqual(24, PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test", LastSnapshot));
            Assert.AreEqual(24, PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test", LastSnapshot.AddSeconds(5)));
            Assert.AreEqual(25, PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test", LastSnapshot.AddSeconds(31)));
        }

        [TestMethod]
        public void HandleRequest_WithResetFlag_MovesClientAndQueuesRepliesForTheNewChannel()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);

            PersistentSyncRegistry.HandleRequest(client, new PersistentSyncRequestMsgData
            {
                DomainCount = 2,
                Domains = new[] { PersistentSyncDomainId.Funds, PersistentSyncDomainId.UpgradeableFacilities },
                ResetSnapshotChannel = true
            });

            var queued = client.SendMessageQueue.ToArray();
            Assert.AreEqual(2, queued.Length);
            Assert.IsTrue(queued.All(m => m is PersistentSyncSrvMsg));
            Assert.IsTrue(queued.All(m => PersistentSyncSnapshotChannelRouter.ResolveChannel(client, m) == 24));
        }

        [TestMethod]
        public void HandleRequest_WithoutResetFlag_KeepsChannel()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);

            PersistentSyncRegistry.HandleRequest(client, new PersistentSyncRequestMsgData
            {
                DomainCount = 1,
                Domains = new[] { PersistentSyncDomainId.Funds },
                ResetSnapshotChannel = false
            });

            Assert.AreEqual(1, client.SendMessageQueue.Count);
            Assert.AreEqual(23, PersistentSyncSnapshotChannelRouter.GetSnapshotChannel(client));
        }

        /// <summary>
        /// Server side of the incident: revision 6 of UpgradeableFacilities was handed to Vlad at 07:19:12 UTC and his
        /// intent at 07:21:02 still reported revision 5. That is now logged as drift.
        /// </summary>
        [TestMethod]
        public void DriftCheck_IncidentReplay_FlagsClientThatNeverAppliedASentRevision()
        {
            var client = CreateClient("Vlad");
            var sentAt = new DateTime(2026, 10, 23, 7, 19, 12, DateTimeKind.Utc);
            PersistentSyncSnapshotChannelRouter.NoteSnapshotSent(client, CreateSnapshotMessage(PersistentSyncDomainId.UpgradeableFacilities, 6), 23, sentAt);

            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.CheckIntentRevisionDrift(client, PersistentSyncDomainId.UpgradeableFacilities, 5, sentAt.AddSeconds(5)),
                "a snapshot still in flight is not drift");
            Assert.IsTrue(PersistentSyncSnapshotChannelRouter.CheckIntentRevisionDrift(client, PersistentSyncDomainId.UpgradeableFacilities, 5, new DateTime(2026, 10, 23, 7, 21, 2, DateTimeKind.Utc)));
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.CheckIntentRevisionDrift(client, PersistentSyncDomainId.UpgradeableFacilities, 6, sentAt.AddMinutes(5)));
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.CheckIntentRevisionDrift(client, PersistentSyncDomainId.Funds, 0, sentAt.AddMinutes(5)),
                "nothing sent for that domain");
        }

        [TestMethod]
        public void DriftCheck_KeepsTheNewestSentRevision()
        {
            var client = CreateClient("Vlad");
            var t = LastSnapshot;
            PersistentSyncSnapshotChannelRouter.NoteSnapshotSent(client, CreateSnapshotMessage(PersistentSyncDomainId.Science, 305), 23, t);
            PersistentSyncSnapshotChannelRouter.NoteSnapshotSent(client, CreateSnapshotMessage(PersistentSyncDomainId.Science, 304), 23, t.AddSeconds(1));

            Assert.IsTrue(PersistentSyncSnapshotChannelRouter.CheckIntentRevisionDrift(client, PersistentSyncDomainId.Science, 304, t.AddMinutes(2)));
        }

        private static IServerMessageBase CreateSnapshotMessage(PersistentSyncDomainId domainId, long revision)
        {
            var data = new PersistentSyncSnapshotMsgData { DomainId = domainId, Revision = revision, NumBytes = 0, Payload = new byte[0] };
            return ServerFactory.CreateNew<PersistentSyncSrvMsg>(data);
        }

        private static ClientStructure CreateClient(string playerName, bool withSendQueue = false)
        {
            var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
            client.PlayerName = playerName;
            if (withSendQueue)
            {
                typeof(ClientStructure)
                    .GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(client, new ConcurrentQueue<IServerMessageBase>());
            }

            return client;
        }

        #endregion
    }
}
