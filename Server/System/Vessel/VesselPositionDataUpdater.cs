using LmpCommon.Message.Data.Vessel;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;

namespace Server.System.Vessel
{
    /// <summary>
    /// We try to avoid working with protovessels as much as possible as they can be huge files.
    /// This class patches the vessel file with the information messages we receive about a position and other vessel properties.
    /// This way we send the whole vessel definition only when there are parts that have changed 
    /// </summary>
    public partial class VesselDataUpdater
    {
        /// <summary>
        /// Update the vessel files with position data max at a 2,5 seconds interval
        /// </summary>
        private const int FilePositionUpdateIntervalMs = 2500;

        /// <summary>
        /// Avoid updating the vessel files so often as otherwise the server will lag a lot!
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, DateTime> LastPositionUpdateDictionary = new ConcurrentDictionary<Guid, DateTime>();

        /// <summary>
        /// We received a position information from a player
        /// Then we rewrite the vesselproto with that last information so players that connect later receive an updated vesselproto
        /// </summary>
        public static void WritePositionDataToFile(VesselBaseMsgData message)
        {
            if (!(message is VesselPositionMsgData msgData)) return;
            if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;

            ApplyOrbitalBodyNameFromPositionMessage(msgData);

            if (!LastPositionUpdateDictionary.TryGetValue(msgData.VesselId, out var lastUpdated) || (DateTime.Now - lastUpdated).TotalMilliseconds > FilePositionUpdateIntervalMs)
            {
                LastPositionUpdateDictionary.AddOrUpdate(msgData.VesselId, DateTime.Now, (key, existingVal) => DateTime.Now);

                // Capture the delta bitmask now so the worker task respects which fields
                // were actually present on the wire.  Field groups whose bit is clear may
                // contain stale or default values from the pooled message instance and
                // must NOT overwrite the persistent vessel store.
                var fields = msgData.DeltaFields;

                Task.Run(() =>
                {
                    lock (Semaphore.GetOrAdd(msgData.VesselId, new object()))
                    {
                        if (!VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var vessel)) return;

                        if ((fields & PositionDeltaFields.LatLonAlt) != 0)
                        {
                            vessel.Fields.Update("lat", msgData.LatLonAlt[0].ToString(CultureInfo.InvariantCulture));
                            vessel.Fields.Update("lon", msgData.LatLonAlt[1].ToString(CultureInfo.InvariantCulture));
                            vessel.Fields.Update("alt", msgData.LatLonAlt[2].ToString(CultureInfo.InvariantCulture));
                        }

                        if ((fields & PositionDeltaFields.HeightFromTerrain) != 0)
                            vessel.Fields.Update("hgt", msgData.HeightFromTerrain.ToString(CultureInfo.InvariantCulture));

                        if ((fields & PositionDeltaFields.NormalVector) != 0)
                            vessel.Fields.Update("nrm", $"{msgData.NormalVector[0].ToString(CultureInfo.InvariantCulture)}," +
                                                        $"{msgData.NormalVector[1].ToString(CultureInfo.InvariantCulture)}," +
                                                        $"{msgData.NormalVector[2].ToString(CultureInfo.InvariantCulture)}");

                        if ((fields & PositionDeltaFields.SrfRelRotation) != 0)
                            vessel.Fields.Update("rot", $"{msgData.SrfRelRotation[0].ToString(CultureInfo.InvariantCulture)}," +
                                                        $"{msgData.SrfRelRotation[1].ToString(CultureInfo.InvariantCulture)}," +
                                                        $"{msgData.SrfRelRotation[2].ToString(CultureInfo.InvariantCulture)}," +
                                                        $"{msgData.SrfRelRotation[3].ToString(CultureInfo.InvariantCulture)}");

                        if ((fields & PositionDeltaFields.Orbit) != 0)
                        {
                            vessel.Orbit.Update("INC", msgData.Orbit[0].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("ECC", msgData.Orbit[1].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("SMA", msgData.Orbit[2].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("LAN", msgData.Orbit[3].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("LPE", msgData.Orbit[4].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("MNA", msgData.Orbit[5].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("EPH", msgData.Orbit[6].ToString(CultureInfo.InvariantCulture));
                            vessel.Orbit.Update("REF", msgData.Orbit[7].ToString(CultureInfo.InvariantCulture));
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Keep ORBIT/body current from every position message instead of waiting for the throttled full patch.
        /// </summary>
        private static void ApplyOrbitalBodyNameFromPositionMessage(VesselPositionMsgData msgData)
        {
            // Body name is only valid when the Body bit is set on the wire; otherwise the
            // pooled message instance may carry a stale name from a previous deserialize.
            if ((msgData.DeltaFields & PositionDeltaFields.Body) == 0)
                return;
            if (string.IsNullOrEmpty(msgData.BodyName))
                return;

            var lockObj = Semaphore.GetOrAdd(msgData.VesselId, new object());
            lock (lockObj)
            {
                if (!VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var vessel))
                    return;

                vessel.Orbit.Update("body", msgData.BodyName);
            }
        }
    }
}
