using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LmpClient.Systems.VesselProtoSys
{
    /// <summary>
    /// This system handles the vessel loading into the game and sending our vessel structure to other players.
    /// </summary>
    public class VesselProtoSystem : MessageSystem<VesselProtoSystem, VesselProtoMessageSender, VesselProtoMessageHandler>
    {
        #region Fields & properties

        private static readonly HashSet<Guid> QueuedVesselsToSend = new HashSet<Guid>();

        public readonly HashSet<Guid> VesselsUnableToLoad = new HashSet<Guid>();

        public ConcurrentDictionary<Guid, VesselProtoQueue> VesselProtos { get; } = new ConcurrentDictionary<Guid, VesselProtoQueue>();

        /// <summary>
        /// Hash of the proto bytes last applied per vessel. A proto with identical bytes that arrives again (the server
        /// re-sends everything on each scene change) is dropped without parsing it or reloading the vessel.
        /// </summary>
        private readonly Dictionary<Guid, ulong> _lastAppliedProtoHash = new Dictionary<Guid, ulong>();

        /// <summary>
        /// Remote vessels are only loaded where they are shown. The editor has no use for them, and loading every vessel
        /// of the universe there (plus the game save that follows each batch) is what froze the builder.
        /// </summary>
        public static bool CanLoadRemoteVessels
        {
            get
            {
                var scene = HighLogic.LoadedScene;
                return scene == GameScenes.SPACECENTER || scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION;
            }
        }

        /// <summary>
        /// True while at least one queued proto is ready to be applied.
        /// </summary>
        public bool HasVesselsDueToLoad
        {
            get
            {
                var universalTime = TimeSyncSystem.UniversalTime;
                foreach (var keyVal in VesselProtos)
                {
                    if (keyVal.Value.TryPeek(out var proto) && proto.GameTime <= universalTime)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Vessels that have a proto waiting in the queue. The periodic vessel sync reports them as known, otherwise the
        /// server re-sends a proto we already hold for as long as it waits (throttled loads, or a future subspace time).
        /// </summary>
        public IEnumerable<Guid> VesselsWithQueuedProto()
        {
            foreach (var keyVal in VesselProtos)
            {
                if (!keyVal.Value.IsEmpty)
                    yield return keyVal.Key;
            }
        }

        public bool ProtoSystemReady => Enabled && FlightGlobals.ready && HighLogic.LoadedScene == GameScenes.FLIGHT &&
            FlightGlobals.ActiveVessel != null && !VesselCommon.IsSpectating;

        public VesselProtoEvents VesselProtoEvents { get; } = new VesselProtoEvents();

        public VesselRemoveSystem VesselRemoveSystem => VesselRemoveSystem.Singleton;

        /// <summary>
        /// Cap how many incoming protos we fully deserialize + load per Update. Server join sync sends many
        /// <see cref="VesselProtoMsgData"/> with GameTime default 0, so all are eligible at once; loading every
        /// vessel in a single frame spikes RAM (ConfigNode + parts). Spreading loads removes most of that peak.
        /// </summary>
        private const int MaxVesselProtoLoadsPerUpdate = 4;

        private static readonly object ManeuverNodeLogLock = new object();
        private static readonly string ManeuverNodeLogPath = Path.Combine(KSPUtil.ApplicationRootPath, "LMP_ManeuverNodes.log");
        private static readonly Dictionary<Guid, string> ManeuverSignatures = new Dictionary<Guid, string>();

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(VesselProtoSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();

            GameEvents.onFlightReady.Add(VesselProtoEvents.FlightReady);
            GameEvents.onGameSceneLoadRequested.Add(VesselProtoEvents.OnSceneRequested);

            GameEvents.OnTriggeredDataTransmission.Add(VesselProtoEvents.TriggeredDataTransmission);
            GameEvents.OnExperimentStored.Add(VesselProtoEvents.ExperimentStored);
            ExperimentEvent.onExperimentReset.Add(VesselProtoEvents.ExperimentReset);
            GameEvents.onManeuverAdded.Add(VesselProtoEvents.ManeuverNodeAdded);
            GameEvents.onManeuverRemoved.Add(VesselProtoEvents.ManeuverNodeRemoved);
            GameEvents.onGroundSciencePartDeployed.Add(VesselProtoEvents.GroundSciencePartDeployed);
            GameEvents.onGroundSciencePartChanged.Add(VesselProtoEvents.GroundSciencePartChanged);
            GameEvents.onGroundSciencePartEnabledStateChanged.Add(VesselProtoEvents.GroundSciencePartChanged);
            GameEvents.onGroundSciencePartRemoved.Add(VesselProtoEvents.GroundSciencePartRemoved);
            GameEvents.onGroundScienceClusterRegistered.Add(VesselProtoEvents.GroundScienceClusterChanged);
            GameEvents.onGroundScienceClusterUpdated.Add(VesselProtoEvents.GroundScienceClusterChanged);
            GameEvents.onGroundScienceControllerChanged.Add(VesselProtoEvents.GroundScienceControllerChanged);
            GameEvents.onGroundScienceGenerated.Add(VesselProtoEvents.GroundScienceGenerated);
            GameEvents.onGroundScienceTransmitted.Add(VesselProtoEvents.GroundScienceGenerated);
            GameEvents.onGroundScienceDeregisterCluster.Add(VesselProtoEvents.GroundScienceClusterDeregistered);

            PartEvent.onPartDecoupled.Add(VesselProtoEvents.PartDecoupled);
            PartEvent.onPartUndocked.Add(VesselProtoEvents.PartUndocked);
            PartEvent.onPartCoupled.Add(VesselProtoEvents.PartCoupled);

            WarpEvent.onTimeWarpStopped.Add(VesselProtoEvents.WarpStopped);

            SetupRoutine(new RoutineDefinition(0, RoutineExecution.Update, CheckVesselsToLoad));
            SetupRoutine(new RoutineDefinition(2500, RoutineExecution.Update, SendVesselDefinition));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();

            GameEvents.onFlightReady.Remove(VesselProtoEvents.FlightReady);
            GameEvents.onGameSceneLoadRequested.Remove(VesselProtoEvents.OnSceneRequested);

            GameEvents.OnTriggeredDataTransmission.Remove(VesselProtoEvents.TriggeredDataTransmission);
            GameEvents.OnExperimentStored.Remove(VesselProtoEvents.ExperimentStored);
            ExperimentEvent.onExperimentReset.Remove(VesselProtoEvents.ExperimentReset);
            GameEvents.onManeuverAdded.Remove(VesselProtoEvents.ManeuverNodeAdded);
            GameEvents.onManeuverRemoved.Remove(VesselProtoEvents.ManeuverNodeRemoved);
            GameEvents.onGroundSciencePartDeployed.Remove(VesselProtoEvents.GroundSciencePartDeployed);
            GameEvents.onGroundSciencePartChanged.Remove(VesselProtoEvents.GroundSciencePartChanged);
            GameEvents.onGroundSciencePartEnabledStateChanged.Remove(VesselProtoEvents.GroundSciencePartChanged);
            GameEvents.onGroundSciencePartRemoved.Remove(VesselProtoEvents.GroundSciencePartRemoved);
            GameEvents.onGroundScienceClusterRegistered.Remove(VesselProtoEvents.GroundScienceClusterChanged);
            GameEvents.onGroundScienceClusterUpdated.Remove(VesselProtoEvents.GroundScienceClusterChanged);
            GameEvents.onGroundScienceControllerChanged.Remove(VesselProtoEvents.GroundScienceControllerChanged);
            GameEvents.onGroundScienceGenerated.Remove(VesselProtoEvents.GroundScienceGenerated);
            GameEvents.onGroundScienceTransmitted.Remove(VesselProtoEvents.GroundScienceGenerated);
            GameEvents.onGroundScienceDeregisterCluster.Remove(VesselProtoEvents.GroundScienceClusterDeregistered);

            PartEvent.onPartDecoupled.Remove(VesselProtoEvents.PartDecoupled);
            PartEvent.onPartUndocked.Remove(VesselProtoEvents.PartUndocked);
            PartEvent.onPartCoupled.Remove(VesselProtoEvents.PartCoupled);

            WarpEvent.onTimeWarpStopped.Remove(VesselProtoEvents.WarpStopped);

            //This is the main system that handles the vesselstore so if it's disabled clear the store too
            VesselProtos.Clear();
            VesselsUnableToLoad.Clear();
            _lastAppliedProtoHash.Clear();
            QueuedVesselsToSend.Clear();
            ManeuverSignatures.Clear();
        }

        #endregion

        #region Update routines

        /// <summary>
        /// Send the definition of our own vessel and the secondary vessels.
        /// </summary>
        private void SendVesselDefinition()
        {
            try
            {
                if (ProtoSystemReady)
                {
                    var activeVessel = FlightGlobals.ActiveVessel;
                    if (activeVessel.parts.Count != activeVessel.protoVessel.protoPartSnapshots.Count)
                        MessageSender.SendVesselMessage(activeVessel);

                    CheckAndSendManeuverChanges(activeVessel);

                    foreach (var vessel in VesselCommon.GetSecondaryVessels())
                    {
                        if (vessel.parts.Count != vessel.protoVessel.protoPartSnapshots.Count)
                            MessageSender.SendVesselMessage(vessel);
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Error in SendVesselDefinition {e}");
            }

        }

        /// <summary>
        /// Check vessels that must be loaded
        /// </summary>
        public void CheckVesselsToLoad()
        {
            if (!CanLoadRemoteVessels) return;

            var checkStart = StallWatch.Start();
            var loaded = 0;

            try
            {
                var loadsRemaining = MaxVesselProtoLoadsPerUpdate;
                var universalTime = TimeSyncSystem.UniversalTime;
                foreach (var keyVal in VesselProtos)
                {
                    if (loadsRemaining <= 0)
                        break;

                    var queue = keyVal.Value;
                    if (!queue.TryPeek(out var vesselProto) || vesselProto.GameTime > universalTime)
                        continue;

                    if (!queue.TryDequeue(out vesselProto))
                        continue;

                    loadsRemaining--;

                    // A proto is a full snapshot of the vessel: when several are already due only the newest one matters.
                    var forceReload = vesselProto.ForceReload;
                    while (queue.TryPeek(out var newer) && newer.GameTime <= universalTime && queue.TryDequeue(out newer))
                    {
                        queue.Recycle(vesselProto);
                        vesselProto = newer;
                        forceReload |= vesselProto.ForceReload;
                    }

                    var vesselId = vesselProto.VesselId;
                    if (VesselRemoveSystem.VesselWillBeKilled(vesselId))
                    {
                        queue.Recycle(vesselProto);
                        continue;
                    }

                    var protoHash = ProtoHash(vesselProto.RawData, vesselProto.NumBytes);
                    var existingVessel = FlightGlobals.FindVessel(vesselId);
                    if (!forceReload && existingVessel != null && _lastAppliedProtoHash.TryGetValue(vesselId, out var lastHash) && lastHash == protoHash)
                    {
                        queue.Recycle(vesselProto);
                        continue;
                    }

                    var protoVessel = vesselProto.CreateProtoVessel();
                    queue.Recycle(vesselProto);

                    if (protoVessel == null || protoVessel.HasInvalidParts(!VesselsUnableToLoad.Contains(vesselId)))
                    {
                        VesselsUnableToLoad.Add(vesselId);
                        continue;
                    }

                    VesselsUnableToLoad.Remove(vesselId);

                    if (existingVessel == null)
                    {
                        if (VesselLoader.LoadVessel(protoVessel, forceReload))
                        {
                            _lastAppliedProtoHash[vesselId] = protoHash;
                            loaded++;
                            LunaLog.Log($"[VMP]: Vessel {protoVessel.vesselID} loaded");
                            VesselLoadEvent.onLmpVesselLoaded.Fire(protoVessel.vesselRef);
                        }
                    }
                    else if (VesselLoader.LoadVessel(protoVessel, forceReload))
                    {
                        _lastAppliedProtoHash[vesselId] = protoHash;

                        // The loader returns true without touching the vessel when parts and crew already match.
                        if (protoVessel.vesselRef != null)
                        {
                            loaded++;
                            LunaLog.Log($"[VMP]: Vessel {protoVessel.vesselID} reloaded");
                            VesselReloadEvent.onLmpVesselReloaded.Fire(protoVessel.vesselRef);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Error in CheckVesselsToLoad {e}");
            }

            if (loaded > 0)
            {
                StallWatch.End(checkStart, $"loading {loaded} remote vessel(s)", 250);
            }
        }

        /// <summary>
        /// FNV-1a over the proto bytes; only used to recognise a proto we already applied.
        /// </summary>
        private static ulong ProtoHash(byte[] data, int length)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                for (var i = 0; i < length; i++)
                {
                    hash ^= data[i];
                    hash *= 1099511628211UL;
                }

                return hash ^ (ulong)length;
            }
        }

        #endregion

        #region Public methods

        public void InitializeActiveVesselManeuverTracking(string reason)
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.id == Guid.Empty)
                return;

            var signature = GetManeuverSignature(activeVessel);
            ManeuverSignatures[activeVessel.id] = signature;
            WriteManeuverNodeLog(activeVessel, $"Updated maneuver tracking ({reason})", signature);
        }

        public void SendActiveVesselManeuverNodes(string reason)
        {
            if (!ProtoSystemReady)
                return;

            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.id == Guid.Empty)
                return;

            var signature = GetManeuverSignature(activeVessel);
            ManeuverSignatures[activeVessel.id] = signature;
            WriteManeuverNodeLog(activeVessel, $"Immediate maneuver node send ({reason})", signature);
            MessageSender.SendVesselMessage(activeVessel);
        }

        private void CheckAndSendManeuverChanges(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty)
                return;

            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(vessel.id, SettingsSystem.CurrentSettings.PlayerName))
                return;

            var signature = GetManeuverSignature(vessel);
            if (ManeuverSignatures.TryGetValue(vessel.id, out var lastSignature))
            {
                if (lastSignature == signature)
                    return;

                ManeuverSignatures[vessel.id] = signature;
                WriteManeuverNodeLog(vessel, $"Detected maneuver node change via poll. Previous: {lastSignature}", signature);
                MessageSender.SendVesselMessage(vessel);
                return;
            }

            ManeuverSignatures[vessel.id] = signature;
            WriteManeuverNodeLog(vessel, "Tracking active vessel maneuver nodes", signature);
        }

        private static string GetManeuverSignature(Vessel vessel)
        {
            var solver = vessel?.patchedConicSolver;
            if (solver == null || solver.maneuverNodes == null || solver.maneuverNodes.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            for (var i = 0; i < solver.maneuverNodes.Count; i++)
            {
                var node = solver.maneuverNodes[i];
                if (node == null)
                {
                    sb.Append("|null");
                    continue;
                }

                var deltaV = node.DeltaV;
                sb.Append('|').Append(i).Append(':')
                    .Append(node.UT.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(deltaV.x.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(deltaV.y.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(deltaV.z.ToString("F3", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static void WriteManeuverNodeLog(Vessel vessel, string message, string signature)
        {
            try
            {
                var vesselName = vessel?.vesselName ?? "<null>";
                var vesselId = vessel?.id.ToString() ?? Guid.Empty.ToString();
                var line = $"[{DateTime.UtcNow:O}] {message} | Vessel={vesselName} | Id={vesselId} | Signature={signature}{Environment.NewLine}";

                lock (ManeuverNodeLogLock)
                {
                    File.AppendAllText(ManeuverNodeLogPath, line);
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Error writing maneuver node log: {e}");
            }
        }

        /// <summary>
        /// Sends a delayed vessel definition to the server.
        /// Call this method if you expect to do a lot of modifications to a vessel and you want to send it only once
        /// </summary>
        public void DelayedSendVesselMessage(Guid vesselId, float delayInSec, bool forceReload = false)
        {
            if (QueuedVesselsToSend.Contains(vesselId)) return;

            QueuedVesselsToSend.Add(vesselId);
            CoroutineUtil.StartDelayedRoutine("QueueVesselMessageAsPartsChanged", () =>
            {
                QueuedVesselsToSend.Remove(vesselId);

                LunaLog.Log($"[VMP]: Sending delayed proto vessel {vesselId}");
                MessageSender.SendVesselMessage(FlightGlobals.FindVessel(vesselId));
            }, delayInSec);
        }

        /// <summary>
        /// Removes a vessel from the system
        /// </summary>
        public void RemoveVessel(Guid vesselId)
        {
            VesselProtos.TryRemove(vesselId, out _);
        }

        #endregion
    }
}
