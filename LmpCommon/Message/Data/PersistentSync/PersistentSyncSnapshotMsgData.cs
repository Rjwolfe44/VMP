using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.PersistentSync;

namespace LmpCommon.Message.Data.PersistentSync
{
    public class PersistentSyncSnapshotMsgData : PersistentSyncBaseMsgData
    {
        internal PersistentSyncSnapshotMsgData() { }

        public override Message.Types.PersistentSyncMessageType PersistentSyncMessageType => Message.Types.PersistentSyncMessageType.Snapshot;

        public PersistentSyncDomainId DomainId;
        public long Revision;
        public PersistentAuthorityPolicy AuthorityPolicy;

        /// <summary>
        /// The payload bytes as they travel: <see cref="Payload"/> and <see cref="NumBytes"/> hold the encoded form while
        /// this is not <see cref="SnapshotEncodings.Raw"/>. A receiver decodes in place (<see cref="ClientSnapshotDecoder"/>)
        /// and everything after that sees the domain's own bytes with this set back to Raw. Always assign it: message data is pooled.
        /// </summary>
        public byte PayloadEncoding;

        /// <summary>Size of the domain's own payload once decoded. Equals <see cref="NumBytes"/> for Raw.</summary>
        public int RawLength;

        /// <summary>Only for <see cref="SnapshotEncodings.ZstdDictionary"/>: the snapshot (revision and checksum) the payload was encoded against.</summary>
        public long BaseRevision;
        public uint BaseChecksum;

        public byte[] Payload = new byte[0];
        public int NumBytes;

        public override string ClassName { get; } = nameof(PersistentSyncSnapshotMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write((byte)DomainId);
            lidgrenMsg.Write(Revision);
            lidgrenMsg.Write((byte)AuthorityPolicy);
            lidgrenMsg.Write(PayloadEncoding);
            if (PayloadEncoding != SnapshotEncodings.Raw)
            {
                lidgrenMsg.Write(RawLength);
                lidgrenMsg.Write(BaseRevision);
                lidgrenMsg.Write(BaseChecksum);
            }

            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Payload, 0, NumBytes);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            DomainId = (PersistentSyncDomainId)lidgrenMsg.ReadByte();
            Revision = lidgrenMsg.ReadInt64();
            AuthorityPolicy = (PersistentAuthorityPolicy)lidgrenMsg.ReadByte();
            PayloadEncoding = lidgrenMsg.ReadByte();
            if (PayloadEncoding != SnapshotEncodings.Raw)
            {
                RawLength = lidgrenMsg.ReadInt32();
                BaseRevision = lidgrenMsg.ReadInt64();
                BaseChecksum = lidgrenMsg.ReadUInt32();
            }
            else
            {
                RawLength = 0;
                BaseRevision = 0;
                BaseChecksum = 0;
            }

            NumBytes = lidgrenMsg.ReadInt32();
            if (PayloadEncoding == SnapshotEncodings.Raw)
            {
                RawLength = NumBytes;
            }

            if (Payload.Length < NumBytes)
            {
                Payload = new byte[NumBytes];
            }

            lidgrenMsg.ReadBytes(Payload, 0, NumBytes);
        }

        internal override int InternalGetMessageSize()
        {
            var encodingHeader = sizeof(byte) + (PayloadEncoding != SnapshotEncodings.Raw ? sizeof(int) + sizeof(long) + sizeof(uint) : 0);
            return base.InternalGetMessageSize() + sizeof(byte) + sizeof(long) + sizeof(byte) + encodingHeader + sizeof(int) + NumBytes;
        }
    }
}
