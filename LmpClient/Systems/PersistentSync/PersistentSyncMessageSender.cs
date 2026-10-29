using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.PersistentSync;
using System;
using System.Linq;

namespace LmpClient.Systems.PersistentSync
{
    public class PersistentSyncMessageSender : SubSystem<PersistentSyncSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<PersistentSyncCliMsg>(msg)));
        }

        public void SendRequest(params PersistentSyncDomainId[] domains)
        {
            SendRequestInternal(domains, resetSnapshotChannel: false, forceFullSnapshot: false);
        }

        /// <summary>
        /// Snapshots stopped arriving: asks the server to move this client's snapshot stream to a fresh channel and
        /// resend <paramref name="domains"/> there. Older servers ignore the flag and simply resend.
        /// </summary>
        public void SendRecoveryRequest(PersistentSyncDomainId[] domains)
        {
            SendRequestInternal(domains, resetSnapshotChannel: true, forceFullSnapshot: false);
        }

        /// <summary>
        /// A snapshot arrived that this client cannot restore (it does not hold the snapshot it was encoded against):
        /// asks for <paramref name="domains"/> again as complete payloads, not relative to anything sent before.
        /// </summary>
        public void SendFullSnapshotRequest(params PersistentSyncDomainId[] domains)
        {
            SendRequestInternal(domains, resetSnapshotChannel: false, forceFullSnapshot: true);
        }

        private void SendRequestInternal(PersistentSyncDomainId[] domains, bool resetSnapshotChannel, bool forceFullSnapshot)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<PersistentSyncRequestMsgData>();
            msgData.DomainCount = domains.Length;
            msgData.Domains = domains;
            msgData.ResetSnapshotChannel = resetSnapshotChannel;
            msgData.ForceFullSnapshot = forceFullSnapshot;

            // Tells the server which snapshot encodings this client can read; it sends raw payloads until it has heard this.
            msgData.SupportedEncodings = SnapshotCodec.LocalDecodeCapabilities;

            // The server answers every request for a registered domain. GameLaunchId alone may be unknown to older
            // servers and then gets no reply, so it does not count as a request that must be answered.
            if (domains.Any(d => d != PersistentSyncDomainId.GameLaunchId))
            {
                System.Liveness.OnRequestSent(DateTime.UtcNow, resetSnapshotChannel ? "RecoveryRequest" : forceFullSnapshot ? "FullSnapshotRequest" : "Request");
            }

            SendMessage(msgData);
        }

        public void SendIntent(PersistentSyncDomainId domainId, long clientKnownRevision, byte[] payload, string reason)
        {
            System.Liveness.OnIntentSent(DateTime.UtcNow);
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<PersistentSyncIntentMsgData>();
            msgData.DomainId = domainId;
            msgData.ClientKnownRevision = clientKnownRevision;
            msgData.Payload = payload;
            msgData.NumBytes = payload.Length;
            msgData.Reason = reason;
            SendMessage(msgData);
        }

        public void SendFundsIntent(double funds, string reason)
        {
            var payload = FundsIntentPayloadSerializer.Serialize(funds, reason);
            SendIntent(PersistentSyncDomainId.Funds, System.GetKnownRevision(PersistentSyncDomainId.Funds), payload, reason);
        }

        public void SendScienceIntent(float science, string reason)
        {
            var payload = ScienceIntentPayloadSerializer.Serialize(science, reason);
            SendIntent(PersistentSyncDomainId.Science, System.GetKnownRevision(PersistentSyncDomainId.Science), payload, reason);
        }

        public void SendReputationIntent(float reputation, string reason)
        {
            var payload = ReputationIntentPayloadSerializer.Serialize(reputation, reason);
            SendIntent(PersistentSyncDomainId.Reputation, System.GetKnownRevision(PersistentSyncDomainId.Reputation), payload, reason);
        }

        public void SendUpgradeableFacilityIntent(string facilityId, int level, string reason)
        {
            var payload = UpgradeableFacilitiesIntentPayloadSerializer.Serialize(facilityId, level);
            var knownRevision = System.GetKnownRevision(PersistentSyncDomainId.UpgradeableFacilities);
            if (System.Domains.TryGetValue(PersistentSyncDomainId.UpgradeableFacilities, out var domain) &&
                domain is UpgradeableFacilitiesPersistentSyncClientDomain facilitiesDomain)
            {
                // Until a server snapshot shows this level, re-applying an older cached snapshot must not undo it.
                facilitiesDomain.NoteLocalUpgradeIntent(facilityId, level, knownRevision);
            }

            SendIntent(PersistentSyncDomainId.UpgradeableFacilities, knownRevision, payload, reason);
        }

        public void SendContractsIntentPayload(byte[] payload, string reason)
        {
            SendIntent(PersistentSyncDomainId.Contracts, System.GetKnownRevision(PersistentSyncDomainId.Contracts), payload, reason);
        }

        public void SendTechnologyIntent(TechnologySnapshotInfo[] technologies, string reason)
        {
            var payload = TechnologySnapshotPayloadSerializer.Serialize(technologies);
            SendIntent(PersistentSyncDomainId.Technology, System.GetKnownRevision(PersistentSyncDomainId.Technology), payload, reason);
        }

        public void SendStrategyIntent(StrategySnapshotInfo[] strategies, string reason)
        {
            var payload = StrategySnapshotPayloadSerializer.Serialize(strategies);
            SendIntent(PersistentSyncDomainId.Strategy, System.GetKnownRevision(PersistentSyncDomainId.Strategy), payload, reason);
        }

        public void SendAchievementsIntent(AchievementSnapshotInfo[] achievements, string reason)
        {
            var payload = AchievementSnapshotPayloadSerializer.Serialize(achievements);
            SendIntent(PersistentSyncDomainId.Achievements, System.GetKnownRevision(PersistentSyncDomainId.Achievements), payload, reason);
        }

        public void SendScienceSubjectsIntent(ScienceSubjectSnapshotInfo[] subjects, string reason)
        {
            var payload = ScienceSubjectSnapshotPayloadSerializer.Serialize(subjects);
            SendIntent(PersistentSyncDomainId.ScienceSubjects, System.GetKnownRevision(PersistentSyncDomainId.ScienceSubjects), payload, reason);
        }

        public void SendExperimentalPartsIntent(ExperimentalPartSnapshotInfo[] parts, string reason)
        {
            var payload = ExperimentalPartsSnapshotPayloadSerializer.Serialize(parts);
            SendIntent(PersistentSyncDomainId.ExperimentalParts, System.GetKnownRevision(PersistentSyncDomainId.ExperimentalParts), payload, reason);
        }

        public void SendPartPurchasesIntent(PartPurchaseSnapshotInfo[] purchases, string reason)
        {
            var payload = PartPurchasesSnapshotPayloadSerializer.Serialize(purchases);
            SendIntent(PersistentSyncDomainId.PartPurchases, System.GetKnownRevision(PersistentSyncDomainId.PartPurchases), payload, reason);
        }
    }
}
