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
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<Guid, byte> OwnVesselIds = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Remembers a vessel this client is responsible for: one it created with its own decouple or undock (dropped
        /// boosters, spent stages, an undocked probe) or one it piloted this session (left behind after EVA or a vessel
        /// switch). While we hold its update lock and nobody pilots it we simulate it, so <see cref="DoVesselChecks"/> must
        /// not take it for another player's craft whose control lock has not arrived yet.
        /// </summary>
        public static void MarkOwnVessel(Guid vesselId) => OwnVesselIds[vesselId] = 0;

        /// <summary>
        /// Another player took the controls: from now on they are the authority for this vessel.
        /// </summary>
        public static void ForgetOwnVessel(Guid vesselId) => OwnVesselIds.TryRemove(vesselId, out _);

        public static bool IsOwnVessel(Guid vesselId) => OwnVesselIds.ContainsKey(vesselId);

        public static void ClearOwnVessels() => OwnVesselIds.Clear();

        /// <summary>
        /// How far BEHIND real time we render remote vessels. Uses only the sender's ping so every
        /// observer agrees on the same render timestamp (critical for orbital docking where multiple
        /// players must see identical closing geometry). Local receive latency is absorbed by the
        /// interpolation catch-up controller via <see cref="VesselPositionUpdate.AdjustExtraInterpolationTimes"/>.
        /// </summary>
        public static float PositionAndFlightStateMessageOffsetSec(float senderPingSec)
        {
            var sendIntervalSec = SettingsSystem.ServerSettings.VesselUpdatesMsInterval / 1000f;
            var floor = Mathf.Max(0.100f, 2f * sendIntervalSec);
            return Mathf.Clamp(senderPingSec, floor, 2.5f);
        }

        /// <summary>
        /// Effective position send interval for the active vessel given proximity / range tiers.
        /// </summary>
        public static int GetEffectiveVesselUpdatesMsInterval()
        {
            var settings = SettingsSystem.ServerSettings;
            var active = FlightGlobals.ActiveVessel;
            if (active != null && settings.ProximityHighRateEnabled &&
                OtherLoadedVesselWithinRange(active, settings.ProximityHighRateRangeMeters))
            {
                return Math.Max(1, settings.ProximityHighRateMsInterval);
            }

            if (PlayerVesselsNearby())
                return settings.VesselUpdatesMsInterval;

            if (PlayerVesselsOnSameBody())
                return (settings.VesselUpdatesMsInterval + settings.SecondaryVesselUpdatesMsInterval) / 2;

            return settings.SecondaryVesselUpdatesMsInterval;
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
            // Not for our own vessels (see MarkOwnVessel): a dropped booster never gets a control lock and a craft we switched
            // away from has none left, so treating them as remote made them immortal and skipped FlightIntegrator (no
            // gravity, drag or thrust).
            if (!IsOwnVessel(vesselId) &&
                LockSystem.LockQuery.UpdateLockBelongsToPlayer(vesselId, SettingsSystem.CurrentSettings.PlayerName) &&
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
