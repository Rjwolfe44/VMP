using HarmonyLib;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Extensions;
using LmpClient.Network;
using LmpClient.Systems.PersistentSync;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Interface;
using LmpCommon.PersistentSync;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpClient.Systems.ShareAchievements
{
    public class ShareAchievementsMessageSender : SubSystem<ShareAchievementsSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<ShareProgressCliMsg>(msg)));
        }

        /// <summary>
        /// Sends several achievements in one intent (the periodic record flush), so the server answers with one snapshot
        /// instead of one per node. Does nothing unless persistent sync owns achievements.
        /// </summary>
        public void SendAchievementsMessages(IEnumerable<ProgressNode> achievements)
        {
            if (!PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Achievements)) return;

            var infos = new List<AchievementSnapshotInfo>();
            foreach (var achievement in achievements)
            {
                var foundNode = FindTreeNode(achievement);
                if (foundNode == null) continue;

                var configNode = ConvertAchievementToConfigNode(foundNode);
                if (configNode == null) continue;

                var data = configNode.Serialize();
                infos.Add(new AchievementSnapshotInfo { Id = foundNode.Id, Data = data, NumBytes = data.Length });
            }

            if (infos.Count == 0) return;

            PersistentSyncSystem.Singleton.MessageSender.SendAchievementsIntent(infos.ToArray(), $"AchievementUpdate:{string.Join(",", infos.Select(i => i.Id))}");
        }

        private static ProgressNode FindTreeNode(ProgressNode achievement)
        {
            //We only send the ProgressNodes that are CelestialBodySubtree
            var foundNode = ProgressTracking.Instance.FindNode(achievement.Id);
            if (foundNode == null)
            {
                var traverse = new Traverse(achievement).Field<CelestialBody>("body");

                var body = traverse.Value ? traverse.Value.name : null;
                if (body != null)
                {
                    foundNode = ProgressTracking.Instance.FindNode(body);
                }
            }

            return foundNode;
        }

        public void SendAchievementsMessage(ProgressNode achievement)
        {
            var foundNode = FindTreeNode(achievement);

            if (foundNode != null)
            {
                var configNode = ConvertAchievementToConfigNode(foundNode);
                if (configNode == null) return;

                if (PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Achievements))
                {
                    PersistentSyncSystem.Singleton.MessageSender.SendAchievementsIntent(new[]
                    {
                        new AchievementSnapshotInfo
                        {
                            Id = foundNode.Id,
                            Data = configNode.Serialize(),
                            NumBytes = configNode.Serialize().Length
                        }
                    }, $"AchievementUpdate:{foundNode.Id}");
                    return;
                }

                //Build the packet and send it.
                var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<ShareProgressAchievementsMsgData>();
                msgData.Id = foundNode.Id;
                msgData.Data = configNode.Serialize();
                msgData.NumBytes = msgData.Data.Length;
                System.MessageSender.SendMessage(msgData);
            }
        }

        private static ConfigNode ConvertAchievementToConfigNode(ProgressNode achievement)
        {
            var configNode = new ConfigNode(achievement.Id);
            try
            {
                achievement.Save(configNode);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Error while saving achievement: {e}");
                return null;
            }

            return configNode;
        }
    }
}
