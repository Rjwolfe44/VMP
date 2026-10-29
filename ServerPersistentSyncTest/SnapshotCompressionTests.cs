using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Client;
using Server.Settings.Definition;
using Server.Settings.Structures;
using Server.System;
using Server.System.PersistentSync;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ServerPersistentSyncTest
{
    /// <summary>
    /// Snapshot compression. The server re-sends a domain's whole snapshot to a client after every small change (the
    /// Contracts snapshot is 131 KB raw; one player received 534 copies in a day). Payloads now travel compressed with
    /// Zstandard, relative to the snapshot the client already holds when possible, with QuickLZ as the fallback for a
    /// client that cannot read Zstd. These tests cover the codec, the per-client bookkeeping on the server, the client
    /// decoder, the wire format and the courtesy-reply suppression.
    /// </summary>
    [TestClass]
    public class SnapshotCompressionTests
    {
        private static readonly ServerMessageFactory ServerFactory = new ServerMessageFactory();
        private const byte AllEncodings = SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd | SnapshotEncodings.CapZstdDictionary;

        [TestInitialize]
        public void Setup()
        {
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
            GeneralSettings.SettingsStore.SnapshotCompression = SnapshotCompressionMode.Zstd;
        }

        #region Codec

        [TestMethod]
        public void ZstdIsAvailableForBothDirectionsOnTheServerRuntime()
        {
            Assert.IsTrue(ZstdBackend.CanDecompress, ZstdBackend.Error);
            Assert.IsTrue(ZstdBackend.CanCompress, ZstdBackend.Error);
            Assert.AreEqual(AllEncodings, SnapshotCodec.LocalDecodeCapabilities);
        }

        [TestMethod]
        public void ZstdPlainShrinksTextAndRoundTrips()
        {
            var raw = ContractLikePayload(300, 1);

            var encoded = SnapshotCodec.Encode(raw, raw.Length, (byte)(SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd), null);

            Assert.AreEqual(SnapshotEncodings.Zstd, encoded.Encoding);
            Assert.IsTrue(encoded.Length < raw.Length / 3, $"{raw.Length} -> {encoded.Length}");
            AssertDecodes(encoded, null, raw);
        }

        [TestMethod]
        public void PreviousSnapshotAsDictionaryMakesAResendTiny()
        {
            var first = ContractLikePayload(300, 1);
            var second = Edit(first, 12);
            var receiverBase = SnapshotBase.Create(7, first, first.Length);

            var encoded = SnapshotCodec.Encode(second, second.Length, AllEncodings, receiverBase);

            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, encoded.Encoding);
            Assert.AreEqual(7, encoded.BaseRevision);
            Assert.AreEqual(receiverBase.Checksum, encoded.BaseChecksum);
            Assert.IsTrue(encoded.Length < second.Length / 50, $"{second.Length} -> {encoded.Length}");
            AssertDecodes(encoded, receiverBase, second);
        }

        [TestMethod]
        public void IdenticalResendCostsAlmostNothing()
        {
            var raw = ContractLikePayload(300, 1);
            var receiverBase = SnapshotBase.Create(7, raw, raw.Length);

            var encoded = SnapshotCodec.Encode(raw, raw.Length, AllEncodings, receiverBase);

            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, encoded.Encoding);
            Assert.IsTrue(encoded.Length < 200, encoded.Length.ToString());
            AssertDecodes(encoded, receiverBase, raw);
        }

        [TestMethod]
        public void ReceiverThatCannotReadDictionariesGetsPlainZstd()
        {
            var first = ContractLikePayload(300, 1);
            var second = Edit(first, 12);

            var encoded = SnapshotCodec.Encode(second, second.Length, (byte)(SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd),
                SnapshotBase.Create(7, first, first.Length));

            Assert.AreEqual(SnapshotEncodings.Zstd, encoded.Encoding);
            AssertDecodes(encoded, null, second);
        }

        [TestMethod]
        public void ReceiverWithoutZstdGetsQuickLz()
        {
            var raw = ContractLikePayload(300, 1);

            var encoded = SnapshotCodec.Encode(raw, raw.Length, SnapshotEncodings.CapQuickLz, SnapshotBase.Create(7, raw, raw.Length));

            Assert.AreEqual(SnapshotEncodings.QuickLz, encoded.Encoding);
            Assert.IsTrue(encoded.Length < raw.Length / 2);
            AssertDecodes(encoded, null, raw);
        }

        [TestMethod]
        public void SmallIncompressibleOrUnknownReceiversGetRaw()
        {
            var small = Encoding.UTF8.GetBytes(new string('a', SnapshotCodec.MinCompressBytes - 1));
            Assert.AreEqual(SnapshotEncodings.Raw, SnapshotCodec.Encode(small, small.Length, AllEncodings, null).Encoding);

            var text = ContractLikePayload(300, 1);
            Assert.AreEqual(SnapshotEncodings.Raw, SnapshotCodec.Encode(text, text.Length, 0, null).Encoding, "no capabilities reported yet");

            var noise = new byte[8192];
            new Random(5).NextBytes(noise);
            var encoded = SnapshotCodec.Encode(noise, noise.Length, AllEncodings, null);
            Assert.AreEqual(SnapshotEncodings.Raw, encoded.Encoding, "random bytes do not shrink, so they are not wrapped");
            Assert.AreEqual(noise.Length, encoded.Length);
        }

        [TestMethod]
        public void DecodeRefusesAMissingOrDifferentBase()
        {
            var first = ContractLikePayload(300, 1);
            var second = Edit(first, 12);
            var receiverBase = SnapshotBase.Create(7, first, first.Length);
            var encoded = SnapshotCodec.Encode(second, second.Length, AllEncodings, receiverBase);
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, encoded.Encoding);

            Assert.IsFalse(TryDecode(encoded, null, out _, out var noBaseError));
            StringAssert.Contains(noBaseError, "no base");

            var otherRevision = SnapshotBase.Create(8, first, first.Length);
            Assert.IsFalse(TryDecode(encoded, otherRevision, out _, out var revisionError));
            StringAssert.Contains(revisionError, "mismatch");

            var otherContent = SnapshotBase.Create(7, second, second.Length);
            Assert.IsFalse(TryDecode(encoded, otherContent, out _, out var checksumError));
            StringAssert.Contains(checksumError, "mismatch");
        }

        [TestMethod]
        public void DecodeNeverThrowsOnDamagedData()
        {
            var raw = ContractLikePayload(300, 1);
            var receiverBase = SnapshotBase.Create(7, raw, raw.Length);

            foreach (var caps in new[] { SnapshotEncodings.CapQuickLz, (byte)(SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd), AllEncodings })
            {
                var encoded = SnapshotCodec.Encode(Edit(raw, 3), raw.Length, caps, receiverBase);
                Assert.AreNotEqual(SnapshotEncodings.Raw, encoded.Encoding);

                var damaged = encoded.Data.Take(encoded.Length).ToArray();
                for (var i = 0; i < damaged.Length; i += 7) damaged[i] ^= 0xFF;
                Assert.IsFalse(SnapshotCodec.TryDecode(encoded.Encoding, damaged, damaged.Length, encoded.RawLength, encoded.BaseRevision, encoded.BaseChecksum, receiverBase, out _, out var error), "damaged " + encoded.Encoding);
                Assert.IsFalse(string.IsNullOrEmpty(error));

                var truncated = encoded.Data.Take(Math.Max(1, encoded.Length / 2)).ToArray();
                Assert.IsFalse(SnapshotCodec.TryDecode(encoded.Encoding, truncated, truncated.Length, encoded.RawLength, encoded.BaseRevision, encoded.BaseChecksum, receiverBase, out _, out _), "truncated " + encoded.Encoding);
            }

            Assert.IsFalse(SnapshotCodec.TryDecode(99, new byte[10], 10, 10, 0, 0, null, out _, out var unknown));
            StringAssert.Contains(unknown, "unknown encoding");
            Assert.IsFalse(SnapshotCodec.TryDecode(SnapshotEncodings.Zstd, new byte[10], 10, int.MaxValue, 0, 0, null, out _, out var tooBig));
            StringAssert.Contains(tooBig, "implausible");
        }

        [TestMethod]
        public void ChecksumDiffersWhenOneByteChanges()
        {
            var a = ContractLikePayload(50, 1);
            var b = (byte[])a.Clone();
            b[b.Length / 2] ^= 1;
            Assert.AreNotEqual(SnapshotCodec.Checksum(a, a.Length), SnapshotCodec.Checksum(b, b.Length));
            Assert.AreEqual(SnapshotCodec.Checksum(a, a.Length), SnapshotCodec.Checksum((byte[])a.Clone(), a.Length));
        }

        #endregion

        #region Client decoder

        [TestMethod]
        public void DecoderFollowsAChainOfSnapshotsAndKeepsOnlyTheLatestAsBase()
        {
            var decoder = new ClientSnapshotDecoder();
            var payloads = new[] { ContractLikePayload(300, 1), null, null };
            payloads[1] = Edit(payloads[0], 10);
            payloads[2] = Edit(payloads[1], 10);

            // What the server does for one client: capabilities known, base = the last snapshot it sent.
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);

            var encodings = new byte[3];
            for (var i = 0; i < payloads.Length; i++)
            {
                var message = PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, i + 1, payloads[i]));
                var wire = (PersistentSyncSnapshotMsgData)message.Data;
                encodings[i] = wire.PayloadEncoding;

                var received = CloneAsReceived(wire);
                Assert.IsTrue(decoder.TryDecodeInPlace(received, out var error), error);
                Assert.AreEqual(SnapshotEncodings.Raw, received.PayloadEncoding);
                Assert.AreEqual(payloads[i].Length, received.NumBytes);
                CollectionAssert.AreEqual(payloads[i], received.Payload.Take(received.NumBytes).ToArray());
                Assert.AreEqual(i + 1, decoder.GetBase(PersistentSyncDomainId.Contracts).Revision);
            }

            Assert.AreEqual(SnapshotEncodings.Zstd, encodings[0], "first send: nothing to be relative to");
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, encodings[1]);
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, encodings[2]);
        }

        [TestMethod]
        public void DecoderRefusesADeltaItDoesNotHaveTheBaseFor()
        {
            var first = ContractLikePayload(300, 1);
            var second = Edit(first, 10);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);
            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, first));
            var delta = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, second)).Data;
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, delta.PayloadEncoding);

            // A client that never saw revision 1 (system was off, or it reconnected into the same server session).
            var decoder = new ClientSnapshotDecoder();
            var received = CloneAsReceived(delta);
            Assert.IsFalse(decoder.TryDecodeInPlace(received, out var error));
            StringAssert.Contains(error, "no base");
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, received.PayloadEncoding, "left as received so nothing half-decoded is applied");
            Assert.IsNull(decoder.GetBase(PersistentSyncDomainId.Contracts));
        }

        [TestMethod]
        public void DecoderDropsItsBaseAfterAFailureSoTheNextDeltaIsRefusedToo()
        {
            var first = ContractLikePayload(300, 1);
            var second = Edit(first, 10);
            var decoder = new ClientSnapshotDecoder();
            var raw = RawAsReceived(PersistentSyncDomainId.Contracts, 1, first);
            Assert.IsTrue(decoder.TryDecodeInPlace(raw, out _));
            Assert.IsNotNull(decoder.GetBase(PersistentSyncDomainId.Contracts));

            var wrongBase = SnapshotCodec.Encode(second, second.Length, AllEncodings, SnapshotBase.Create(5, first, first.Length));
            var bad = ReceivedFrom(PersistentSyncDomainId.Contracts, 6, wrongBase);
            Assert.IsFalse(decoder.TryDecodeInPlace(bad, out _));
            Assert.IsNull(decoder.GetBase(PersistentSyncDomainId.Contracts));
        }

        [TestMethod]
        public void DecoderForgetsTheBaseWhenAPayloadBecomesSmall()
        {
            var decoder = new ClientSnapshotDecoder();
            Assert.IsTrue(decoder.TryDecodeInPlace(RawAsReceived(PersistentSyncDomainId.Science, 1, ContractLikePayload(300, 1)), out _));
            Assert.IsNotNull(decoder.GetBase(PersistentSyncDomainId.Science));

            Assert.IsTrue(decoder.TryDecodeInPlace(RawAsReceived(PersistentSyncDomainId.Science, 2, new byte[16]), out _));
            Assert.IsNull(decoder.GetBase(PersistentSyncDomainId.Science), "the server drops its copy for small payloads too");

            Assert.IsTrue(decoder.TryDecodeInPlace(RawAsReceived(PersistentSyncDomainId.Science, 3, ContractLikePayload(300, 1)), out _));
            decoder.Reset();
            Assert.IsNull(decoder.GetBase(PersistentSyncDomainId.Science));
        }

        [TestMethod]
        public void DecoderKeepsADomainsBaseApartFromOtherDomains()
        {
            var contracts = ContractLikePayload(300, 1);
            var science = ContractLikePayload(200, 2);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);
            var decoder = new ClientSnapshotDecoder();

            foreach (var (domain, payload) in new[] { (PersistentSyncDomainId.Contracts, contracts), (PersistentSyncDomainId.ScienceSubjects, science) })
            {
                var wire = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(domain, 1, payload)).Data;
                Assert.IsTrue(decoder.TryDecodeInPlace(CloneAsReceived(wire), out var error), error);
            }

            var changedContracts = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, Edit(contracts, 5))).Data;
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, changedContracts.PayloadEncoding);
            Assert.IsTrue(decoder.TryDecodeInPlace(CloneAsReceived(changedContracts), out var contractsError), contractsError);
            Assert.AreEqual(1, decoder.GetBase(PersistentSyncDomainId.ScienceSubjects).Revision, "the other domain's base is untouched");
        }

        #endregion

        #region Server encoder

        [TestMethod]
        public void UnknownClientGetsTheOriginalMessageUntouched()
        {
            var client = CreateClient("Vlad");
            var message = RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, ContractLikePayload(300, 1));

            var prepared = PersistentSyncSnapshotEncoder.PrepareForClient(client, message);

            Assert.AreSame(message, prepared, "no capabilities reported yet, so raw and the same object");
            Assert.AreEqual(SnapshotEncodings.Raw, ((PersistentSyncSnapshotMsgData)prepared.Data).PayloadEncoding);
        }

        [TestMethod]
        public void SnapshotsSentBeforeTheFirstRequestAreNotRememberedAsBases()
        {
            // A broadcast reaches a client that is still joining: its PersistentSync system is not running, so it drops the message.
            var payload = ContractLikePayload(300, 1);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, payload));
            Assert.IsNull(PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, PersistentSyncDomainId.Contracts));

            // Its first request arrives with its capabilities; the reply is the first snapshot it can use, not relative to the dropped one.
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);
            var reply = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, Edit(payload, 5))).Data;
            Assert.AreEqual(SnapshotEncodings.Zstd, reply.PayloadEncoding);
            Assert.AreEqual(0, reply.BaseRevision);
        }

        [TestMethod]
        public void SharedMessageIsNeverModifiedAndClientsAreIndependent()
        {
            var payload = ContractLikePayload(300, 1);
            var vlad = CreateClient("Vlad");
            var friend = CreateClient("Friend");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(vlad, AllEncodings);
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(friend, SnapshotEncodings.CapQuickLz);
            var shared = RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, payload);

            var forVlad = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(vlad, shared).Data;
            var forFriend = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(friend, shared).Data;

            Assert.AreEqual(SnapshotEncodings.Zstd, forVlad.PayloadEncoding);
            Assert.AreEqual(SnapshotEncodings.QuickLz, forFriend.PayloadEncoding);
            var sharedData = (PersistentSyncSnapshotMsgData)shared.Data;
            Assert.AreEqual(SnapshotEncodings.Raw, sharedData.PayloadEncoding);
            Assert.AreEqual(payload.Length, sharedData.NumBytes);
            Assert.AreSame(payload, sharedData.Payload);
        }

        [TestMethod]
        public void ForceFullAndChannelResetClearTheBaseSoTheNextSendIsNotRelative()
        {
            var first = ContractLikePayload(300, 1);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);

            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, first));
            var relative = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, Edit(first, 5))).Data;
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, relative.PayloadEncoding);

            PersistentSyncSnapshotChannelRouter.ClearDeltaBases(client, new[] { PersistentSyncDomainId.Contracts });
            var afterForce = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 3, Edit(first, 6))).Data;
            Assert.AreEqual(SnapshotEncodings.Zstd, afterForce.PayloadEncoding, "after a full-snapshot request");

            var again = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 4, Edit(first, 7))).Data;
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, again.PayloadEncoding, "relative again once a full one went out");

            PersistentSyncSnapshotChannelRouter.AdvanceSnapshotChannel(client, "test", new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
            var afterReset = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 5, Edit(first, 8))).Data;
            Assert.AreEqual(SnapshotEncodings.Zstd, afterReset.PayloadEncoding, "a stalled channel may never deliver what was sent on it");
        }

        [TestMethod]
        public void SmallPayloadClearsTheServersBaseLikeTheClient()
        {
            var big = ContractLikePayload(300, 1);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);
            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, big));
            Assert.IsNotNull(PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, PersistentSyncDomainId.Contracts));

            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, new byte[8]));

            Assert.IsNull(PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, PersistentSyncDomainId.Contracts));
        }

        [TestMethod]
        public void SnapshotCompressionSettingLimitsWhatIsSent()
        {
            var payload = ContractLikePayload(300, 1);
            var client = CreateClient("Vlad");
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);

            GeneralSettings.SettingsStore.SnapshotCompression = SnapshotCompressionMode.Off;
            Assert.AreEqual(SnapshotEncodings.Raw, ((PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, payload)).Data).PayloadEncoding);

            GeneralSettings.SettingsStore.SnapshotCompression = SnapshotCompressionMode.QuickLz;
            Assert.AreEqual(SnapshotEncodings.QuickLz, ((PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 2, payload)).Data).PayloadEncoding);

            GeneralSettings.SettingsStore.SnapshotCompression = SnapshotCompressionMode.Zstd;
            Assert.AreEqual(SnapshotEncodings.ZstdDictionary, ((PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 3, payload)).Data).PayloadEncoding);
        }

        [TestMethod]
        public void HandleRequestRecordsCapabilitiesAndEveryQueuedReplyDecodesToTheDomainSnapshot()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);
            var domains = new[] { PersistentSyncDomainId.Funds, PersistentSyncDomainId.Science, PersistentSyncDomainId.Technology, PersistentSyncDomainId.Contracts };
            var decoder = new ClientSnapshotDecoder();

            PersistentSyncRegistry.HandleRequest(client, new PersistentSyncRequestMsgData { DomainCount = domains.Length, Domains = domains, SupportedEncodings = AllEncodings });

            Assert.AreEqual(AllEncodings, PersistentSyncSnapshotChannelRouter.GetSupportedEncodings(client));
            var expected = PersistentSyncRegistry.GetSnapshots(domains).ToDictionary(s => s.DomainId);
            foreach (var queued in client.SendMessageQueue.ToArray())
            {
                // The server's send thread does this for each queued message.
                var wire = (PersistentSyncSnapshotMsgData)PersistentSyncSnapshotEncoder.PrepareForClient(client, queued).Data;
                var received = CloneAsReceived(wire);
                Assert.IsTrue(decoder.TryDecodeInPlace(received, out var error), error);
                var snapshot = expected[received.DomainId];
                Assert.AreEqual(snapshot.NumBytes, received.NumBytes);
                CollectionAssert.AreEqual(snapshot.Payload.Take(snapshot.NumBytes).ToArray(), received.Payload.Take(received.NumBytes).ToArray(), received.DomainId.ToString());
            }
        }

        [TestMethod]
        public void HandleRequestWithForceFullDropsTheBasesOfTheRequestedDomainsOnly()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);
            PersistentSyncSnapshotChannelRouter.SetSupportedEncodings(client, AllEncodings);
            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.Contracts, 1, ContractLikePayload(300, 1)));
            PersistentSyncSnapshotEncoder.PrepareForClient(client, RawSnapshotMessage(PersistentSyncDomainId.ScienceSubjects, 1, ContractLikePayload(200, 2)));

            PersistentSyncRegistry.HandleRequest(client, new PersistentSyncRequestMsgData
            {
                DomainCount = 1,
                Domains = new[] { PersistentSyncDomainId.Contracts },
                ForceFullSnapshot = true,
                SupportedEncodings = AllEncodings
            });

            Assert.IsNull(PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, PersistentSyncDomainId.Contracts));
            Assert.IsNotNull(PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, PersistentSyncDomainId.ScienceSubjects));
        }

        #endregion

        #region Wire format

        [TestMethod]
        public void SnapshotMessageRoundTripsEveryEncoding()
        {
            var raw = ContractLikePayload(300, 1);
            var receiverBase = SnapshotBase.Create(7, raw, raw.Length);
            var second = Edit(raw, 10);

            foreach (var encoded in new[]
            {
                SnapshotCodec.Encode(second, second.Length, 0, null),
                SnapshotCodec.Encode(second, second.Length, SnapshotEncodings.CapQuickLz, null),
                SnapshotCodec.Encode(second, second.Length, AllEncodings, null),
                SnapshotCodec.Encode(second, second.Length, AllEncodings, receiverBase),
            })
            {
                var data = new PersistentSyncSnapshotMsgData
                {
                    DomainId = PersistentSyncDomainId.Contracts,
                    Revision = 8,
                    PayloadEncoding = encoded.Encoding,
                    RawLength = encoded.RawLength,
                    BaseRevision = encoded.BaseRevision,
                    BaseChecksum = encoded.BaseChecksum,
                    Payload = encoded.Data,
                    NumBytes = encoded.Length
                };

                var outgoing = CreateOutgoing();
                data.Serialize(outgoing);
                Assert.AreEqual(data.GetMessageSize(), outgoing.LengthBytes, "GetMessageSize must cover the encoding header (" + SnapshotEncodings.Name(encoded.Encoding) + ")");

                // A reused pooled instance still holding another message's header.
                var read = new PersistentSyncSnapshotMsgData { PayloadEncoding = SnapshotEncodings.ZstdDictionary, BaseRevision = 99, BaseChecksum = 1, RawLength = 5 };
                read.Deserialize(CreateIncoming(outgoing.Data, outgoing.LengthBytes));

                Assert.AreEqual(encoded.Encoding, read.PayloadEncoding);
                Assert.AreEqual(encoded.RawLength, read.RawLength);
                Assert.AreEqual(encoded.BaseRevision, read.BaseRevision);
                Assert.AreEqual(encoded.BaseChecksum, read.BaseChecksum);
                Assert.AreEqual(encoded.Length, read.NumBytes);

                var decoder = new ClientSnapshotDecoder();
                if (encoded.Encoding == SnapshotEncodings.ZstdDictionary)
                {
                    Assert.IsTrue(decoder.TryDecodeInPlace(RawAsReceived(PersistentSyncDomainId.Contracts, 7, raw), out _));
                }

                Assert.IsTrue(decoder.TryDecodeInPlace(read, out var error), error);
                CollectionAssert.AreEqual(second, read.Payload.Take(read.NumBytes).ToArray());
            }
        }

        [TestMethod]
        public void RawSnapshotCostsOneExtraByte()
        {
            var data = new PersistentSyncSnapshotMsgData { DomainId = PersistentSyncDomainId.Funds, Revision = 3, Payload = new byte[8], NumBytes = 8, RawLength = 8 };
            var outgoing = CreateOutgoing();
            data.Serialize(outgoing);

            var read = new PersistentSyncSnapshotMsgData { RawLength = 99 };
            read.Deserialize(CreateIncoming(outgoing.Data, outgoing.LengthBytes));

            Assert.AreEqual(SnapshotEncodings.Raw, read.PayloadEncoding);
            Assert.AreEqual(8, read.RawLength);
            Assert.AreEqual(0, read.BaseRevision);
            Assert.AreEqual(8, read.NumBytes);
        }

        #endregion

        #region Courtesy reply suppression

        [TestMethod]
        public void RepeatedNoOpIntentsFromALaggingClientGetOneReplyNotOneEach()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);
            var funds = FundsIntentPayloadSerializer.Serialize(4200d, "Setup");
            var setup = PersistentSyncRegistry.ApplyServerMutation(PersistentSyncDomainId.Funds, funds, funds.Length, "Setup");
            Assert.IsTrue(setup.Changed);
            var revision = setup.Snapshot.Revision;
            client.SendMessageQueue.Clear();

            // Same value again (a no-op) from a client that still reports the older revision: a courtesy resync.
            for (var i = 0; i < 6; i++)
            {
                PersistentSyncRegistry.HandleIntent(client, FundsIntent(4200d, knownRevision: revision - 1));
            }

            Assert.AreEqual(1, client.SendMessageQueue.Count, "one reply, the other five are suppressed");

            // Once the window has passed (or for another revision) a reply goes out again.
            PersistentSyncSnapshotChannelRouter.NoteReplyQueued(client, PersistentSyncDomainId.Funds, revision, DateTime.UtcNow - PersistentSyncSnapshotChannelRouter.DriftReplySuppressionWindow - TimeSpan.FromSeconds(1));
            PersistentSyncRegistry.HandleIntent(client, FundsIntent(4200d, knownRevision: revision - 1));
            Assert.AreEqual(2, client.SendMessageQueue.Count);
        }

        [TestMethod]
        public void ClientThatIsUpToDateGetsNoReplyToANoOp()
        {
            PersistentSyncRegistry.Initialize(true);
            var client = CreateClient("Vlad", withSendQueue: true);
            var funds = FundsIntentPayloadSerializer.Serialize(4200d, "Setup");
            var revision = PersistentSyncRegistry.ApplyServerMutation(PersistentSyncDomainId.Funds, funds, funds.Length, "Setup").Snapshot.Revision;
            client.SendMessageQueue.Clear();

            PersistentSyncRegistry.HandleIntent(client, FundsIntent(4200d, knownRevision: revision));

            Assert.AreEqual(0, client.SendMessageQueue.Count);
        }

        [TestMethod]
        public void SuppressionLooksAtTheRevisionAndTheClient()
        {
            var vlad = CreateClient("Vlad");
            var friend = CreateClient("Friend");
            var now = DateTime.UtcNow;
            PersistentSyncSnapshotChannelRouter.NoteReplyQueued(vlad, PersistentSyncDomainId.Contracts, 5, now);

            Assert.IsTrue(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(vlad, PersistentSyncDomainId.Contracts, 5, now.AddSeconds(5)));
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(vlad, PersistentSyncDomainId.Contracts, 6, now.AddSeconds(5)), "a newer revision is not covered");
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(vlad, PersistentSyncDomainId.Science, 5, now.AddSeconds(5)), "another domain");
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(friend, PersistentSyncDomainId.Contracts, 5, now.AddSeconds(5)), "another client");
            Assert.IsFalse(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(vlad, PersistentSyncDomainId.Contracts, 5, now.AddSeconds(11)), "after the window");

            PersistentSyncSnapshotChannelRouter.NoteSnapshotSent(friend, RawSnapshotMessage(PersistentSyncDomainId.Science, 9, new byte[4]), 23, now);
            Assert.IsTrue(PersistentSyncSnapshotChannelRouter.WasRevisionSentRecently(friend, PersistentSyncDomainId.Science, 9, now.AddSeconds(2)), "an actually sent snapshot counts too");
        }

        #endregion

        #region Traffic counters

        [TestMethod]
        public void TrafficSummaryListsTypesAndSnapshotSavings()
        {
            VmpTrafficStats.TakeWindowSummary();
            VmpTrafficStats.RecordSent("VesselPositionMsgData", 200);
            VmpTrafficStats.RecordSent("VesselPositionMsgData", 200);
            VmpTrafficStats.RecordSent("PersistentSyncSnapshotMsgData", 3000);
            VmpTrafficStats.RecordReceived("PersistentSyncIntentMsgData", 90);
            VmpTrafficStats.RecordSnapshot(PersistentSyncDomainId.Contracts, SnapshotEncodings.ZstdDictionary, 131000, 120);

            var summary = VmpTrafficStats.TakeWindowSummary();

            StringAssert.Contains(summary, "out 3.3 KB");
            StringAssert.Contains(summary, "PersistentSyncSnapshot 2.9 KB/1");
            StringAssert.Contains(summary, "VesselPosition 400 B/2");
            StringAssert.Contains(summary, "in 90 B");
            StringAssert.Contains(summary, "Contracts 1x");
            StringAssert.Contains(summary, "zstd+dict");
            Assert.IsNull(VmpTrafficStats.TakeWindowSummary(), "the window was reset");
            Assert.IsTrue(VmpTrafficStats.GetSentTotals().Any(r => r.Name == "VesselPositionMsgData" && r.Messages >= 2), "totals keep counting");
        }

        #endregion

        #region Helpers

        /// <summary>Text in the shape of a contracts snapshot: many similar blocks with random ids and values.</summary>
        private static byte[] ContractLikePayload(int contracts, int seed)
        {
            var random = new Random(seed);
            var text = new StringBuilder();
            for (var i = 0; i < contracts; i++)
            {
                text.Append("CONTRACT\n{\n");
                text.Append("\tguid = ").Append(new Guid(Enumerable.Range(0, 16).Select(_ => (byte)random.Next(256)).ToArray())).Append('\n');
                text.Append("\ttype = ").Append(i % 3 == 0 ? "RecoverAsset" : i % 3 == 1 ? "PartTest" : "SurveyContract").Append('\n');
                text.Append("\tstate = ").Append(i % 4 == 0 ? "Active" : "Offered").Append('\n');
                text.Append("\tfundsAdvance = ").Append(random.Next(100000)).Append('\n');
                text.Append("\tdeadline = ").Append(random.Next(1000000)).Append('\n');
                text.Append("\tPARAM\n\t{\n\t\tname = Orbit\n\t\tstate = Incomplete\n\t\tcompleted = ").Append(random.Next(10)).Append("\n\t}\n}\n");
            }

            return Encoding.UTF8.GetBytes(text.ToString());
        }

        private static byte[] Edit(byte[] source, int edits)
        {
            var copy = (byte[])source.Clone();
            var random = new Random(99);
            for (var i = 0; i < edits; i++)
            {
                copy[random.Next(copy.Length)] = (byte)('0' + random.Next(10));
            }

            return copy;
        }

        private static bool TryDecode(EncodedSnapshot encoded, SnapshotBase localBase, out byte[] raw, out string error)
        {
            return SnapshotCodec.TryDecode(encoded.Encoding, encoded.Data, encoded.Length, encoded.RawLength, encoded.BaseRevision, encoded.BaseChecksum, localBase, out raw, out error);
        }

        private static void AssertDecodes(EncodedSnapshot encoded, SnapshotBase localBase, byte[] expected)
        {
            Assert.IsTrue(TryDecode(encoded, localBase, out var raw, out var error), error);
            CollectionAssert.AreEqual(expected, raw);
        }

        private static IServerMessageBase RawSnapshotMessage(PersistentSyncDomainId domain, long revision, byte[] payload)
        {
            var data = new PersistentSyncSnapshotMsgData
            {
                DomainId = domain,
                Revision = revision,
                Payload = payload,
                NumBytes = payload.Length,
                PayloadEncoding = SnapshotEncodings.Raw,
                RawLength = payload.Length
            };
            return ServerFactory.CreateNew<PersistentSyncSrvMsg>(data);
        }

        /// <summary>What the client's deserializer hands over: the wire fields only, in a fresh pooled-style instance.</summary>
        private static PersistentSyncSnapshotMsgData CloneAsReceived(PersistentSyncSnapshotMsgData wire)
        {
            var outgoing = CreateOutgoing();
            wire.Serialize(outgoing);
            var read = new PersistentSyncSnapshotMsgData();
            read.Deserialize(CreateIncoming(outgoing.Data, outgoing.LengthBytes));
            return read;
        }

        private static PersistentSyncSnapshotMsgData RawAsReceived(PersistentSyncDomainId domain, long revision, byte[] payload)
        {
            return new PersistentSyncSnapshotMsgData
            {
                DomainId = domain,
                Revision = revision,
                Payload = payload,
                NumBytes = payload.Length,
                RawLength = payload.Length,
                PayloadEncoding = SnapshotEncodings.Raw
            };
        }

        private static PersistentSyncSnapshotMsgData ReceivedFrom(PersistentSyncDomainId domain, long revision, EncodedSnapshot encoded)
        {
            return new PersistentSyncSnapshotMsgData
            {
                DomainId = domain,
                Revision = revision,
                PayloadEncoding = encoded.Encoding,
                RawLength = encoded.RawLength,
                BaseRevision = encoded.BaseRevision,
                BaseChecksum = encoded.BaseChecksum,
                Payload = encoded.Data,
                NumBytes = encoded.Length
            };
        }

        private static PersistentSyncIntentMsgData FundsIntent(double funds, long knownRevision)
        {
            var payload = FundsIntentPayloadSerializer.Serialize(funds, "No-op");
            return new PersistentSyncIntentMsgData
            {
                DomainId = PersistentSyncDomainId.Funds,
                ClientKnownRevision = knownRevision,
                Payload = payload,
                NumBytes = payload.Length,
                Reason = "No-op"
            };
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
    }
}
