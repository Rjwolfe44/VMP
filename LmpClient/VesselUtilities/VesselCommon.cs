using LmpClient.Network;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselCoupleSys;
using LmpClient.Systems.VesselDecoupleSys;
using LmpClient.Systems.VesselFairingsSys;
using LmpClient.Systems.VesselFlightStateSys;
using LmpClient.Systems.VesselPartSyncCallSys;
using LmpClient.Systems.VesselPartSyncFieldSys;
using LmpClient.Systems.VesselPartSyncUiFieldSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Systems.VesselResourceSys;
using LmpClient.Systems.VesselUndockSys;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LmpClient.VesselUtilities
{
    /// <summary>
    /// Class to hold common logic regarding the Vessel systems
    /// </summary>
    public class VesselCommon
    {
        /// <summary>
        /// How far BEHIND real time we render remote vessels.
        /// <para>
        /// Latency budget is <c>max(localPing, remotePing)</c> — the slower of the two one-way
        /// paths — clamped between an adaptive floor and 2.5s. Previous formula was
        /// <c>localPing + remotePing</c>, which double-counted parallel paths and pushed each
        /// observer further behind real time than required.
        /// </para>
        /// <para>
        /// The <c>max</c> form also reduces the "further forward / further behind" asymmetry
        /// between observers: when both observers' localPing is smaller than the sender's
        /// remotePing (the common docking case, where the pilot is the remote sender), the
        /// formula reduces to the same remotePing on both clients, so they agree on the render
        /// timestamp for that vessel and converge on the same visual position. With the old
        /// additive formula each observer subtracted their own localPing, guaranteeing
        /// disagreement.
        /// </para>
        /// <para>
        /// Adaptive floor is <c>max(100ms, 2 * VesselUpdatesMsInterval)</c>. The floor exists
        /// because the interpolation controller compares current time against the PREVIOUS
        /// snapshot timestamp; if the delay drops below roughly one send interval, the
        /// controller reads itself as "behind" on almost every packet and shortens interpolation
        /// aggressively, producing visible micro-holds/snaps on localhost and LAN. Two send
        /// intervals keeps one future packet in reserve without reintroducing the old 250ms
        /// docking lag.
        /// </para>
        /// </summary>
        public static float PositionAndFlightStateMessageOffsetSec(float targetPingSec)
        {
            var sendIntervalSec = SettingsSystem.ServerSettings.VesselUpdatesMsInterval / 1000f;
            var floor = Mathf.Max(0.100f, 2f * sendIntervalSec);
            // Use max(local, remote) rather than local+remote: the two pings describe parallel
            // paths (sender->server and server->you), not a serial round-trip. Summing them
            // double-counts and over-delays rendering. Taking the max uses the bottleneck path,
            // approximately halves visible closing-velocity offset, and lets observers with low
            // localPing converge on the same render timestamp (= remotePing), removing the
            // "further forward / further behind" asymmetry between observers of the same vessel.
            var budget = Mathf.Max(NetworkStatistics.PingSec, targetPingSec);
            return Mathf.Clamp(budget, floor, 2.5f);
        }

        public static bool UpdateIsForOwnVessel(Guid vesselId)
        {
            //Ignore updates to our own vessel if we aren't spectating
            return !IsSpectating && FlightGlobals.ActiveVessel && FlightGlobals.ActiveVessel.id == vesselId;
        }

        private static bool _isSpectating;
        public static bool IsSpectating
        {
            get => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ActiveVessel != null && _isSpectating;
            set => _isSpectating = value;
        }

        /// <summary>
        /// Return the controlled vessel ids
        /// </summary>
        public static IEnumerable<Guid> GetControlledVesselIds()
        {
            return LockSystem.LockQuery.GetAllControlLocks()
                .Select(v => v.VesselId);
        }

        /// <summary>
        /// Removes the specified vessel from the vessel systems
        /// </summary>
        public static void RemoveVesselFromSystems(Guid vesselId)
        {
            VesselPositionSystem.Singleton.RemoveVessel(vesselId);
            VesselFlightStateSystem.Singleton.RemoveVessel(vesselId);
            VesselResourceSystem.Singleton.RemoveVessel(vesselId);
            VesselProtoSystem.Singleton.RemoveVessel(vesselId);
            VesselPartSyncFieldSystem.Singleton.RemoveVessel(vesselId);
            VesselPartSyncUiFieldSystem.Singleton.RemoveVessel(vesselId);
            VesselPartSyncCallSystem.Singleton.RemoveVessel(vesselId);
            VesselFairingsSystem.Singleton.RemoveVessel(vesselId);
            VesselCoupleSystem.Singleton.RemoveVessel(vesselId);
            VesselDecoupleSystem.Singleton.RemoveVessel(vesselId);
            VesselUndockSystem.Singleton.RemoveVessel(vesselId);
        }

        /// <summary>
        /// Check if there are other player controlled vessels nearby
        /// </summary>
        /// <returns></returns>
        public static bool PlayerVesselsNearby()
        {
            if (FlightGlobals.ActiveVessel != null)
            {
                // ReSharper disable once LoopCanBeConvertedToQuery
                // ReSharper disable once ForCanBeConvertedToForeach
                for (var i = 0; i < FlightGlobals.VesselsLoaded.Count; i++)
                {
                    if (FlightGlobals.VesselsLoaded[i] != FlightGlobals.ActiveVessel)
                        return true;
                }

                return false;

                //TODO: I simplified this method as it generates a lot of garbage since it's called on every frame
                ////If there is someone spectating us then return true and update it fast;
                //if (IsSomeoneSpectatingUs)
                //{
                //    return true;
                //}

                //var controlledVesselsIds = GetControlledVesselIds();
                //var loadedVesselIds = FlightGlobals.VesselsLoaded?.Where(v => v != null).Select(v => v.id);

                //if (loadedVesselIds != null)
                //    return controlledVesselsIds.Intersect(loadedVesselIds).Any(v => v != FlightGlobals.ActiveVessel?.id);
            }

            return false;
        }

        /// <summary>
        /// Returns true when at least one other player-controlled vessel shares the same main body
        /// as the active vessel but is not loaded (i.e. not "nearby" in the KSP physics sense).
        /// Used for the intermediate update-rate tier: same body → medium rate, different body → slow.
        /// </summary>
        public static bool PlayerVesselsOnSameBody()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null) return false;

            var activeBody = activeVessel.mainBody;
            if (activeBody == null) return false;

            foreach (var guid in GetControlledVesselIds())
            {
                if (guid == activeVessel.id) continue;
                var v = FlightGlobals.FindVessel(guid);
                if (v != null && v.mainBody == activeBody)
                    return true;
            }
            return false;
        }

        public static bool OtherLoadedVesselWithinRange(Vessel sourceVessel, float rangeMeters)
        {
            if (!sourceVessel || rangeMeters <= 0f) return false;

            for (var i = 0; i < FlightGlobals.VesselsLoaded.Count; i++)
            {
                var vessel = FlightGlobals.VesselsLoaded[i];
                if (!vessel || vessel == sourceVessel)
                    continue;

                if (vessel.state == Vessel.State.DEAD || vessel.vesselType == VesselType.Flag || vessel.vesselType == VesselType.Debris)
                    continue;

                if (Vector3d.Distance(vessel.vesselTransform.position, sourceVessel.vesselTransform.position) <= rangeMeters)
                    return true;
            }

            return false;
        }

        public static bool IsIdleSecondaryVessel(Vessel vessel)
        {
            if (!vessel) return false;
            if (!SettingsSystem.ServerSettings.IdleVesselDetectionEnabled) return false;
            if (vessel.Landed || vessel.Splashed) return true;

            var speedThreshold = Mathf.Max(0f, SettingsSystem.ServerSettings.IdleVesselSpeedThresholdMs);
            var throttleIdle = vessel.ctrlState == null || vessel.ctrlState.mainThrottle < 0.01f;
            var rcsIdle = vessel.ActionGroups == null || !vessel.ActionGroups[KSPActionGroup.RCS];
            return vessel.srf_velocity.magnitude <= speedThreshold && throttleIdle && rcsIdle;
        }

        /// <summary>
        /// Check if we should apply a message to the given vesselId
        /// </summary>
        public static bool DoVesselChecks(Guid vesselId)
        {
            //Ignore updates if vessel is in kill list
            if (VesselRemoveSystem.Singleton.VesselWillBeKilled(vesselId))
                return false;

            //Ignore vessel updates for our own controlled vessel
            if (LockSystem.LockQuery.ControlLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName))
                return false;

            //Another player is flying this vessel — always apply their streamed state. Without this, the first
            //client in the session can win the VesselLoaded update-lock race and then ignore all position/flight
            //messages for that vessel because UpdateLockBelongsToPlayer stays true locally until the server
            //hands the lock to the pilot (or forever if messages reorder).
            if (LockSystem.LockQuery.ControlLockExists(vesselId) &&
                !LockSystem.LockQuery.ControlLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName))
            {
                return true;
            }

            // Second client can acquire the update lock in VesselLoaded before the pilot's control lock row exists
            // locally. Without this branch, the next check returns false ("we own update") and we ignore the pilot's
            // stream — same class of bug as reversed HUD / wrong FlightIntegrator on the other player's craft.
            // Applies when nobody has a control lock yet, the vessel is loaded, not our ActiveVessel, and the vessel
            // is in a state where the pilot will stream physics/FX/part-calls (include pad/prelaunch/landed: missing
            // those situations caused one client to ignore engine smoke + other sync while the other still saw labels).
            if (LockSystem.LockQuery.UpdateLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName) &&
                !LockSystem.LockQuery.ControlLockExists(vesselId) &&
                FlightGlobals.ActiveVessel != null &&
                vesselId != FlightGlobals.ActiveVessel.id)
            {
                var v = FlightGlobals.FindVessel(vesselId);
                if (v != null && v.loaded)
                {
                    switch (v.situation)
                    {
                        case Vessel.Situations.PRELAUNCH:
                        case Vessel.Situations.LANDED:
                        case Vessel.Situations.SPLASHED:
                        case Vessel.Situations.DOCKED:
                        case Vessel.Situations.FLYING:
                        case Vessel.Situations.ORBITING:
                        case Vessel.Situations.SUB_ORBITAL:
                        case Vessel.Situations.ESCAPING:
                            return true;
                    }
                }
            }

            //Ignore vessel updates for our own updated vessels
            if (LockSystem.LockQuery.UpdateLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName))
                return false;

            //Ignore vessel updates for our own updated vessels
            if (LockSystem.LockQuery.UnloadedUpdateLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName))
                return false;

            return true;
        }

        /// <summary>
        /// Return all the vessels except the active one that we have the update lock and that are loaded
        /// </summary>
        public static IEnumerable<Vessel> GetSecondaryVessels()
        {
            //We don't need to check if vessel is in safety bubble as the update locks are updated accordingly
            var result = new List<Vessel>();
            var playerName = SettingsSystem.CurrentSettings.PlayerName;
            var activeVessel = FlightGlobals.ActiveVessel;

            foreach (var updateLock in LockSystem.LockQuery.GetAllUpdateLocks(playerName))
            {
                Vessel vessel = null;
                for (var i = 0; i < FlightGlobals.VesselsLoaded.Count; i++)
                {
                    var loadedVessel = FlightGlobals.VesselsLoaded[i];
                    if (loadedVessel && loadedVessel.id == updateLock.VesselId)
                    {
                        vessel = loadedVessel;
                        break;
                    }
                }

                if (!vessel || activeVessel != null && vessel == activeVessel)
                    continue;

                // Do not send secondary physics for vessels another player is piloting — only they should stream
                // from ActiveVessel; otherwise we fight their lock state and spam wrong positions.
                if (LockSystem.LockQuery.ControlLockExists(vessel.id) &&
                    !LockSystem.LockQuery.ControlLockBelongsToPlayer(vessel.id, playerName))
                    continue;

                result.Add(vessel);
            }

            return result;
        }

        /// <summary>
        /// Return all the that we have the unloaded update lock ONLY.
        /// </summary>
        public static IEnumerable<Vessel> GetUnloadedSecondaryVessels()
        {
            //We don't need to check if vessel is in safety bubble as the update locks are updated accordingly
            return LockSystem.LockQuery.GetAllUnloadedUpdateLocks(SettingsSystem.CurrentSettings.PlayerName)
                .Select(l => FlightGlobals.VesselsUnloaded.FirstOrDefault(v => v && v.id == l.VesselId))
                .Where(v => v && (FlightGlobals.ActiveVessel == null || v != FlightGlobals.ActiveVessel));
        }
    }
}
