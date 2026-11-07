using LmpClient.Base;
using LmpClient.Extensions;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using LmpCommon.Scenario;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Expansions;
using UniLinq;

namespace LmpClient.Systems.Scenario
{
    public class ScenarioSystem : MessageSystem<ScenarioSystem, ScenarioMessageSender, ScenarioMessageHandler>
    {
        #region Fields

        private ConcurrentDictionary<string, string> CheckData { get; } = new ConcurrentDictionary<string, string>();
        public ConcurrentQueue<ScenarioEntry> ScenarioQueue { get; private set; } = new ConcurrentQueue<ScenarioEntry>();

        // ReSharper disable once InconsistentNaming
        private static readonly ConcurrentDictionary<string, Type> _allScenarioTypesInAssemblies = new ConcurrentDictionary<string, Type>();
        private static ConcurrentDictionary<string, Type> AllScenarioTypesInAssemblies
        {
            get
            {
                if (!_allScenarioTypesInAssemblies.Any())
                {
                    var scenarioTypes = AssemblyLoader.loadedAssemblies
                        .SelectMany(a => a.assembly.GetLoadableTypes())
                        .Where(s => s.IsSubclassOf(typeof(ScenarioModule)) && !_allScenarioTypesInAssemblies.ContainsKey(s.Name));

                    foreach (var scenarioType in scenarioTypes)
                        _allScenarioTypesInAssemblies.TryAdd(scenarioType.Name, scenarioType);
                }

                return _allScenarioTypesInAssemblies;
            }
        }

        /// <summary>One scenario send at a time; a full send asked for meanwhile runs at the next 1 s tick.</summary>
        private readonly ScenarioSendCoordinator _sendCoordinator = new ScenarioSendCoordinator();

        /// <summary>The only module a Kerbal Konstructs shared-static flush saves and sends.</summary>
        private const string KerbalKonstructsModule = "KerbalKonstructsSettings";
        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(ScenarioSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();
            //Run it every 30 seconds
            SetupRoutine(new RoutineDefinition(30000, RoutineExecution.Update, SendScenarioModules));
            //Every second: a full send that had to wait for a running one, else a Kerbal Konstructs shared-static flush
            //(only that module, at most every 5 s) so an edit saved for sharing goes out within about a second.
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, SendPendingScenarioModules));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            CheckData.Clear();
            _sendCoordinator.Reset();
            ScenarioQueue = new ConcurrentQueue<ScenarioEntry>();
            AllScenarioTypesInAssemblies.Clear();
            //Disconnected: shared Kerbal Konstructs statics of this server must not reach the next one.
            KerbalKonstructsShare.KkSharedStaticsSync.ResetSession();
        }

        #endregion

        #region Public methods

        public void LoadMissingScenarioDataIntoGame()
        {
            LoadMissingScenarioDataIntoGame(_ => true);
        }

        public void LoadMissingScenarioDataIntoGame(IEnumerable<string> scenarioNames)
        {
            if (scenarioNames == null) return;

            var requiredScenarioNames = new HashSet<string>(scenarioNames);
            if (requiredScenarioNames.Count == 0) return;

            LoadMissingScenarioDataIntoGame(s => requiredScenarioNames.Contains(s.ModuleType.Name));
        }

        private void LoadMissingScenarioDataIntoGame(Func<KSPScenarioType, bool> scenarioFilter)
        {
            //ResourceScenario.Instance.Load();

            var validScenarios = KSPScenarioType.GetAllScenarioTypesInAssemblies()
                .Where(s => !HighLogic.CurrentGame.scenarios.Exists(psm => psm.moduleName == s.ModuleType.Name) 
                            && LoadModuleByGameMode(s)
                            && IsDlcScenarioInstalled(s.ModuleType.Name)
                            && scenarioFilter(s));

            foreach (var validScenario in validScenarios)
            {
                LunaLog.Log($"[VMP]: Creating new scenario module {validScenario.ModuleType.Name}");
                HighLogic.CurrentGame.AddProtoScenarioModule(validScenario.ModuleType, validScenario.ScenarioAttributes.TargetScenes);
            }
        }

        private void SendPendingScenarioModules()
        {
            if (!Enabled)
                return;
            switch (_sendCoordinator.Tick(KerbalKonstructsShare.KkSharedStaticsSync.TakeFlushRequest))
            {
                case ScenarioSendCoordinator.TickAction.Full:
                    StartSend(ScenarioRunner.GetLoadedModules().Where(s => s != null));
                    break;
                case ScenarioSendCoordinator.TickAction.SingleModule:
                    StartSend(ScenarioRunner.GetLoadedModules().Where(s => s != null && s.GetType().Name == KerbalKonstructsModule));
                    break;
            }
        }

        /// <summary>
        /// Check if the scenario has changed and sends it to the server. While another send runs, this one is started by
        /// the next 1 s tick instead (two sends never run at once).
        /// </summary>
        public void SendScenarioModules()
        {
            if (Enabled && _sendCoordinator.TryBeginFull())
                StartSend(ScenarioRunner.GetLoadedModules().Where(s => s != null));
        }

        /// <summary>Caller owns the send (coordinator). Saves on this (main) thread, serializes and sends on a task.</summary>
        private void StartSend(IEnumerable<ScenarioModule> modules)
        {
            try
            {
                var nodes = ParseModulesToConfigNodes(modules);
                TaskFactory.StartNew(() => SendModulesConfigNodes(nodes));
            }
            catch (Exception e)
            {
                _sendCoordinator.End();
                LunaLog.LogError($"Error while trying to send the scenario modules!. Details {e}");
            }
        }

        /// <summary>
        /// This transforms the scenarioModule to a config node. We cannot do this in another thread as Lingoona 
        /// is called sometimes and that makes a hard crash
        /// </summary>
        private static List<Tuple<string, ConfigNode>> ParseModulesToConfigNodes(IEnumerable<ScenarioModule> modules)
        {
            var scenariosConfigNodes = new List<Tuple<string, ConfigNode>>();
            var totalStart = StallWatch.Start();
            foreach (var scenarioModule in modules)
            {
                var scenarioType = scenarioModule.GetType().Name;

                if (IgnoredScenarios.IgnoreSend.Contains(scenarioType))
                    continue;

                if (!IsScenarioModuleAllowed(scenarioType))
                    continue;

                var configNode = new ConfigNode();
                var moduleStart = StallWatch.Start();
                scenarioModule.Save(configNode);
                StallWatch.End(moduleStart, "scenario module " + scenarioType + " Save", 100);

                scenariosConfigNodes.Add(new Tuple<string, ConfigNode>(scenarioType, configNode));
            }

            StallWatch.End(totalStart, "scenario send, saving all modules", 250);
            return scenariosConfigNodes;
        }

        /// <summary>
        /// Sends the parsed config nodes to the server after doing basic checks. Works only on this send's own lists
        /// (ScenarioSendPlanner); a module counts as sent (CheckData) only after the message went to the sender.
        /// </summary>
        private void SendModulesConfigNodes(List<Tuple<string, ConfigNode>> nodes)
        {
            try
            {
                var serialized = nodes.Select(n => new KeyValuePair<string, byte[]>(n.Item1, n.Item2.Serialize())).ToList();
                var batch = ScenarioSendPlanner.Plan(serialized, CheckData, module => LunaLog.Log($"[VMP]: Error writing scenario data for {module}"));
                if (batch.Count > 0)
                {
                    MessageSender.SendScenarioModuleData(batch.Names, batch.Data);
                    ScenarioSendPlanner.Commit(batch, CheckData);
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"Error while trying to send the scenario modules!. Details {e}");
            }
            finally
            {
                _sendCoordinator.End();
            }
        }

        public void LoadScenarioDataIntoGame()
        {
            while (ScenarioQueue.TryDequeue(out var scenarioEntry))
            {
                var psm = new ProtoScenarioModule(scenarioEntry.ScenarioNode);
                if (IsScenarioModuleAllowed(psm.moduleName) && !IgnoredScenarios.IgnoreReceive.Contains(psm.moduleName))
                {
                    LunaLog.Log($"[VMP]: Loading {psm.moduleName} scenario data");
                    HighLogic.CurrentGame.scenarios.Add(psm);
                }
                else
                {
                    LunaLog.Log($"[VMP]: Skipping {psm.moduleName} scenario data in {SettingsSystem.ServerSettings.GameMode} mode");
                }
            }
        }

        #endregion

        #region Private methods

        private static bool LoadModuleByGameMode(KSPScenarioType validScenario)
        {
            switch (HighLogic.CurrentGame.Mode)
            {
                case Game.Modes.CAREER:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewCareerGames);
                case Game.Modes.SCIENCE_SANDBOX:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewScienceSandboxGames);
                case Game.Modes.SANDBOX:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewSandboxGames);
            }
            return false;
        }

        private static bool IsDlcScenarioInstalled(string scenarioName)
        {
            if (scenarioName == "DeployedScience" && !ExpansionsLoader.IsExpansionInstalled("Serenity"))
                return false;

            return true;
        }

        private static bool IsScenarioModuleAllowed(string scenarioName)
        {
            if (string.IsNullOrEmpty(scenarioName)) return false;

            if (scenarioName == "DeployedScience" && !ExpansionsLoader.IsExpansionInstalled("Serenity"))
                return false;

            if (!IsDlcScenarioInstalled(scenarioName))
                return false;

            if (!AllScenarioTypesInAssemblies.ContainsKey(scenarioName)) return false; //Module missing

            var scenarioType = AllScenarioTypesInAssemblies[scenarioName];

            var scenarioAttributes = (KSPScenario[])scenarioType.GetCustomAttributes(typeof(KSPScenario), true);
            if (scenarioAttributes.Length > 0)
            {
                var attribute = scenarioAttributes[0];
                var protoAllowed = false;
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
                {
                    protoAllowed = attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingCareerGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewCareerGames);
                }
                if (HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX)
                {
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingScienceSandboxGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewScienceSandboxGames);
                }
                if (HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX)
                {
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingSandboxGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewSandboxGames);
                }
                return protoAllowed;
            }

            //Scenario is not marked with KSPScenario - let's load it anyway.
            return true;
        }

        #endregion
    }
}
