using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;

namespace LmpClient.Systems.VesselActionGroupSys
{
    public class VesselActionGroupEvents : SubSystem<VesselActionGroupSystem>
    {
        public void ActionGroupFired(Vessel vessel, KSPActionGroup actionGroup, bool value)
        {
            if (LockSystem.LockQuery.UpdateLockExists(vessel.id) &&
                !LockSystem.LockQuery.UpdateLockBelongsToPlayer(vessel.id, SettingsSystem.CurrentSettings.PlayerName))
                return;

            //Send the vessel whose action group fired, not the vessel the player is flying: they differ when a part,
            //a probe's own autopilot or a mod fires a group on a loaded vessel that is not the active one.
            System.MessageSender.SendVesselActionGroup(vessel, actionGroup, value);
        }
    }
}
