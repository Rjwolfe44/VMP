using HarmonyLib;
using KSPAchievements;
using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Systems.PersistentSync;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareProgress;
using LmpClient.Systems.ShareReputation;
using LmpClient.Systems.ShareScience;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using LmpCommon.PersistentSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LmpClient.Systems.ShareAchievements
{
    public class ShareAchievementsSystem : ShareProgressBaseSystem<ShareAchievementsSystem, ShareAchievementsMessageSender, ShareAchievementsMessageHandler>
    {
        public override string SystemName { get; } = nameof(ShareAchievementsSystem);

        private ShareAchievementsEvents ShareAchievementsEvents { get; } = new ShareAchievementsEvents();

        protected override bool ShareSystemReady => ProgressTracking.Instance != null;

        /// <summary>
        /// Unused when <see cref="ShareProgressBaseSystem.UseSessionApplicabilityInsteadOfGameModeMask"/> is true; kept for API completeness.
        /// </summary>
        protected override GameMode RelevantGameModes => GameMode.Career | GameMode.Science;

        protected override bool UseSessionApplicabilityInsteadOfGameModeMask => true;

        protected override bool IsShareSystemApplicableForSession()
        {
            var caps = PersistentSyncSessionCapabilitiesFactory.CreateForCurrentSession();
            return PersistentSyncDomainApplicability.IsDomainApplicableForShareProducer(
                PersistentSyncDomainId.Achievements,
                SettingsSystem.ServerSettings.GameMode,
                in caps);
        }

        private ConfigNode _lastAchievements;

        public bool Reverting { get; set; }

        /// <summary>
        /// The four record nodes. KSP pays a reward each time a record passes the next interval, and only tells us
        /// (OnProgressReached) when a new vessel first beats the record, so the value the server held stopped near where
        /// each flight started. The next session loaded that stale record and was paid for every interval between it and
        /// the real record again. These nodes are re-sent while they keep improving (see <see cref="FlushDirtyRecords"/>).
        /// </summary>
        private static readonly string[] RecordNodeIds = { "RecordsSpeed", "RecordsAltitude", "RecordsDistance", "RecordsDepth" };

        private const int RecordFlushIntervalMs = 10000;

        private readonly HashSet<string> _dirtyRecords = new HashSet<string>();

        public static bool IsRecordNode(string id) => Array.IndexOf(RecordNodeIds, id) >= 0;

        protected override void OnEnabled()
        {
            base.OnEnabled();

            GameEvents.OnProgressReached.Add(ShareAchievementsEvents.AchievementReached);
            GameEvents.OnProgressComplete.Add(ShareAchievementsEvents.AchievementCompleted);
            GameEvents.OnProgressAchieved.Add(ShareAchievementsEvents.AchievementAchieved);

            RevertEvent.onRevertingToLaunch.Add(ShareAchievementsEvents.RevertingDetected);
            RevertEvent.onReturningToEditor.Add(ShareAchievementsEvents.RevertingToEditorDetected);
            GameEvents.onLevelWasLoadedGUIReady.Add(ShareAchievementsEvents.LevelLoaded);
            GameEvents.onGameSceneLoadRequested.Add(ShareAchievementsEvents.SceneLoadRequested);

            if (IsShareSystemApplicableForSession())
            {
                SetupRoutine(new RoutineDefinition(RecordFlushIntervalMs, RoutineExecution.Update, FlushDirtyRecords));
            }
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();

            //Always try to remove the event, as when we disconnect from a server the server settings will get the default values
            GameEvents.OnProgressReached.Remove(ShareAchievementsEvents.AchievementReached);
            GameEvents.OnProgressComplete.Remove(ShareAchievementsEvents.AchievementCompleted);
            GameEvents.OnProgressAchieved.Remove(ShareAchievementsEvents.AchievementAchieved);

            RevertEvent.onRevertingToLaunch.Remove(ShareAchievementsEvents.RevertingDetected);
            RevertEvent.onReturningToEditor.Remove(ShareAchievementsEvents.RevertingToEditorDetected);
            GameEvents.onLevelWasLoadedGUIReady.Remove(ShareAchievementsEvents.LevelLoaded);
            GameEvents.onGameSceneLoadRequested.Remove(ShareAchievementsEvents.SceneLoadRequested);

            _lastAchievements = null;
            _dirtyRecords.Clear();
            Reverting = false;
        }

        public void MarkRecordDirty(string id)
        {
            _dirtyRecords.Add(id);
        }

        /// <summary>Sends the record nodes that improved since the last send. Called every few seconds and before a scene change.</summary>
        public void FlushDirtyRecords()
        {
            if (_dirtyRecords.Count == 0) return;
            if (ProgressTracking.Instance == null || !PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Achievements)) return;

            var nodes = new List<ProgressNode>();
            foreach (var id in _dirtyRecords)
            {
                var node = ProgressTracking.Instance.achievementTree[id];
                if (node != null && node.IsReached)
                {
                    nodes.Add(node);
                }
            }

            _dirtyRecords.Clear();
            if (nodes.Count > 0)
            {
                MessageSender.SendAchievementsMessages(nodes);
            }
        }

        public override void SaveState()
        {
            base.SaveState();
            _lastAchievements = new ConfigNode();
            if (ProgressTracking.Instance) ProgressTracking.Instance.achievementTree.Save(_lastAchievements);
        }

        public override void RestoreState()
        {
            base.RestoreState();
            if (ProgressTracking.Instance == null)
            {
                //The instance will be null when reverting to editor so we must restore the old data into the proto
                //This happens because in ProgressTracking class, the KSPScenario attribute doesn't include 
                //GameScenes.Editor into it's values :(
                var achievementsScn = HighLogic.CurrentGame.scenarios.FirstOrDefault(s => s.moduleName == "ProgressTracking");
                var moduleValues = Traverse.Create(achievementsScn).Field<ConfigNode>("moduleValues").Value;

                var progressNode = moduleValues.GetNode("Progress");
                progressNode.ClearNodes();

                foreach (var node in _lastAchievements.GetNodes())
                {
                    progressNode.AddNode(node);
                }
            }
            else
            {
                ProgressTracking.Instance.achievementTree.Load(_lastAchievements);
            }
        }

        public void ApplyAchievementSnapshotTree(ConfigNode snapshotTree, string source)
        {
            if (snapshotTree == null)
            {
                return;
            }

            var suppressFunds = PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Funds) && Funding.Instance != null;
            var suppressScience = PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Science) && ResearchAndDevelopment.Instance != null;
            var suppressReputation = PersistentSyncSystem.IsLiveForDomain(PersistentSyncDomainId.Reputation) && Reputation.Instance != null;

            StartIgnoringEvents();
            if (suppressFunds)
                ShareFundsSystem.Singleton.StartIgnoringEvents();
            if (suppressScience)
                ShareScienceSystem.Singleton.StartIgnoringEvents();
            if (suppressReputation)
                ShareReputationSystem.Singleton.StartIgnoringEvents();
            try
            {
                DropRecordsThatWouldRollBackLocalRecords(snapshotTree);
                ProgressTracking.Instance.achievementTree.Load(snapshotTree);
                LunaLog.Log($"Achievements snapshot applied from {source}");
            }
            finally
            {
                if (suppressFunds)
                    ShareFundsSystem.Singleton.StopIgnoringEvents(true);
                if (suppressScience)
                    ShareScienceSystem.Singleton.StopIgnoringEvents(true);
                if (suppressReputation)
                    ShareReputationSystem.Singleton.StopIgnoringEvents(true);
                StopIgnoringEvents();
            }
        }

        /// <summary>
        /// Loading a record node sets the local record to the snapshot's value and recomputes the next reward
        /// threshold from it. A snapshot is built from what the server last received, which can be up to a few seconds
        /// behind a record this client is setting right now; loading it would hand the same intervals out again. So a
        /// record node is only loaded when the snapshot is at least as far along as the local one.
        /// </summary>
        private static void DropRecordsThatWouldRollBackLocalRecords(ConfigNode snapshotTree)
        {
            foreach (var id in RecordNodeIds)
            {
                var snapshotNode = snapshotTree.GetNode(id);
                if (snapshotNode == null) continue;

                var localRecord = GetLocalRecord(ProgressTracking.Instance.achievementTree[id]);
                if (!localRecord.HasValue) continue;

                if (snapshotNode.HasValue("record") &&
                    double.TryParse(snapshotNode.GetValue("record"), NumberStyles.Float, CultureInfo.InvariantCulture, out var snapshotRecord) &&
                    snapshotRecord >= localRecord.Value)
                {
                    continue;
                }

                snapshotTree.RemoveNode(id);
            }
        }

        private static double? GetLocalRecord(ProgressNode node)
        {
            switch (node)
            {
                case RecordsSpeed speed: return speed.record;
                case RecordsAltitude altitude: return altitude.record;
                case RecordsDistance distance: return distance.record;
                case RecordsDepth depth: return depth.record;
                default: return null;
            }
        }
    }
}
