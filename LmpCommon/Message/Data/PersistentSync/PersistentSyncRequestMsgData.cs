using Lidgren.Network;
using LmpCommon.PersistentSync;

namespace LmpCommon.Message.Data.PersistentSync
{
    public class PersistentSyncRequestMsgData : PersistentSyncBaseMsgData
    {
        internal PersistentSyncRequestMsgData() { }

        public override Message.Types.PersistentSyncMessageType PersistentSyncMessageType => Message.Types.PersistentSyncMessageType.Request;

        public int DomainCount;
        public PersistentSyncDomainId[] Domains = new PersistentSyncDomainId[0];

        /// <summary>
        /// Set by a client that has stopped receiving PersistentSync snapshots: the server moves this client's
        /// snapshot stream to a fresh Lidgren channel before answering. Sent as an optional trailing byte, so older
        /// servers ignore it and newer servers read a missing byte as false. Always assign it; message data is pooled.
        /// </summary>
        public bool ResetSnapshotChannel;

        /// <summary>
        /// Set by a client that could not read a snapshot it received (it lacks the base the sender encoded against, or the
        /// frame was corrupt): the server forgets what it sent this client for the requested domains and answers with
        /// full, base-free snapshots. Always assign it; message data is pooled.
        /// </summary>
        public bool ForceFullSnapshot;

        /// <summary>
        /// Snapshot encodings this client can read (<see cref="SnapshotEncodings"/> capability bits). The server sends raw
        /// payloads until it has seen this once. Always assign it; message data is pooled.
        /// </summary>
        public byte SupportedEncodings;

        public override string ClassName { get; } = nameof(PersistentSyncRequestMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(DomainCount);
            for (var i = 0; i < DomainCount; i++)
            {
                lidgrenMsg.Write((byte)Domains[i]);
            }

            lidgrenMsg.Write((byte)((ResetSnapshotChannel ? 1 : 0) | (ForceFullSnapshot ? 2 : 0)));
            lidgrenMsg.Write(SupportedEncodings);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            DomainCount = lidgrenMsg.ReadInt32();
            if (Domains.Length < DomainCount)
            {
                Domains = new PersistentSyncDomainId[DomainCount];
            }

            for (var i = 0; i < DomainCount; i++)
            {
                Domains[i] = (PersistentSyncDomainId)lidgrenMsg.ReadByte();
            }

            var flags = lidgrenMsg.LengthBits - lidgrenMsg.Position >= 8 ? lidgrenMsg.ReadByte() : (byte)0;
            ResetSnapshotChannel = (flags & 1) != 0;
            ForceFullSnapshot = (flags & 2) != 0;
            SupportedEncodings = lidgrenMsg.LengthBits - lidgrenMsg.Position >= 8 ? lidgrenMsg.ReadByte() : (byte)0;
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(int) + DomainCount * sizeof(byte) + sizeof(byte) + sizeof(byte);
        }
    }
}
