using LmpClient;
using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Systems.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon.Enums;
using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LmpClient.Systems.PersistentSync
{
    public class PersistentSyncSystem : MessageSystem<PersistentSyncSystem, PersistentSyncMessageSender, PersistentSyncMessageHandler>
    {
        public override string SystemName { get; } = nameof(PersistentSyncSystem);

        public override int ExecutionOrder => -50;

        protected override ClientState EnableStage => ClientState.ScenariosSynced;

        /// <summary>
        /// Per join session: <see cref="PersistentSyncDomainId.GameLaunchId"/> is requested only after the mandatory
        /// persistent-sync handshake completes so older servers (no domain 11) do not block the join batch.
        /// </summary>
        private bool _optionalGameLaunchIdPullSent;

        public PersistentSyncReconciler Reconciler { get; } = new PersistentSyncReconciler();

        /// <summary>
        /// Restores compressed snapshot payloads and remembers the last raw snapshot of each domain, the reference the
        /// server compresses the next one against (see <see cref="ClientSnapshotDecoder"/>).
        /// </summary>
        public ClientSnapshotDecoder SnapshotDecoder { get; } = new ClientSnapshotDecoder();

        private static readonly TimeSpan FullSnapshotRequestMinInterval = TimeSpan.FromSeconds(3);
        private readonly Dictionary<PersistentSyncDomainId, DateTime> _lastFullSnapshotRequestUtc = new Dictionary<PersistentSyncDomainId, DateTime>();

        /// <summary>
        /// Notices when snapshots stop reaching this client (see <see cref="PersistentSyncLivenessWatchdog"/>) and
        /// drives the probe / channel-reset recovery in <see cref="CheckSnapshotStreamLiveness"/>.
        /// </summary>
        public PersistentSyncLivenessWatchdog Liveness { get; } = new PersistentSyncLivenessWatchdog();

        private static readonly TimeSpan ProbeLogInterval = TimeSpan.FromMinutes(10);
        private DateTime _lastProbeLogUtc = DateTime.MinValue;

        // Network-layer vs handler-layer counters. If snapshots stop again, the stall log says whether they stopped
        // arriving from the network (transport) or arrived and were not handled (client queue).
        private long _networkSnapshotMessages;
        private long _lastNetworkSnapshotTicks;
        private long _handledSnapshotMessages;
        private PersistentSyncDomainId _lastHandledDomain;
        private long _lastHandledRevision;

        public Dictionary<PersistentSyncDomainId, IPersistentSyncClientDomain> Domains { get; } =
            new Dictionary<PersistentSyncDomainId, IPersistentSyncClientDomain>
            {
                [PersistentSyncDomainId.GameLaunchId] = new GameLaunchIdPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Funds] = new FundsPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Science] = new SciencePersistentSyncClientDomain(),
                [PersistentSyncDomainId.Reputation] = new ReputationPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Strategy] = new StrategyPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Achievements] = new AchievementsPersistentSyncClientDomain(),
                [PersistentSyncDomainId.ScienceSubjects] = new ScienceSubjectsPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Technology] = new TechnologyPersistentSyncClientDomain(),
                [PersistentSyncDomainId.ExperimentalParts] = new ExperimentalPartsPersistentSyncClientDomain(),
                [PersistentSyncDomainId.PartPurchases] = new PartPurchasesPersistentSyncClientDomain(),
                [PersistentSyncDomainId.UpgradeableFacilities] = new UpgradeableFacilitiesPersistentSyncClientDomain(),
                [PersistentSyncDomainId.Contracts] = new ContractsPersistentSyncClientDomain()
            };

        protected override void OnEnabled()
        {
            base.OnEnabled();
            ResetLivenessTracking();
            SnapshotDecoder.Reset();
            _lastFullSnapshotRequestUtc.Clear();
            LogSnapshotEncodingSupport();
            SetupRoutine(new RoutineDefinition(30000, RoutineExecution.Update, LogTrafficSummary));
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, FlushPendingState));
            // Registered after the base class's message-drain routine, so snapshots already queued this frame are
            // handled before the watchdog decides that none arrived.
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, CheckSnapshotStreamLiveness));
            GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneReady);
            GameEvents.onGUIRnDComplexSpawn.Add(OnRnDComplexSpawn);
        }

        private static void LogSnapshotEncodingSupport()
        {
            var capabilities = SnapshotCodec.LocalDecodeCapabilities;
            var zstd = (capabilities & SnapshotEncodings.CapZstd) != 0;
            LunaLog.Log($"[PersistentSync] snapshot encodings this client can read: quicklz, {(zstd ? "zstd, zstd+dictionary" : "(zstd unavailable: " + (string.IsNullOrEmpty(ZstdBackend.Error) ? "self test failed" : ZstdBackend.Error) + ")")}");
        }

        /// <summary>One line every 30 s with the bytes per message type (and what snapshot compression saved), only while something moved.</summary>
        private static void LogTrafficSummary()
        {
            var summary = VmpTrafficStats.TakeWindowSummary(5);
            if (summary != null)
            {
                LunaLog.Log(summary);
            }
        }

        /// <summary>
        /// A snapshot arrived that this client cannot restore exactly (it lacks the snapshot it was compressed against, or the
        /// data is damaged). Nothing is applied; the server is asked to resend the domain in full. Repeats for one domain
        /// within a few seconds are folded into the first request.
        /// </summary>
        internal void RequestFullSnapshot(PersistentSyncDomainId domainId, long revision, string reason)
        {
            var now = DateTime.UtcNow;
            if (_lastFullSnapshotRequestUtc.TryGetValue(domainId, out var last) && now - last < FullSnapshotRequestMinInterval)
            {
                return;
            }

            _lastFullSnapshotRequestUtc[domainId] = now;
            LunaLog.LogWarning($"[PersistentSync] could not read snapshot domain={domainId} revision={revision}: {reason}; asking the server for a full copy");
            MessageSender.SendFullSnapshotRequest(domainId);
        }

        /// <summary>Counts every PersistentSync message the network thread hands over (runs on the receive thread).</summary>
        public override void EnqueueMessage(IServerMessageBase msg)
        {
            Interlocked.Increment(ref _networkSnapshotMessages);
            Interlocked.Exchange(ref _lastNetworkSnapshotTicks, DateTime.UtcNow.Ticks);
            base.EnqueueMessage(msg);
        }

        /// <summary>
        /// Called by <see cref="PersistentSyncMessageHandler"/> for every snapshot that reaches the reconciler,
        /// before it is applied, deferred or ignored: any snapshot proves the stream is alive.
        /// </summary>
        internal void NoteSnapshotHandled(PersistentSyncSnapshotMsgData data)
        {
            _handledSnapshotMessages++;
            _lastHandledDomain = data.DomainId;
            _lastHandledRevision = data.Revision;

            var stalledFor = Liveness.OnSnapshotReceived(DateTime.UtcNow);
            if (stalledFor.HasValue)
            {
                LunaLog.Log(
                    $"[PersistentSync] snapshot stream restored after {stalledFor.Value.TotalSeconds:F0}s " +
                    $"(first snapshot domain={data.DomainId} revision={data.Revision} channelResetsRequested={_channelResetsRequested})");
                _channelResetsRequested = 0;
            }
        }

        private int _channelResetsRequested;

        private void ResetLivenessTracking()
        {
            Liveness.Reset(DateTime.UtcNow);
            _lastProbeLogUtc = DateTime.MinValue;
            _channelResetsRequested = 0;
            Interlocked.Exchange(ref _networkSnapshotMessages, 0);
            Interlocked.Exchange(ref _lastNetworkSnapshotTicks, 0);
            _handledSnapshotMessages = 0;
            _lastHandledRevision = 0;
        }

        /// <summary>
        /// Every second while in game: probes a quiet snapshot stream and, if a request goes unanswered, asks the
        /// server to move this client's snapshots to a fresh channel and resend every domain.
        /// </summary>
        private void CheckSnapshotStreamLiveness()
        {
            if (MainSystem.NetworkState < ClientState.Running)
            {
                return;
            }

            var domains = GetRequiredDomainsForCurrentSession();
            if (domains.Length == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            switch (Liveness.Evaluate(now))
            {
                case PersistentSyncLivenessAction.SendProbe:
                    var probeDomain = domains.Contains(PersistentSyncDomainId.Funds) ? PersistentSyncDomainId.Funds : domains[0];
                    if (now - _lastProbeLogUtc >= ProbeLogInterval)
                    {
                        _lastProbeLogUtc = now;
                        LunaLog.Log(
                            $"[PersistentSync] liveness probe domain={probeDomain} reason={Liveness.AwaitingReason} " +
                            $"lastSnapshot={(now - Liveness.LastSnapshotUtc).TotalSeconds:F0}s ago (logged at most every {ProbeLogInterval.TotalMinutes:F0} min)");
                    }

                    MessageSender.SendRequest(probeDomain);
                    break;
                case PersistentSyncLivenessAction.SendRecovery:
                    _channelResetsRequested++;
                    LunaLog.LogError(BuildStallReport(now, domains));
                    if (_channelResetsRequested == 3)
                    {
                        LunaScreenMsg.PostScreenMessage(
                            "VMP: career sync (funds, science, facilities) is not reaching this client. Reconnect if it does not recover.",
                            10f, ScreenMessageStyle.UPPER_CENTER, UnityEngine.Color.yellow);
                    }

                    MessageSender.SendRecoveryRequest(domains);
                    break;
            }
        }

        private string BuildStallReport(DateTime now, PersistentSyncDomainId[] domains)
        {
            var networkCount = Interlocked.Read(ref _networkSnapshotMessages);
            var lastNetworkTicks = Interlocked.Read(ref _lastNetworkSnapshotTicks);
            var queued = MessageHandler.IncomingMessages?.Count ?? 0;
            var lastNetwork = lastNetworkTicks == 0 ? "never" : $"{(now - new DateTime(lastNetworkTicks, DateTimeKind.Utc)).TotalSeconds:F0}s ago";
            var stallSince = Liveness.StallSinceUtc;
            var layer = queued > 0
                ? $"client queue ({queued} PersistentSync message(s) received but not handled)"
                : "transport (no PersistentSync message reached NetworkReceiver)";
            return
                $"[PersistentSync] snapshot stream STALLED: a request got no snapshot back for {(stallSince.HasValue ? (now - stallSince.Value).TotalSeconds : 0):F0}s; " +
                $"lastSnapshot={(now - Liveness.LastSnapshotUtc).TotalSeconds:F0}s ago domain={_lastHandledDomain} revision={_lastHandledRevision}; " +
                $"networkMessages={networkCount} handled={_handledSnapshotMessages} queued={queued} lastNetworkArrival={lastNetwork}; layer={layer}. " +
                $"Asking the server to move snapshots to a fresh channel and resend domains=[{string.Join(",", domains.Select(d => d.ToString()))}] (attempt {_channelResetsRequested})";
        }

        private static PersistentSyncDomainId[] GetRequiredDomainsForCurrentSession()
        {
            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            return PersistentSyncDomainApplicability
                .GetRequiredDomainsForInitialSync(SettingsSystem.ServerSettings.GameMode, caps)
                .ToArray();
        }

        /// <summary>
        /// Single live-predicate every <c>Share*MessageSender</c> must use when deciding whether to publish a client
        /// intent to the server. Returns true only when:
        /// <list type="bullet">
        /// <item><description>the system singleton exists and is Enabled (post-scenario-sync, pre-disconnect), AND</description></item>
        /// <item><description>a client-side domain handler for <paramref name="domainId"/> is registered (i.e., the
        /// scenario-domain layer for <paramref name="domainId"/> is actually live in this session).</description></item>
        /// </list>
        ///
        /// Scenario Sync Domain Contract rule: per-domain predicates (<c>IsPersistentSyncLiveForContracts</c>, inline
        /// <c>PersistentSyncSystem.Singleton.Enabled</c> checks, missing checks) are forbidden. Use this instead.
        /// </summary>
        public static bool IsLiveForDomain(PersistentSyncDomainId domainId)
        {
            var singleton = Singleton;
            if (singleton == null
                || !singleton.Enabled
                || singleton.Domains == null
                || !singleton.Domains.ContainsKey(domainId))
            {
                return false;
            }

            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            return PersistentSyncDomainApplicability.IsDomainApplicableForShareProducer(
                domainId,
                SettingsSystem.ServerSettings.GameMode,
                in caps);
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneReady);
            GameEvents.onGUIRnDComplexSpawn.Remove(OnRnDComplexSpawn);
            Reconciler.Reset(new PersistentSyncDomainId[0]);
            SnapshotDecoder.Reset();
            _lastFullSnapshotRequestUtc.Clear();
            _optionalGameLaunchIdPullSent = false;
            ResetLivenessTracking();
            if (Domains[PersistentSyncDomainId.UpgradeableFacilities] is UpgradeableFacilitiesPersistentSyncClientDomain facilitiesDomain)
            {
                facilitiesDomain.ResetSession();
            }
        }

        public void StartInitialSync()
        {
            _optionalGameLaunchIdPullSent = false;
            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            var requiredDomains = PersistentSyncDomainApplicability
                .GetRequiredDomainsForInitialSync(SettingsSystem.ServerSettings.GameMode, caps)
                .ToArray();
            Reconciler.Reset(requiredDomains);

            if (!requiredDomains.Any())
            {
                LunaLog.Log("[PersistentSync] StartInitialSync no required domains for current game mode; advancing to PersistentStateSynced");
                MainSystem.NetworkState = ClientState.PersistentStateSynced;
                RequestOptionalGameLaunchIdSnapshotAfterMandatorySync();
                return;
            }

            var domainList = string.Join(",", requiredDomains.Select(d => d.ToString()));
            LunaLog.Log($"[PersistentSync] StartInitialSync requesting snapshots for domains=[{domainList}]");
            MessageSender.SendRequest(requiredDomains);
        }

        /// <summary>
        /// Pulls <see cref="PersistentSyncDomainId.GameLaunchId"/> in a second request after mandatory domains so
        /// servers without that domain still complete the initial snapshot round-trip.
        /// </summary>
        internal void RequestOptionalGameLaunchIdSnapshotAfterMandatorySync()
        {
            if (_optionalGameLaunchIdPullSent)
            {
                return;
            }

            if (!IsLiveForDomain(PersistentSyncDomainId.GameLaunchId))
            {
                return;
            }

            _optionalGameLaunchIdPullSent = true;
            LunaLog.Log("[PersistentSync] optional post-handshake snapshot request domain=GameLaunchId");
            MessageSender.SendRequest(PersistentSyncDomainId.GameLaunchId);
        }

        /// <summary>
        /// Re-sends snapshot requests without clearing reconciler state. Used when the join watchdog
        /// detects prolonged idle time (should be rare while <see cref="NetworkSystem.BumpPersistentSyncJoinActivity"/> runs).
        /// </summary>
        public void ResendInitialSnapshotRequest()
        {
            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            var requiredDomains = PersistentSyncDomainApplicability
                .GetRequiredDomainsForInitialSync(SettingsSystem.ServerSettings.GameMode, caps)
                .ToArray();

            if (!requiredDomains.Any())
            {
                return;
            }

            var domainList = string.Join(",", requiredDomains.Select(d => d.ToString()));
            LunaLog.Log($"[PersistentSync] ResendInitialSnapshotRequest domains=[{domainList}]");
            MessageSender.SendRequest(requiredDomains);
        }

        /// <summary>
        /// Whether the client may leave <see cref="ClientState.PersistentStateSynced"/> for lock sync.
        /// Uses the same completion rule as leaving <see cref="ClientState.SyncingPersistentState"/>:
        /// join-time snapshot acceptance (including deferred-until-ingame payloads), not live KSP writes.
        /// Live apply continues via <see cref="FlushPendingState"/> and scene hooks.
        /// </summary>
        public bool IsPersistentSnapshotPhaseCompleteForCurrentSession()
        {
            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            var required = PersistentSyncDomainApplicability
                .GetRequiredDomainsForInitialSync(SettingsSystem.ServerSettings.GameMode, caps)
                .ToArray();
            return !required.Any() || Reconciler.State.AreAllJoinHandshakesComplete();
        }

        public long GetKnownRevision(PersistentSyncDomainId domainId)
        {
            return Reconciler.GetKnownRevision(domainId);
        }

        /// <summary>
        /// Retries buffered server snapshots first, then asks domains to commit any scalar values
        /// that were deferred until live game objects existed (<see cref="PersistentSyncApplyOutcome"/>).
        /// </summary>
        private void FlushPendingState()
        {
            NetworkSystem.BumpPersistentSyncJoinActivity();
            Reconciler.RetryDeferredSnapshots();
            Reconciler.FlushPendingState();
        }

        /// <summary>
        /// Runs the same work as the timed flush routine immediately. Call right after
        /// <c>HighLogic.CurrentGame.Start()</c> on join: deferred domains (notably <see cref="ContractsPersistentSyncClientDomain"/>
        /// when <see cref="ContractSystem.Instance"/> was null during snapshot handling) otherwise wait for the
        /// 1000ms routine, leaving an empty <see cref="ContractSystem"/> while stock Mission Control / ContractsApp
        /// already bind to that empty model.
        /// </summary>
        public void FlushLivePendingPersistentSyncState(string reason)
        {
            if (!Enabled)
            {
                return;
            }

            LunaLog.Log($"[PersistentSync] FlushLivePendingPersistentSyncState source={reason}");
            FlushPendingState();
        }

        private void OnSceneReady(GameScenes data)
        {
            NetworkSystem.BumpPersistentSyncJoinActivity();

            if (ResearchAndDevelopment.Instance != null && AssetBase.RnDTechTree != null)
            {
                int available = 0, unavailable = 0, totalStates = 0;
                foreach (var tech in AssetBase.RnDTechTree.GetTreeTechs().Where(t => t != null))
                {
                    var state = ResearchAndDevelopment.Instance.GetTechState(tech.techID);
                    if (state == null) continue;
                    totalStates++;
                    if (state.state == RDTech.State.Available) available++;
                    else unavailable++;
                }
                LunaLog.Log($"[PersistentSync] OnSceneReady scene={data} rdTechStates total={totalStates} available={available} unavailable={unavailable}");
            }
            else
            {
                LunaLog.Log($"[PersistentSync] OnSceneReady scene={data} rdInstance={(ResearchAndDevelopment.Instance != null)} rdTree={(AssetBase.RnDTechTree != null)}");
            }

            Reconciler.RetryDeferredSnapshots();
            Reconciler.FlushPendingState();

            if (data != GameScenes.SPACECENTER || MainSystem.NetworkState < ClientState.PersistentStateSynced)
            {
                return;
            }

            // ScenarioUpgradeableFacilities can initialize KSC defaults after our first PersistentSync flush;
            // re-apply the last server snapshot once GUI is ready so upgraded levels stick. The re-stage refuses a
            // cached snapshot older than the newest revision we know of and never lowers a facility the player
            // upgraded after that snapshot (see UpgradeableFacilityLocalUpgradeTracker).
            if (Domains[PersistentSyncDomainId.UpgradeableFacilities] is UpgradeableFacilitiesPersistentSyncClientDomain facilitiesDomain &&
                facilitiesDomain.TryStageReassertFromLastServerSnapshot(
                    Reconciler.GetHighestKnownRevision(PersistentSyncDomainId.UpgradeableFacilities),
                    Liveness.IsStallSuspected))
            {
                LunaLog.Log("[PersistentSync] KSC GUI ready re-staging facility snapshot for reconciler flush");
                Reconciler.FlushPendingState();
            }

            // ResearchAndDevelopment can be rebuilt from stale ProtoScenarioModule config after our first
            // apply; reassert the authoritative tech + purchases so opening R&D sees the server-correct tree.
            ReassertTechnologyAndPurchases("KSCGuiReady");

            // If we still never live-marked applied, ask the server to re-send (ClearDeferred in RequestResync).
            if (Reconciler.State.IsInitialJoinHandshakeComplete(PersistentSyncDomainId.UpgradeableFacilities) &&
                !Reconciler.State.HasInitialSnapshot(PersistentSyncDomainId.UpgradeableFacilities))
            {
                Reconciler.RequestResync(PersistentSyncDomainId.UpgradeableFacilities, "KscGuiReadyFacilityReapply");
            }
        }

        /// <summary>
        /// KSP's R&D complex GUI reads tech state at spawn time; if the stock module reinitialized
        /// R&D.Instance behind us, the panel would show a fresh tree even though our apply succeeded
        /// earlier. Reassert the last server snapshots here so the panel reflects server truth.
        /// </summary>
        private void OnRnDComplexSpawn()
        {
            if (MainSystem.NetworkState < ClientState.PersistentStateSynced)
            {
                return;
            }

            if (ResearchAndDevelopment.Instance != null && AssetBase.RnDTechTree != null)
            {
                int available = 0, unavailable = 0, totalStates = 0;
                foreach (var tech in AssetBase.RnDTechTree.GetTreeTechs().Where(t => t != null))
                {
                    var state = ResearchAndDevelopment.Instance.GetTechState(tech.techID);
                    if (state == null) continue;
                    totalStates++;
                    if (state.state == RDTech.State.Available) available++;
                    else unavailable++;
                }
                LunaLog.Log($"[PersistentSync] OnRnDComplexSpawn rdTechStates total={totalStates} available={available} unavailable={unavailable}");
            }

            // Defer one frame: stock R&D UI allocates placeholder RDNodes first; an immediate flush + tree
            // refresh here races that build (empty cog / "No text" nodes) before the real tree appears.
            CoroutineUtil.StartFrameDelayedRoutine(
                "PersistentSync.RndReassertNextFrame",
                () => ReassertTechnologyAndPurchases("RnDComplexSpawn+nextFrame"),
                1);
        }

        private void ReassertTechnologyAndPurchases(string source)
        {
            var flushNeeded = false;

            if (Domains.TryGetValue(PersistentSyncDomainId.Technology, out var techDomainObj) &&
                techDomainObj is TechnologyPersistentSyncClientDomain techDomain &&
                techDomain.TryStageReassertFromLastServerSnapshot())
            {
                LunaLog.Log($"[PersistentSync] {source} re-staging Technology snapshot for reconciler flush");
                flushNeeded = true;
            }

            if (Domains.TryGetValue(PersistentSyncDomainId.PartPurchases, out var partsDomainObj) &&
                partsDomainObj is PartPurchasesPersistentSyncClientDomain partsDomain &&
                partsDomain.TryStageReassertFromLastServerSnapshot())
            {
                LunaLog.Log($"[PersistentSync] {source} re-staging PartPurchases snapshot for reconciler flush");
                flushNeeded = true;
            }

            if (flushNeeded)
            {
                Reconciler.FlushPendingState();
            }
        }
    }
}
