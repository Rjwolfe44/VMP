using LmpClient.Extensions;
using LmpClient.Systems.Lock;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;
using System;

namespace LmpClient.Systems.VesselDecoupleSys
{
    /// <summary>
    /// Class that maps a message class to a system class. This way we avoid the message caching issues
    /// </summary>
    public class VesselDecouple
    {
        #region Fields and Properties

        public double GameTime;
        public Guid VesselId;
        public uint PartFlightId;
        public float BreakForce;
        public Guid NewVesselId;

        #endregion

        public void ProcessDecouple()
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
                    VesselDecoupleSystem.Singleton.ManuallyDecouplingVesselId = vesselBefore.id;
                    VesselDecoupleSystem.Singleton.IgnoreEvents = true;

                    try
                    {
                        try
                        {
                            protoPart.partRef.decouple(BreakForce);
                        }
                        catch (Exception e)
                        {
                            //A part module of a mod (for example ModuleStructuralNode.CheckDisplay) can throw while the game runs the
                            //decoupling. That is the game's problem, not a reason to drop the player from the server: the
                            //Unity update loop treats any exception that escapes here as fatal and disconnects.
                            LunaLog.LogWarning($"[VMP]: Decoupling part {PartFlightId} of vessel {VesselId} threw, continuing: {e.GetType().Name}: {e.Message}");

                            //If the part is still on the same vessel the split did not happen: do not rename that vessel
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
                        VesselDecoupleSystem.Singleton.IgnoreEvents = false;
                        VesselDecoupleSystem.Singleton.ManuallyDecouplingVesselId = Guid.Empty;
                    }
                }
            }
        }
    }
}
