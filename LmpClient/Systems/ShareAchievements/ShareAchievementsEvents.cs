using LmpClient.Base;

namespace LmpClient.Systems.ShareAchievements
{
    public class ShareAchievementsEvents : SubSystem<ShareAchievementsSystem>
    {
        #region EventHandlers

        public void AchievementReached(ProgressNode progressNode)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendAchievementsMessage(progressNode);
            LunaLog.Log($"Achievement reached: {progressNode.Id}");
        }

        public void AchievementCompleted(ProgressNode progressNode)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendAchievementsMessage(progressNode);
            LunaLog.Log($"Achievement completed: {progressNode.Id}");
        }

        public void AchievementAchieved(ProgressNode progressNode)
        {
            //This event is triggered to often (always if some speed or distance record changes), so it is not sent as it
            //happens. The record nodes are marked and re-sent every few seconds (ShareAchievementsSystem.FlushDirtyRecords)
            //so the server keeps the real record; a stale one makes the next session pay the same intervals again.
            if (System.IgnoreEvents || progressNode == null) return;

            if (ShareAchievementsSystem.IsRecordNode(progressNode.Id))
            {
                System.MarkRecordDirty(progressNode.Id);
            }
        }

        public void SceneLoadRequested(GameScenes data)
        {
            if (System.IgnoreEvents) return;

            System.FlushDirtyRecords();
        }

        public void RevertingDetected()
        {
            System.Reverting = true;
            System.StartIgnoringEvents();
        }

        public void RevertingToEditorDetected(EditorFacility data)
        {
            System.Reverting = true;
            System.StartIgnoringEvents();
        }

        public void LevelLoaded(GameScenes data)
        {
            if (System.Reverting)
            {
                System.Reverting = false;
                System.StopIgnoringEvents(true);
            }
        }

        #endregion
    }
}
