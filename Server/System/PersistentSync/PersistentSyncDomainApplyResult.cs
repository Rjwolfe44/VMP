namespace Server.System.PersistentSync
{
    public class PersistentSyncDomainApplyResult
    {
        public bool Accepted;
        public bool Changed;
        public bool ReplyToOriginClient;

        /// <summary>
        /// True when <see cref="ReplyToOriginClient"/> is set only because the sender's known revision differs from the
        /// server's (nothing changed, nothing was rejected). Such a reply is a courtesy resync, so the registry skips it when
        /// the client was already sent this revision moments ago; a client whose snapshot apply is deferred keeps reporting
        /// the old revision on every intent and used to be answered with the whole snapshot each time.
        /// </summary>
        public bool ReplyOnlyBecauseRevisionDrift;

        /// <summary>
        /// When true the registry sends <see cref="Snapshot"/> to the current designated producer (contract lock
        /// owner) so a non-producer client can request a controlled generation pass (see
        /// <see cref="LmpCommon.PersistentSync.ContractIntentPayloadKind.RequestOfferGeneration"/>). Re-applying the
        /// canonical snapshot on the producer drives its <c>ReplenishStockOffersAfterPersistentSnapshotApply</c> pass,
        /// which mints offers back to the server as <see cref="LmpCommon.PersistentSync.ContractIntentPayloadKind.OfferObserved"/> proposals.
        /// </summary>
        public bool ReplyToProducerClient;
        public PersistentSyncDomainSnapshot Snapshot;
    }
}
