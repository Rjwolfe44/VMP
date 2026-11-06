using KSP.UI;
using KSP.UI.Screens;
using LmpClient;
using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Utilities;
using System.Collections.Concurrent;
using System.Reflection;
using UnityEngine;

namespace LmpClient.Systems.KerbalSys
{
    /// <summary>
    /// System that handles the kerbals between client and the server.
    /// </summary>
    public class KerbalSystem : MessageSystem<KerbalSystem, KerbalMessageSender, KerbalMessageHandler>
    {
        #region Fields

        public ConcurrentQueue<string> KerbalsToRemove { get; private set; } = new ConcurrentQueue<string>();
        public ConcurrentQueue<ConfigNode> KerbalsToProcess { get; private set; } = new ConcurrentQueue<ConfigNode>();

        private const double MinCrewCheckIntervalMs = 10000;

        private bool _pendingCrewCheck;
        private bool _pendingCrewChanged;
        private long _lastCrewCheck;
        public bool KerbalSystemReady => Enabled && HighLogic.CurrentGame?.CrewRoster != null;

        public KerbalEvents KerbalEvents { get; } = new KerbalEvents();

        private static AstronautComplex _astronautComplex;
        public AstronautComplex AstronautComplex
        {
            get
            {
                if (_astronautComplex == null)
                {
                    _astronautComplex = Object.FindObjectOfType<AstronautComplex>();
                }
                return _astronautComplex;
            }
        }

        #region Reflection fields

        private static readonly FieldInfo KerbalStatusField = typeof(ProtoCrewMember).GetField("_rosterStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo KerbalTypeField = typeof(ProtoCrewMember).GetField("_type", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo CreateAvailableList = typeof(AstronautComplex).GetMethod("CreateAvailableList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo CreateAssignedList = typeof(AstronautComplex).GetMethod("CreateAssignedList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo CreateKiaList = typeof(AstronautComplex).GetMethod("CreateKiaList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo InitiateGui = typeof(AstronautComplex).GetMethod("InitiateGUI", BindingFlags.NonPublic | BindingFlags.Instance);

        #endregion

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(KerbalSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, RemoveQueuedKerbals));
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, LoadKerbals));
            SetupRoutine(new RoutineDefinition(250, RoutineExecution.Update, ProcessPendingCrewCheck));

            VesselAssemblyEvent.onVesselValidationBeforAssembly.Add(KerbalEvents.ValidationBeforeAssembly);
            GameEvents.onKerbalLevelUp.Add(KerbalEvents.KerbalLevelUp);
            GameEvents.onKerbalStatusChange.Add(KerbalEvents.StatusChange);
            GameEvents.onKerbalTypeChange.Add(KerbalEvents.TypeChange);
            RevertEvent.onReturningToEditor.Add(KerbalEvents.ReturningToEditor);
            RemoveEvent.onLmpTerminatedVessel.Add(KerbalEvents.OnVesselTerminated);
            RemoveEvent.onLmpRecoveredVessel.Add(KerbalEvents.OnVesselRecovered);
            RemoveEvent.onLmpDestroyVessel.Add(KerbalEvents.OnVesselWillDestroy);

            VesselLoadEvent.onLmpVesselLoaded.Add(KerbalEvents.OnVesselLoaded);
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            KerbalsToRemove = new ConcurrentQueue<string>();
            KerbalsToProcess = new ConcurrentQueue<ConfigNode>();
            _pendingCrewCheck = false;
            _pendingCrewChanged = false;
            VesselAssemblyEvent.onVesselValidationBeforAssembly.Remove(KerbalEvents.ValidationBeforeAssembly);
            GameEvents.onKerbalStatusChange.Remove(KerbalEvents.StatusChange);
            GameEvents.onKerbalTypeChange.Remove(KerbalEvents.TypeChange);
            RevertEvent.onReturningToEditor.Remove(KerbalEvents.ReturningToEditor);
            RemoveEvent.onLmpTerminatedVessel.Remove(KerbalEvents.OnVesselTerminated);
            RemoveEvent.onLmpRecoveredVessel.Remove(KerbalEvents.OnVesselRecovered);
            RemoveEvent.onLmpDestroyVessel.Remove(KerbalEvents.OnVesselWillDestroy);

            VesselLoadEvent.onLmpVesselLoaded.Remove(KerbalEvents.OnVesselLoaded);
        }

        #endregion

        #region Public

        /// <summary>
        /// Load all the received kerbals from the server into the game
        /// This should be called before the game starts as otherwise loading vessels with crew will fail
        /// </summary>
        public void LoadKerbalsIntoGame()
        {
            ProcessKerbalQueue();
        }

        /// <summary>
        /// Sets the kerbal status without triggering the event (usefull when receiveing kerbals from other clients)
        /// </summary>
        public void SetKerbalStatusWithoutTriggeringEvent(ProtoCrewMember crew, ProtoCrewMember.RosterStatus newStatus)
        {
            if (crew == null) return;

            KerbalStatusField?.SetValue(crew, newStatus);
        }

        /// <summary>
        /// Sets the kerbal type without triggering the event (usefull when receiveing kerbals from other clients)
        /// </summary>
        public void SetKerbalTypeWithoutTriggeringEvent(ProtoCrewMember crew, ProtoCrewMember.KerbalType newType)
        {
            if (crew == null) return;

            KerbalTypeField?.SetValue(crew, newType);
        }

#endregion

        #region Public

        /// <summary>
        /// Schedules one crew assignment check for a later routine tick, so a batch of vessel loads collapses into one check.
        /// <paramref name="crewChanged"/> says that the loaded vessel carries crew the roster does not already show as
        /// assigned to it (see <see cref="VesselCrewNeedsCheck"/>).
        /// </summary>
        public void RequestCrewCheck(bool crewChanged)
        {
            _pendingCrewCheck = true;
            _pendingCrewChanged |= crewChanged;
        }

        /// <summary>
        /// True when the roster does not already match the crew of this freshly loaded vessel: a crew member the roster
        /// does not know, a different ProtoCrewMember instance than the roster's, or one whose roster status is not
        /// Assigned. These are the cases where KerbalRoster.ValidateAssignments would change something for this vessel.
        /// Errs on the side of true.
        /// </summary>
        public static bool VesselCrewNeedsCheck(Vessel vessel)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            var protoVessel = vessel ? vessel.protoVessel : null;
            if (roster == null || protoVessel?.protoPartSnapshots == null)
                return true;

            foreach (var part in protoVessel.protoPartSnapshots)
            {
                if (part?.protoModuleCrew == null)
                    continue;

                for (var i = 0; i < part.protoModuleCrew.Count; i++)
                {
                    if (part.protoCrewNames == null || i >= part.protoCrewNames.Count || string.IsNullOrEmpty(part.protoCrewNames[i]))
                        return true;

                    var rosterEntry = roster[part.protoCrewNames[i]];
                    if (rosterEntry == null || !ReferenceEquals(rosterEntry, part.protoModuleCrew[i]) ||
                        rosterEntry.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Runs the crew assignment check once after vessel loads asked for it.
        /// </summary>
        private void ProcessPendingCrewCheck()
        {
            if (!_pendingCrewCheck || HighLogic.CurrentGame?.CrewRoster == null)
                return;

            // Vessels load a few per frame: wait until the batch is done, and never check more often than every
            // MinCrewCheckIntervalMs.
            if (VesselProtoSystem.Singleton?.HasVesselsDueToLoad == true || StallWatch.ElapsedMs(_lastCrewCheck) < MinCrewCheckIntervalMs)
                return;

            var crewChanged = _pendingCrewChanged;
            _pendingCrewCheck = false;
            _pendingCrewChanged = false;

            // While the player is flying, only check when a loaded vessel really brought crew the roster does not match.
            // Uncrewed loads (debris, probes, other players' boosters) cannot give ValidateAssignments anything to fix.
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && !crewChanged)
                return;

            var started = StallWatch.Start();
            ValidateCrewAssignments(HighLogic.CurrentGame);
            _lastCrewCheck = StallWatch.Start();
            StallWatch.End(started, "crew assignment check after vessel loads", 250);
        }

        /// <summary>
        /// The crew part of KSP's Game.Updated(), and nothing else.
        ///
        /// VMP used to call HighLogic.CurrentGame.Updated() after remote vessels loaded, only so the roster would learn which
        /// kerbals sit in those vessels. Read in KSP 1.12's Assembly-CSharp, Game.Updated(GameScenes) does five things:
        ///  1. in a scene with a planetarium (space center, tracking station, flight) it replaces flightState with a new
        ///     FlightState(), which snapshots every vessel in FlightGlobals;
        ///  2. it calls CrewRoster.ValidateAssignments(game), which points every crew seat at the roster's kerbal, marks a
        ///     kerbal found in a vessel as Assigned and an Assigned kerbal found in no vessel as Missing;
        ///  3. it saves every scenario module again (ScenarioRunner.GetUpdatedProtoModules -> each module's OnSave);
        ///  4. it refreshes version and start scene fields;
        ///  5. it fires GameEvents.onGameStateSave to every installed mod.
        /// Only 1 and 2 touch the crew. 3 to 5 prepare a save file that is never written here (every real save runs
        /// Game.Updated again), and on a large pack they can take minutes. So we run 1 and 2 exactly as stock does and
        /// skip the rest. Kerbal sync is unchanged: any status change ValidateAssignments makes fires the same
        /// onKerbalStatusChange as before.
        /// </summary>
        private static void ValidateCrewAssignments(Game game)
        {
            if (HighLogic.LoadedSceneHasPlanetarium)
            {
                game.flightState = new FlightState();
            }

            game.CrewRoster.ValidateAssignments(game);
        }

        /// <summary>
        /// Removes the kerbals that we received
        /// </summary>
        private void RemoveQueuedKerbals()
        {
            if (KerbalSystemReady)
            {
                var refreshDialog = false;
                while (KerbalsToRemove.TryDequeue(out var kerbalNameToRemove))
                {
                    HighLogic.CurrentGame.CrewRoster.Remove(kerbalNameToRemove);
                    refreshDialog = true;
                }

                if (refreshDialog) RefreshCrewListsSafe();
            }
        }

        /// <summary>
        /// Loads the unloaded (either because they are new or they are updated) kerbals into the game.
        /// We load them only when we are actually ready to play
        /// </summary>
        private void LoadKerbals()
        {
            if (KerbalSystemReady && HighLogic.LoadedScene >= GameScenes.SPACECENTER)
            {
                ProcessKerbalQueue();
            }
        }

        #endregion

        #region Private

        /// <summary>
        /// Runs trough the concurrentKerbalQueue and process them
        /// </summary>
        private void ProcessKerbalQueue()
        {
            var refreshDialog = false;
            while (KerbalsToProcess.TryDequeue(out var kerbalNode))
            {
                LoadKerbal(kerbalNode);
                refreshDialog = true;
            }

            if (refreshDialog) RefreshCrewListsSafe();
        }

        /// <summary>
        /// Refreshes roster/list views and astronaut complex without clearing or auto-filling the crew assignment dialog.
        /// Use for remote kerbal sync and other flows that must not stomp another player's slot selection.
        /// </summary>
        public void RefreshCrewListsSafe()
        {
            LunaLog.Log($"[CareerSync:e1] crew UI safe refresh crewAssignmentDialog={(CrewAssignmentDialog.Instance != null)} astronautComplex={(AstronautComplex != null)}");
            if (CrewAssignmentDialog.Instance != null)
            {
                CrewAssignmentDialog.Instance.RefreshCrewLists(CrewAssignmentDialog.Instance.GetManifest(), false, true);
            }

            RefreshAstronautComplexLists();
        }

        /// <summary>
        /// Rebuilds crew assignment UI including clear/fill. Reserved for truly local intentional flows; not used from remote kerbal sync.
        /// </summary>
        public void RefreshCrewDialogInvasiveRebuild()
        {
            LunaLog.Log($"[CareerSync:e1] crew UI invasive rebuild crewAssignmentDialog={(CrewAssignmentDialog.Instance != null)} astronautComplex={(AstronautComplex != null)}");
            if (CrewAssignmentDialog.Instance != null)
            {
                CrewAssignmentDialog.Instance.RefreshCrewLists(CrewAssignmentDialog.Instance.GetManifest(), false, true);
                CrewAssignmentDialog.Instance.ButtonClear();
                CrewAssignmentDialog.Instance.ButtonFill();
            }

            RefreshAstronautComplexLists();
        }

        /// <summary>
        /// Backward-compatible name: roster-safe refresh only (no ButtonClear / ButtonFill).
        /// </summary>
        public void RefreshCrewDialog()
        {
            RefreshCrewListsSafe();
        }

        private void RefreshAstronautComplexLists()
        {
            if (AstronautComplex != null)
            {
                InitiateGui.Invoke(AstronautComplex, null);
                CreateAvailableList.Invoke(AstronautComplex, null);
                CreateAssignedList.Invoke(AstronautComplex, null);
                CreateKiaList.Invoke(AstronautComplex, null);
            }
        }

        /// <summary>
        /// Creates or updates a kerbal
        /// </summary>
        private void LoadKerbal(ConfigNode crewNode)
        {
            var protoCrew = new ProtoCrewMember(HighLogic.CurrentGame.Mode, crewNode);
            if (string.IsNullOrEmpty(protoCrew.name))
            {
                LunaLog.LogError("[VMP]: protoName is blank!");
                return;
            }

            if (!HighLogic.CurrentGame.CrewRoster.Exists(protoCrew.name))
            {
                HighLogic.CurrentGame.CrewRoster.AddCrewMember(protoCrew);
            }
            else
            {
                UpdateKerbalData(crewNode, protoCrew);
            }
        }

        /// <summary>
        /// Updates an existing Kerbal
        /// </summary>
        private void UpdateKerbalData(ConfigNode crewNode, ProtoCrewMember protoCrew)
        {
            var careerLogNode = crewNode.GetNode("CAREER_LOG");
            if (careerLogNode != null)
            {
                //Insert wolf howling at the moon here
                HighLogic.CurrentGame.CrewRoster[protoCrew.name].careerLog.Entries.Clear();
                HighLogic.CurrentGame.CrewRoster[protoCrew.name].careerLog.Load(careerLogNode);
            }
            else
            {
                LunaLog.Log($"[VMP]: Career log node for {protoCrew.name} is empty!");
            }

            var flightLogNode = crewNode.GetNode("FLIGHT_LOG");
            if (flightLogNode != null)
            {
                //And here. Someone "cannot into" lists and how to protect them.
                HighLogic.CurrentGame.CrewRoster[protoCrew.name].flightLog.Entries.Clear();
                HighLogic.CurrentGame.CrewRoster[protoCrew.name].flightLog.Load(flightLogNode);
            }
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].courage = protoCrew.courage;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].experience = protoCrew.experience;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].experienceLevel = protoCrew.experienceLevel;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].experienceTrait = protoCrew.experienceTrait;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].gExperienced = protoCrew.gExperienced;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].gIncrement = protoCrew.gIncrement;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].geeForce = protoCrew.geeForce;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].gender = protoCrew.gender;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].hasToured = protoCrew.hasToured;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].isBadass = protoCrew.isBadass;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].inactiveTimeEnd = protoCrew.inactiveTimeEnd;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].outDueToG = protoCrew.outDueToG;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].seat = protoCrew.seat;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].seatIdx = protoCrew.seatIdx;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].stupidity = protoCrew.stupidity;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].trait = protoCrew.trait;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].UTaR = protoCrew.UTaR;
            HighLogic.CurrentGame.CrewRoster[protoCrew.name].veteran = protoCrew.veteran;

            SetKerbalTypeWithoutTriggeringEvent(HighLogic.CurrentGame.CrewRoster[protoCrew.name], protoCrew.type);
            SetKerbalStatusWithoutTriggeringEvent(HighLogic.CurrentGame.CrewRoster[protoCrew.name], protoCrew.rosterStatus);
        }

        #endregion
    }
}
