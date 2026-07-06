using robotManager.Events;
using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using robotManager.Products;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.Remoting.Lifetime;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Bot.GrindManagement;
using Wholesome_Auto_Quester.Bot.JSONManagement;
using Wholesome_Auto_Quester.Bot.QuestManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Bot.TravelManagement;
using Wholesome_Auto_Quester.Database.DBC;
using Wholesome_Auto_Quester.GUI;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.States;
using WholesomeDungeonCrawler.States;
using WholesomeToolbox;
using wManager.Events;
using wManager.Wow.Bot.States;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.Bot
{
    internal class WAQBot
    {
        private readonly Engine Fsm = new Engine();
        private readonly List<StuckCounter> ListStuckCounters = new List<StuckCounter>();

        // Auto-mount path-block: cancel a mount when a hostile is squarely on the path just ahead, so the bot clears
        // it on foot instead of mounting up only to dismount into it a few yards later. Throttled — OnMount can re-fire
        // rapidly while the mount is repeatedly cancelled, and scanning hostiles + path geometry each time adds up.
        private const float MountBlockPathAhead = 50f; // look this far down the path (matches the hostile scan range)
        private const float MountBlockLineWidth = 14f; // how close to the path line a hostile must be to be "in the way"
        private Timer _mountBlockThrottle = new Timer();
        private bool _mountBlockCached;
        private IWowObjectScanner _objectScanner;
        private ITaskManager _taskManager;
        private IQuestManager _questManager;
        private IGrindManager _grindManager;
        private IJSONManager _jsonManager;
        private IContinentManager _continentManager;
        private TravelManager _travelManager;
        private QuestsTrackerGUI _questTrackerGui;
        private IProduct _product;
        private WAQCheckPathAhead _checkPathAheadState;
        private WAQStateInteract _interactState;

        private Stopwatch statewatch = new Stopwatch();
        private string lastState = "";

        internal bool Pulse(QuestsTrackerGUI tracker, IProduct product)
        {
            try
            {
                _product = product;
                _questTrackerGui = tracker;
                _jsonManager = new JSONManager();
                _continentManager = new ContinentManager(_jsonManager);
                _travelManager = new TravelManager(_continentManager);
                _grindManager = new GrindManager(_jsonManager, _continentManager);
                _objectScanner = new WowObjectScanner(_questTrackerGui);
                _questManager = new QuestManager(_objectScanner, _questTrackerGui, _jsonManager, _continentManager);
                _taskManager = new TaskManager(_objectScanner, _questManager, _grindManager, _questTrackerGui, _travelManager, _continentManager);
                DBCFaction.RecordReputations();

                // Attach onlevelup for spell book:
                EventsLua.AttachEventLua("PLAYER_LEVEL_UP", m => OnLevelUp());
                EventsLua.AttachEventLua("PLAYER_ENTERING_WORLD", m => ScreenReloaded());
                EventsLua.AttachEventLua("UPDATE_FACTION", m => OnReputationChange());

                // Update spell list
                SpellManager.UpdateSpellBook();

                // Load CC:
                CustomClass.LoadCustomClass();

                // FSM
                Fsm.States.Clear();

                _checkPathAheadState = new WAQCheckPathAhead(_objectScanner);
                _interactState = new WAQStateInteract(_objectScanner);
                _interactState.Initialize();
                State lootState = WholesomeAQSettings.CurrentSetting.TurboLoot ?
                    new WAQTurboLoot() : new Looting();

                State[] states = new State[]
                {
                    new Relogger(),
                    new WAQLoadingScreenLock(_travelManager),
                    new NPCScanState(),
                    new Pause(),
                    new WAQForceResurrection(),
                    new Resurrect(),
                    new WAQAntiDrown(),
                    new WAQExitVehicle(),
                    new MyMacro(),
                    //new WAQBlacklistDanger(),
                    new WAQStatePriorityLoot(_objectScanner),
                    new WAQDefend(),
                    new WAQWaitResurrectionSickness(),
                    new Regeneration(),
                    _checkPathAheadState,
                    new WAQStateLoot(_objectScanner), // loot for quests
                    lootState,
                    //new MillingState(),
                    new Farming(),
                    new FarmingRange(),
                    // Druid shortcut INTO Moonglade: cast Teleport: Moonglade instead of walking/taxiing in (the land
                    // entrance is a high-level mob gauntlet). Above the flight-master states so the teleport wins for a
                    // Moonglade destination; inactive for everyone else. LEAVING Moonglade is left to the flight master.
                    new WAQStateMoongladeTeleport(_taskManager, _continentManager),
                    // Reliable Moonglade EXIT via the Cenarion free-flight gossip (Bunthen/Silva) - above the flight
                    // master states, so we never taxi-map or walk out of Moonglade (the taxi map needs the learned
                    // outbound path a teleported-in char lacks; walking out hits the deadly entrance gauntlet).
                    new WAQStateMoongladeExit(_taskManager, _continentManager),
                    // Integrated flight-master travel: ported from the Wholesome-TBC-FlightMaster plugin INTO the product
                    // for full control (no longer the Dungeon Crawler's compiled FlightMaster* states - a separate
                    // product). Register the manager's own instances, since its stuck/discovery hooks compare against
                    // these exact objects.
                    FlightMasterManagement.FlightMasterManager.takeTaxiState,
                    FlightMasterManagement.FlightMasterManager.discoverFlightMasterState,
                    FlightMasterManagement.FlightMasterManager.waitOnTaxiState,
                    // DISABLED (Talamin): the automatic class-trainer state. In the Death Knight start the trainers are
                    // UP in the Ebon Hold tower (only reachable by flying the gryphon back up), so an opportunistic
                    // "go train" would path off to an unreachable spot mid-chain. Training is instead hard-coded into
                    // the DK profile at a defined point (when we're back up top). Re-enable if general questing needs it.
                    // new Trainers(),
                    new ToTown(),
                    // DISABLED (Talamin): the opportunistic "grab a quest NPC we pass within 150y" state. It only ever
                    // acted on the scanner's ALREADY-active task (it does not scan for other nearby givers), so it
                    // merely duplicated Travel + Interact at a HIGHER FSM priority — and that priority twice caused
                    // regressions (level-32 training preemption; amplifying the premature-pickup collision). Turn-ins
                    // and pickups still happen via Travel -> WAQStateInteract on arrival, just without the priority
                    // jump. Re-enable by uncommenting if a "walked past a giver" case ever proves it earns its keep.
                    // new WAQStateGrabNearbyQuest(_objectScanner),
                    // Scripted-zone override: inside the Death Knight start (Ebon Hold, map 609) a DK follows a
                    // hand-authored ordered profile instead of the DB-driven quester (the chain is linear + gossip/
                    // vehicle-scripted). Inactive for everyone else, so normal questing is untouched. Above Travel/
                    // Interact/Kill so it owns routing in Ebon Hold; below combat/loot/regen/town which still interrupt.
                    new WAQStateDeathKnightStart(),
                    new WAQStateTravel(_taskManager, _travelManager, _continentManager),
                    _interactState,
                    // Above Kill: while a "use item on this creature" task is still in its use phase, we use the item
                    // first; the task then flips to KillAndLoot and Kill/Loot below finish a hostile target.
                    new WAQStateUseItemOnCreature(_objectScanner),
                    new WAQStateKill(_objectScanner),
                    // Above MoveToHotspot on purpose: once we're standing on a use-item spot this owns the tick, so
                    // MoveToHotspot never times the task out as "couldn't find target" (that timeout is for scanned
                    // targets, not bare coordinates).
                    new WAQStateUseItem(_taskManager),
                    new WAQStateMoveToHotspot(_taskManager, _travelManager),
                    new MovementLoop(),
                    new Idle()
                };

                states = states.Reverse().ToArray();

                for (int i = 0; i < states.Length; i++)
                {
                    states[i].Priority = i;
                    Fsm.AddState(states[i]);
                }

                Fsm.States.Sort();

                // One-time FSM priority dump (DevMode) — confirms WAQStateGrabNearbyQuest now sits BELOW Trainers /
                // ToTown / the injected Wholesome Vendors states (so a town/train errand is never preempted), but
                // still above Travel/Interact/Kill, and below combat/loot/regen.
                if (WholesomeAQSettings.CurrentSetting.DevMode)
                    foreach (State fsmState in Fsm.States)
                        Logging.Write($"[FSM] prio {fsmState.Priority,3} : {fsmState.DisplayName}");

                Fsm.StartEngine(10, "_AutoQuester");

                // Start the integrated flight-master travel system (movement-pulse taxi trigger + background node
                // discovery). Its FSM states are already registered above.
                FlightMasterManagement.FlightMasterManager.Initialize();

                StopBotIf.LaunchNewThread();

                MovementEvents.OnSeemStuck += SeemStuckHandler;
                OthersEvents.OnMount += OnMountHandler;

                if (WholesomeAQSettings.CurrentSetting.DevMode)
                {
                    Radar3D.OnDrawEvent += Radar3DOnDrawEvent;
                    Radar3D.Pulse();
                }

                FiniteStateMachineEvents.OnBeforeCheckIfNeedToRunState += BeforeRunState;

                return true;
            }
            catch (Exception e)
            {
                Dispose();
                Logging.WriteError("Bot > Bot  > Pulse(): " + e);
                return false;
            }
        }

        internal void Dispose()
        {
            try
            {
                FiniteStateMachineEvents.OnBeforeCheckIfNeedToRunState -= BeforeRunState;
                _jsonManager?.Dispose();
                _grindManager?.Dispose();
                _objectScanner?.Dispose();
                _questManager?.Dispose();
                _taskManager?.Dispose();
                _travelManager?.Dispose();
                FlightMasterManagement.FlightMasterManager.Dispose();

                _interactState.Dispose();

                if (WholesomeAQSettings.CurrentSetting.DevMode)
                {
                    Radar3D.OnDrawEvent -= Radar3DOnDrawEvent;
                
                }
                MovementEvents.OnSeemStuck -= SeemStuckHandler;
                OthersEvents.OnMount -= OnMountHandler;

                CustomClass.DisposeCustomClass();
                Fsm.StopEngine();
                Fight.StopFight();
                MovementManager.StopMove();
            }
            catch (Exception e)
            {
                Logging.WriteError("Bot > Bot  > Dispose(): " + e);
            }
        }

        private void BeforeRunState(Engine engine, State state, CancelEventArgs cancelable)
        {
            if (!WholesomeAQSettings.CurrentSetting.AllowStopWatch) return;
            if (!statewatch.IsRunning) statewatch.Start();
            if (state.DisplayName != "Security/Stop game")
            {
                long elapsed = statewatch.ElapsedMilliseconds;
                PerfLog.Record(lastState, elapsed);
                if (elapsed > 200)
                    Logger.LogError($"{lastState} took {elapsed}");
                statewatch.Restart();
                lastState = state.DisplayName;
            }
        }

        private void OnLevelUp()
        {
            if (ObjectManager.Me.Level >= WholesomeAQSettings.CurrentSetting.StopAtLevel)
            {
                Logger.Log($"You have reached your maximum set level ({WholesomeAQSettings.CurrentSetting.StopAtLevel}). Stopping.");
                _product.Dispose();
                return;
            }

            SpellManager.UpdateSpellBook();
            CustomClass.ResetCustomClass();
            Talent.DoTalents();
            wManager.wManagerSetting.ClearBlacklistOfCurrentProductSession();
            WTSettings.AddRecommendedBlacklistZones();
        }

        private void OnReputationChange()
        {
            DBCFaction.RecordReputations();
        }

        private void ScreenReloaded()
        {
        }

        private void Radar3DOnDrawEvent()
        {
            if (WholesomeAQSettings.CurrentSetting.DevMode)
            {
                Radar3D.DrawString(Logger.ScannerString, new Vector3(30, 290, 0), 10, Color.LightSteelBlue);
                Radar3D.DrawString(Logger.TaskMString, new Vector3(30, 310, 0), 10, Color.MediumAquamarine);

                if (_travelManager.ShouldTravel)
                {
                    Radar3D.DrawString($"{_continentManager.MyMapArea.Continent} - {_continentManager.MyMapArea.areaName} " +
                        $"=> {_taskManager.ActiveTask.WorldMapArea.Continent} - {_taskManager.ActiveTask.WorldMapArea.areaName}",
                        new Vector3(30, 330, 0), 10, Color.PaleGoldenrod);
                }
                /*
                foreach ((Vector3 a, Vector3 b) line in _clearPathState.LinesToCheck)
                {
                    Radar3D.DrawLine(line.a, line.b, Color.Red);
                }
                */

                foreach (Vector3 point in _checkPathAheadState.PointsAlongPathSegments)
                {
                    Radar3D.DrawCircle(point, 0.2f, Color.Green, true, 150);
                }

                if (_checkPathAheadState.DangerTraceline.a != null && _checkPathAheadState.DangerTraceline.b != null)
                {
                    Radar3D.DrawCircle(_checkPathAheadState.DangerTraceline.a, 0.4f, Color.Red, false, 200);
                    Radar3D.DrawLine(_checkPathAheadState.DangerTraceline.a, _checkPathAheadState.DangerTraceline.b, Color.Red, 200);
                }

                if (_checkPathAheadState.UnitOnPath.unit != null)
                {
                    Radar3D.DrawCircle(_checkPathAheadState.UnitOnPath.unit.PositionWithoutType, 0.4f, Color.Red, true, 200);
                }

                for (int i = 0; i < _checkPathAheadState.LinesToCheck.Count - 1; i++)
                {
                    Radar3D.DrawLine(_checkPathAheadState.LinesToCheck[i], _checkPathAheadState.LinesToCheck[i + 1], Color.OrangeRed, 150);
                }

                if (_taskManager.ActiveTask != null)
                {
                    Radar3D.DrawString(_taskManager.ActiveTask.TaskName, new Vector3(30, 350, 0), 10, Color.PaleTurquoise);
                    Radar3D.DrawLine(ObjectManager.Me.Position, _taskManager.ActiveTask.Location, Color.PaleTurquoise);
                    Radar3D.DrawCircle(_taskManager.ActiveTask.Location, 1.3f, Color.PaleTurquoise);
                }

                if (_objectScanner.ActiveWoWObject.wowObject != null)
                {
                    Radar3D.DrawLine(ObjectManager.Me.Position, _objectScanner.ActiveWoWObject.wowObject.Position, Color.Yellow);
                    Radar3D.DrawCircle(_objectScanner.ActiveWoWObject.wowObject.Position, 1, Color.Yellow);
                    Radar3D.DrawLine(ObjectManager.Me.Position, _objectScanner.ActiveWoWObject.task.Location, Color.GreenYellow);
                    Radar3D.DrawCircle(_objectScanner.ActiveWoWObject.task.Location, 0.7f, Color.GreenYellow);
                    Radar3D.DrawString($"{_objectScanner.ActiveWoWObject.wowObject.Name} ({_objectScanner.ActiveWoWObject.wowObject.Entry})"
                        , new Vector3(30, 370, 0), 10, Color.Yellow);
                    Radar3D.DrawString($"{_objectScanner.ActiveWoWObject.task.TaskName}"
                        , new Vector3(30, 390, 0), 10, Color.GreenYellow);
                }

                if (MovementManager.InMovement)
                    Radar3D.DrawString("Movement thread running", new Vector3(30, 410, 0), 10, Color.Green);
                else
                    Radar3D.DrawString("Movement thread not running", new Vector3(30, 410, 0), 10, Color.Red);
            }
        }

        // Cancel an auto-mount when a hostile is squarely on the path just ahead, so the bot clears it ON FOOT instead
        // of mounting up only to dismount into it a few yards later (the "mount -> ride 6s -> dismount -> fight" Talamin
        // saw). Once the blocker is cleared the next mount attempt finds the path clear and proceeds.
        private void OnMountHandler(string mountName, CancelEventArgs cancelable)
        {
            // A flying mount flies over ground mobs, so don't block it — only ground travel runs into them.
            if (wManager.wManagerSetting.CurrentSetting.UseFlyingMount
                && mountName == wManager.wManagerSetting.CurrentSetting.FlyingMountName)
                return;

            if (_mountBlockThrottle.IsReady)
            {
                _mountBlockCached = ToolBox.HostileBlocksTravelPath(MountBlockPathAhead, MountBlockLineWidth);
                _mountBlockThrottle = new Timer(200);
            }

            if (_mountBlockCached)
            {
                cancelable.Cancel = true;
                if (WholesomeAQSettings.CurrentSetting.DevMode)
                    Logging.Write("[WAQ] Mount cancelled: a hostile is on the path ahead — clearing it on foot first.");
            }
        }

        private void SeemStuckHandler()
        {
            if (!MovementManager.InMovement)
            {
                return;
            }

            IWAQTask task = _taskManager.ActiveTask;
            WoWObject wowObject = _objectScanner.ActiveWoWObject.wowObject;

            if (wowObject != null)
            {
                StuckCounter existing = ListStuckCounters.Find(sc => sc.WowObject != null && sc.WowObject.Guid == wowObject.Guid);
                if (existing == null)
                    ListStuckCounters.Add(new StuckCounter(task, wowObject));
                else
                    existing.AddToCount();
                return;
            }

            if (task != null)
            {
                StuckCounter existing = ListStuckCounters.Find(sc => sc.Task.Location == task.Location);
                if (existing == null)
                    ListStuckCounters.Add(new StuckCounter(task, null));
                else
                    existing.AddToCount();
                return;
            }
        }
    }
}

public class StuckCounter
{
    public int Count;
    public WoWObject WowObject;
    public IWAQTask Task;
    private Timer _timer = new Timer();

    public StuckCounter(IWAQTask task, WoWObject wowObject)
    {
        Count = 0;
        WowObject = wowObject;
        Task = task;
        AddToCount();
    }

    public void AddToCount()
    {
        if (_timer.IsReady) Count = 0;
        _timer = new Timer(30 * 1000);
        int maxCOunt = 10;
        if (WowObject?.Position.Z > ObjectManager.Me.Position.Z + 20 && WowObject?.Position.DistanceTo2D(ObjectManager.Me.Position) < 10
            || Task?.Location.Z > ObjectManager.Me.Position.Z + 20 && Task?.Location.DistanceTo2D(ObjectManager.Me.Position) < 10)
            maxCOunt = 3;
        Count++;

        if (Count > maxCOunt || !ObjectManager.Me.IsAlive) return;

        if (WowObject != null)
            Logger.Log($"We seem stuck trying to reach object {WowObject.Name} ({Count})");
        else
            Logger.Log($"We seem stuck trying to reach task {Task.TaskName} ({Count})");

        if (Count >= maxCOunt)
        {
            if (WowObject != null)
            {
                Fight.StopFight();
                BlacklistHelper.AddNPC(WowObject.Guid, $"Stuck {maxCOunt} times trying to reach");
                BlacklistHelper.AddZone(WowObject.Position, 5, $"Stuck {maxCOunt} times trying to reach");
                Task.PutTaskOnTimeout($"Stuck {Count} times", 15 * 60, true);
                return;
            }
            if (Task != null)
            {
                Fight.StopFight();
                BlacklistHelper.AddZone(Task.Location, 5, $"Stuck {maxCOunt} times trying to reach");
                Task.PutTaskOnTimeout($"Stuck {Count} times", 15 * 60, true);
            }
        }
    }
}