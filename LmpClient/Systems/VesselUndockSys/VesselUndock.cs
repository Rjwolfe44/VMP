using LmpClient.Extensions;
using LmpClient.Systems.Lock;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;
using System;

namespace LmpClient.Systems.VesselUndockSys
{
    /// <summary>
    /// Class that maps a message class to a system class. This way we avoid the message caching issues
    /// </summary>
    public class VesselUndock
    {
        #region Fields and Properties

        public double GameTime;
        public Guid VesselId;

        public uint PartFlightId;
        public Guid NewVesselId;

        public DockedVesselInfo DockedInfo;

        #endregion

        public void ProcessUndock()
        {
            if (!VesselCommon.DoVesselChecks(VesselId))
                return;

            var vessel = FlightGlobals.FindVessel(VesselId);
            if (vessel == null) return;

            var protoPart = vessel.protoVessel.GetProtoPart(PartFlightId);
            if (protoPart != null)
            {
                if (protoPart.partRef)
                {
                    var vesselBefore = protoPart.partRef.vessel;
                    VesselUndockSystem.Singleton.ManuallyUndockingVesselId = vesselBefore.id;
                    VesselUndockSystem.Singleton.IgnoreEvents = true;

                    try
                    {
                        try
                        {
                            protoPart.partRef.Undock(DockedInfo);
                        }
                        catch (Exception e)
                        {
                            //Same reasoning as VesselDecouple: an exception thrown by a part module while the game undocks must not
                            //disconnect the player. If the part is still on the same vessel the split did not happen.
                            LunaLog.LogWarning($"[VMP]: Undocking part {PartFlightId} of vessel {VesselId} threw, continuing: {e.GetType().Name}: {e.Message}");

                            if (!protoPart.partRef || protoPart.partRef.vessel == vesselBefore)
                                return;
                        }

                        protoPart.partRef.vessel.id = NewVesselId;

                        LockSystem.Singleton.FireVesselLocksEvents(NewVesselId);

                        //Forcefully set the vessel as immortal
                        protoPart.partRef.vessel.SetImmortal(true);

                        VesselPositionSystem.Singleton.ForceUpdateVesselPosition(NewVesselId);
                    }
                    finally
                    {
                        VesselUndockSystem.Singleton.IgnoreEvents = false;
                        VesselUndockSystem.Singleton.ManuallyUndockingVesselId = Guid.Empty;
                    }
                }
            }
        }
    }
}
