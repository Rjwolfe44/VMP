using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Stats;
using System.Collections.Generic;

namespace LmpCommon.PersistentSync
{
    /// <summary>
    /// Client side of the snapshot encodings: restores the domain's own payload from a received snapshot message and keeps
    /// the last raw snapshot of every domain, which the server uses as the reference for the next one.
    /// <para>
    /// The copy kept here is the last snapshot RECEIVED (not applied): the server tracks what it sent, and messages
    /// arrive in order, so the two stay equal. Anything that breaks the match (a snapshot never decoded because the system
    /// was off, a channel reset, a corrupt frame) shows up as a missing or different base, and the message is refused so
    /// the caller can ask for a full snapshot. Nothing wrong is ever applied.
    /// </para>
    /// Not thread safe by itself on purpose: the client reads its snapshot messages on one thread, in arrival order.
    /// </summary>
    public sealed class ClientSnapshotDecoder
    {
        private readonly Dictionary<PersistentSyncDomainId, SnapshotBase> _bases = new Dictionary<PersistentSyncDomainId, SnapshotBase>();

        /// <summary>Forgets every base (new connection or the system was restarted).</summary>
        public void Reset()
        {
            _bases.Clear();
        }

        /// <summary>
        /// Decodes <paramref name="data"/> in place: on success <c>Payload</c>/<c>NumBytes</c> hold the domain's own bytes and
        /// <c>PayloadEncoding</c> is Raw. On failure the message is left as received, the base for that domain is dropped, and
        /// <paramref name="error"/> says why.
        /// </summary>
        public bool TryDecodeInPlace(PersistentSyncSnapshotMsgData data, out string error)
        {
            error = null;
            var wireBytes = data.NumBytes;
            var encoding = data.PayloadEncoding;

            if (encoding != SnapshotEncodings.Raw)
            {
                _bases.TryGetValue(data.DomainId, out var localBase);
                if (!SnapshotCodec.TryDecode(encoding, data.Payload, data.NumBytes, data.RawLength, data.BaseRevision, data.BaseChecksum, localBase, out var raw, out error))
                {
                    _bases.Remove(data.DomainId);
                    return false;
                }

                data.Payload = raw;
                data.NumBytes = raw.Length;
                data.RawLength = raw.Length;
                data.PayloadEncoding = SnapshotEncodings.Raw;
            }

            Remember(data);
            VmpTrafficStats.RecordSnapshot(data.DomainId, encoding, data.NumBytes, wireBytes);
            return true;
        }

        /// <summary>The raw snapshot currently held for the domain (null when none); for tests and diagnostics.</summary>
        public SnapshotBase GetBase(PersistentSyncDomainId domainId)
        {
            _bases.TryGetValue(domainId, out var snapshotBase);
            return snapshotBase;
        }

        private void Remember(PersistentSyncSnapshotMsgData data)
        {
            // Same rule as the server: small payloads are neither encoded nor kept.
            if (data.NumBytes >= SnapshotCodec.MinCompressBytes)
            {
                _bases[data.DomainId] = SnapshotBase.Create(data.Revision, data.Payload, data.NumBytes);
            }
            else
            {
                _bases.Remove(data.DomainId);
            }
        }
    }
}
