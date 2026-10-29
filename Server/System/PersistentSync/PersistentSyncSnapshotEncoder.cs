using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Settings.Definition;
using Server.Settings.Structures;
using System;

namespace Server.System.PersistentSync
{
    /// <summary>
    /// Turns a PersistentSync snapshot message into the smallest form the receiving client can read, at the moment it is
    /// sent to that client. Doing it per client at send time is what makes the relative encoding safe: the server knows
    /// exactly which snapshot it last sent that client (<see cref="PersistentSyncSnapshotChannelRouter"/> keeps it, and drops
    /// it when the snapshot channel is reset or the client asks for a full resend), whatever got queued or dropped before.
    /// Messages to different clients are separate objects; the shared message is never modified.
    /// </summary>
    public static class PersistentSyncSnapshotEncoder
    {
        /// <summary>
        /// Returns <paramref name="message"/> itself when it goes out raw, otherwise a copy carrying the encoded payload.
        /// Never throws: any failure sends the original message.
        /// </summary>
        public static IServerMessageBase PrepareForClient(ClientStructure client, IServerMessageBase message)
        {
            if (client == null || !(message?.Data is PersistentSyncSnapshotMsgData data) || data.PayloadEncoding != SnapshotEncodings.Raw)
            {
                return message;
            }

            try
            {
                // A client that has not sent its first snapshot request yet has not started its PersistentSync system, so it
                // drops what it is sent now: nothing sent before that request can be a base for what follows.
                var reportedEncodings = PersistentSyncSnapshotChannelRouter.GetSupportedEncodings(client);
                if (reportedEncodings == 0)
                {
                    return message;
                }

                var capabilities = LimitBySetting(reportedEncodings);
                var receiverBase = PersistentSyncSnapshotChannelRouter.GetDeltaBase(client, data.DomainId);
                var encoded = SnapshotCodec.Encode(data.Payload, data.NumBytes, capabilities, receiverBase);

                // From here on the client holds this raw payload (reliable ordered delivery) and the next send is relative to it.
                PersistentSyncSnapshotChannelRouter.RememberSentPayload(client, data.DomainId, data.Revision, data.Payload, data.NumBytes);
                VmpTrafficStats.RecordSnapshot(data.DomainId, encoded.Encoding, data.NumBytes, encoded.Length);

                if (encoded.Encoding == SnapshotEncodings.Raw)
                {
                    return message;
                }

                return CreateEncodedMessage(data, encoded);
            }
            catch (Exception e)
            {
                LunaLog.Error($"[PersistentSync] snapshot encode failed client={client.PlayerName} domain={data.DomainId}: {e.Message}; sending it raw");
                PersistentSyncSnapshotChannelRouter.ClearDeltaBases(client, new[] { data.DomainId });
                return message;
            }
        }

        /// <summary>The client's reported capabilities, cut down to what the server's SnapshotCompression setting allows.</summary>
        internal static byte LimitBySetting(byte clientCapabilities)
        {
            switch (GeneralSettings.SettingsStore.SnapshotCompression)
            {
                case SnapshotCompressionMode.Off:
                    return 0;
                case SnapshotCompressionMode.QuickLz:
                    return (byte)(clientCapabilities & SnapshotEncodings.CapQuickLz);
                default:
                    return clientCapabilities;
            }
        }

        private static IServerMessageBase CreateEncodedMessage(PersistentSyncSnapshotMsgData original, EncodedSnapshot encoded)
        {
            var copy = ServerContext.ServerMessageFactory.CreateNewMessageData<PersistentSyncSnapshotMsgData>();
            copy.DomainId = original.DomainId;
            copy.Revision = original.Revision;
            copy.AuthorityPolicy = original.AuthorityPolicy;
            copy.PayloadEncoding = encoded.Encoding;
            copy.RawLength = encoded.RawLength;
            copy.BaseRevision = encoded.BaseRevision;
            copy.BaseChecksum = encoded.BaseChecksum;
            copy.Payload = encoded.Data;
            copy.NumBytes = encoded.Length;
            return ServerContext.ServerMessageFactory.CreateNew<PersistentSyncSrvMsg>(copy);
        }
    }
}
