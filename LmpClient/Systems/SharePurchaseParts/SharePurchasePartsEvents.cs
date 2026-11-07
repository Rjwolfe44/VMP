using LmpClient.Base;
using LmpClient.Systems.PersistentSync;
using LmpCommon.PersistentSync;

namespace LmpClient.Systems.SharePurchaseParts
{
    public class SharePurchasePartsEvents : SubSystem<SharePurchasePartsSystem>
    {
        public void PartPurchased(AvailablePart part)
        {
            if (System.IgnoreEvents) return;

            //Persistent sync owns part purchases: the server ignores this legacy message (and logs an error for every part,
            //20 lines in one second when a tech with 20 parts is researched), so do not send it.
            if (PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.PartPurchases)) return;

            var techState = ResearchAndDevelopment.Instance.GetTechState(part.TechRequired);
            if (techState != null)
            {
                LunaLog.Log($"Relaying part purchased on tech: {techState.techID}; part: {part.name}");
                System.MessageSender.SendPartPurchasedMessage(techState.techID, part.name);
            }
        }
    }
}
