using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselImmortalSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;

namespace LmpClient.Systems.VesselDecoupleSys
{
    public class VesselDecoupleEvents : SubSystem<VesselDecoupleSystem>
    {
        public void DecoupleStart(Part part, float breakForce)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents || !part) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(part.vessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LunaLog.Log($"Detected decouple! Part: {part.partName} Vessel: {part.vessel.id}");
        }

        public void DecoupleComplete(Part part, float breakForce, Vessel originalVessel)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents || !part || !originalVessel) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(originalVessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LockSystem.Singleton.AcquireUnloadedUpdateLock(part.vessel.id, true, true);
            LockSystem.Singleton.AcquireUpdateLock(part.vessel.id, true, true);

            // We simulate what we drop: undo the immortal state onVesselCreate gave it before the lock existed (the immediate
            // lock above fires no lock event, so nothing else would re-check it until the server echoes the lock).
            VesselCommon.MarkOwnVessel(part.vessel.id);
            VesselImmortalSystem.Singleton.SetImmortalStateBasedOnLock(part.vessel);

            VesselPositionSystem.Singleton.MessageSender.SendVesselPositionUpdate(part.vessel, true);

            LunaLog.Log($"Decouple complete! Part: {part.partName} Vessel: {part.vessel.id}");
            System.MessageSender.SendVesselDecouple(originalVessel, part.flightID, breakForce, part.vessel.id);
        }
    }
}
