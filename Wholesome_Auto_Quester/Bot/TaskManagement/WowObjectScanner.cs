using robotManager.Helpful;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.GUI;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.Logic;
using WholesomeToolbox;
using wManager;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement
{
    internal class WowObjectScanner : IWowObjectScanner
    {
        private readonly Dictionary<int, List<IWAQTask>> _scannerRegistry = new Dictionary<int, List<IWAQTask>>(); // object entry => associated tasks
        private readonly QuestsTrackerGUI _guiTracker;
        private readonly object _scannerLock = new object();
        private bool _isRunning = false;
        private Dictionary<ulong, int> _scanned = new Dictionary<ulong, int>(); // guid, times scanned
        private WAQPath _pathToObject;
        private List<int> _interactObjects = new List<int>() { 
            161536, // Slag Pit quary gate door
            177444 // Maraudon Stone door
        };

        public (WoWObject wowObject, IWAQTask task) ActiveWoWObject { get; private set; } = (null, null);

        public WowObjectScanner(QuestsTrackerGUI tracker)
        {
            _guiTracker = tracker;
            Initialize();
            EventsLuaWithArgs.OnEventsLuaStringWithArgs += LuaEventHandler;
        }

        private WAQPath GetPathToObject(WoWObject wowObject)
        {
            if (_pathToObject == null
                || _pathToObject.Path.Last().DistanceTo(wowObject.Position) > 3
                || _pathToObject.Path.First().DistanceTo(ObjectManager.Me.Position) > 10)
            {
                _pathToObject = ToolBox.GetWAQPath(ObjectManager.Me.Position, wowObject.Position);
            }
            return _pathToObject;
        }

        public void Initialize()
        {
            _isRunning = true;
            Task.Run(async () =>
            {
                while (_isRunning)
                {
                    // A single bad registry entry must never kill this thread: if it dies, ActiveWoWObject
                    // freezes and the bot silently stops finding objects.
                    try
                    {
                        if (Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause)
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            Pulse();
                            PerfLog.Record("Scanner.Pulse", sw.ElapsedMilliseconds);
                        }
                    }
                    catch (System.Exception e)
                    {
                        Logger.LogError($"[Scanner] Pulse crashed (recovered): {e}");
                    }
                    await Task.Delay(500);
                }
            });
            EventsLuaWithArgs.OnEventsLuaStringWithArgs -= LuaEventHandler;
            //ObjectManagerEvents.OnObjectManagerPulsed += OnObjectManagerPulse;
        }

        public void Dispose()
        {
            _isRunning = false;
            //ObjectManagerEvents.OnObjectManagerPulsed -= OnObjectManagerPulse;
            lock (_scannerLock)
            {
                _scannerRegistry.Clear();
            }
        }

        private void LuaEventHandler(string eventid, List<string> args)
        {
            switch (eventid)
            {
                case "PLAYER_LEVEL_UP":
                    lock (_scannerLock)
                    {
                        ActiveWoWObject = (null, null);
                    }
                    break;
            }
        }

        private void HandleSpecialInteractions(WoWObject wowObject)
        {
            // Slag Pit quary gate door
            if (_interactObjects.Contains(wowObject.Entry)
                && wowObject.GetDistance < 5)
            {
                Interact.InteractGameObject(wowObject.GetBaseAddress);
            }
        }

        // Don't declare a POI unreachable when we're already this close - GO centers are often off-mesh (inside
        // the model), so the exact-point pathfind fails even though the object is right there.
        private const float UnreachableMarkMinDistance = 12f;

        private void MarkAsUnreachable(WoWObject obj)
        {
            BlacklistHelper.AddZone(obj.Position, 5, "[Scanner] Unreachable");
            IWAQTask associatedTask = GetTaskMatchingWithObject(obj);
            if (associatedTask == null)
            {
                return;
            }
            // Graduated recovery: a transient obstacle no longer parks the quest for 3 HOURS - short first
            // timeout, escalating only if it keeps being unreachable.
            associatedTask.PutTaskOnTimeout("[Scanner] Unreachable", RecoveryPolicy.FirstTimeoutSeconds(TaskFailureKind.Unreachable), RecoveryPolicy.Escalates(TaskFailureKind.Unreachable));
            associatedTask.RecordAsUnreachable();
        }

        private void Pulse()
        {
            lock (_scannerLock)
            {
                WoWLocalPlayer me = ObjectManager.Me;
                if (me.IsOnTaxi
                    || me.IsDead
                    || !me.IsValid
                    || me.HaveBuff("Drink")
                    || me.HaveBuff("Food"))
                {
                    return;
                }

                Stopwatch watch = new Stopwatch();
                watch.Start();

                _guiTracker.UpdateScanReg(GuiScanEntries);

                List<WoWObject> allObjects = ObjectManager.GetObjectWoW();

                foreach (WoWObject wowObject in allObjects)
                {
                    HandleSpecialInteractions(wowObject);
                }

                Vector3 myPos = me.Position;
                List<WoWObject> listSurroundingPOIs = allObjects
                    .FindAll(wowObject =>
                        wowObject.IsValid
                        && wowObject.Guid > 0
                        && _scannerRegistry.ContainsKey(wowObject.Entry)
                        && _scannerRegistry[wowObject.Entry].Any(task => task.IsValid)
                        && _scannerRegistry[wowObject.Entry].Any(task => task.IsObjectValidForTask(wowObject))
                        && !wManagerSetting.IsBlackListed(wowObject.Guid)
                        && !wManagerSetting.IsBlackListedZone(wowObject.Position)
                        && wowObject.Position.DistanceTo(myPos) < 60
                        /*&& WTLocation.GetZDifferential(wowObject.Position) < 15*/)
                    .OrderBy(wowObject => wowObject.Position.DistanceTo(myPos))
                    .ToList();
                listSurroundingPOIs.RemoveAll(wowObject => _scanned.ContainsKey(wowObject.Guid) && _scanned[wowObject.Guid] > 3);

                if (listSurroundingPOIs.Count > 0)
                {
                    WoWObject closestObject = listSurroundingPOIs[0];

                    WAQPath pathToClosestObject = GetPathToObject(closestObject);

                    // A GameObject's center routinely sits INSIDE its model / off the navmesh, so a pathfind to the
                    // exact point can fail while the char literally stands next to it (Shrine of Dath'Remar: benched
                    // as unreachable from 9.5y away, plus a 5y zone blacklist on top). Within interact-approach range
                    // trust proximity over the pathfinder and let the interact task close the last yards.
                    if (closestObject.Position.DistanceTo(myPos) > UnreachableMarkMinDistance && !pathToClosestObject.IsReachable)
                    {
                        MarkAsUnreachable(closestObject);
                        Logger.LogWatchScanner($"SCANNER MARKED UNREACHABLE", watch.ElapsedMilliseconds);
                        return;
                    }

                    // Avoid snap back and forth
                    if (ActiveWoWObject.wowObject != null
                        && MovementManager.InMovement
                        && pathToClosestObject.Distance > WTPathFinder.GetCurrentPathRemainingDistance() - 15)
                    {
                        Logger.LogWatchScanner($"SCANNER AVOID SNAP", watch.ElapsedMilliseconds);
                        return;
                    }

                    if (pathToClosestObject.Distance > closestObject.Position.DistanceTo(myPos) * 1.5)
                    {
                        int nbObject = listSurroundingPOIs.Count;
                        for (int i = 1; i < nbObject - 1; i++)
                        {
                            WAQPath pathToNewObject = GetPathToObject(listSurroundingPOIs[i]);

                            if (listSurroundingPOIs[i].Position.DistanceTo(myPos) > UnreachableMarkMinDistance && !pathToNewObject.IsReachable)
                            {
                                MarkAsUnreachable(listSurroundingPOIs[i]);
                                break;
                            }

                            if (pathToNewObject.Distance < pathToClosestObject.Distance)
                            {
                                pathToClosestObject = pathToNewObject;
                                closestObject = listSurroundingPOIs[i];
                            }

                            float flyDistanceToNextObject = listSurroundingPOIs[i + 1].Position.DistanceTo(myPos);
                            if (pathToClosestObject.Distance < flyDistanceToNextObject)
                            {
                                break;
                            }
                        }
                    }

                    if (closestObject.Guid <= 0 || !closestObject.IsValid)
                    {
                        ActiveWoWObject = (null, null);
                        return;
                    }

                    IWAQTask associatedTask = GetTaskMatchingWithObject(closestObject);
                    if (associatedTask != null)
                    {
                        ActiveWoWObject = (closestObject, associatedTask);
                        Logger.LogWatchScanner($"SCANNER FOUND OBJECT", watch.ElapsedMilliseconds);
                        return;
                    }
                }

                if (_scanned.Count > 3) _scanned.Clear();

                // scanner Unsnapped
                if (ActiveWoWObject.wowObject != null)
                {
                    if (_scanned.TryGetValue(ActiveWoWObject.wowObject.Guid, out int amount))
                    {
                        _scanned[ActiveWoWObject.wowObject.Guid]++;
                        if (_scanned[ActiveWoWObject.wowObject.Guid] > 3)
                            Logger.LogError($"{ActiveWoWObject.wowObject.Name} has been temporarily banned from the scanner");
                    }
                    else
                        _scanned.Add(ActiveWoWObject.wowObject.Guid, 1);
                }

                Logger.LogWatchScanner($"SCANNER FOUND NOTHING", watch.ElapsedMilliseconds);
                ActiveWoWObject = (null, null);
            }
        }

        private IWAQTask GetTaskMatchingWithObject(WoWObject closestObject)
        {
            if (closestObject == null)
            {
                Logger.LogDebug($"[Scanner] GetTaskMatchingWithObject called with a null object - skipping");
                return null;
            }

            if (_scannerRegistry.TryGetValue(closestObject.Entry, out List<IWAQTask> taskList))
            {
                // When ONE npc carries several tasks (very common: turn in quest A, then that same npc gives quest B),
                // a TURN-IN must win. Both tasks share the npc's Location, so ordering by distance alone is a tie and
                // FirstOrDefault picked whichever registered first - often the pickup. The pickup for B usually can't
                // even be completed until A is handed in, so the bot looped "Failed to pick up B" and never turned in A
                // (Gornek: "Your Place in the World" / "Cutting Teeth"). Turn-ins first fixes the ordering at the source.
                return taskList
                    .Where(task => task.IsObjectValidForTask(closestObject) && task.IsValid)
                    .OrderByDescending(task => task.IsTurnInQuest)
                    .ThenBy(task => task.Location.DistanceTo(closestObject.Position))
                    .FirstOrDefault();
            }
            Logger.LogDebug($"[Scanner] No registry entry for {closestObject.Entry} ({closestObject.Name}) - skipping");
            return null;
        }

        public void AddToScannerRegistry(int entry, IWAQTask task)
        {
            lock (_scannerLock)
            {
                if (_scannerRegistry.TryGetValue(entry, out List<IWAQTask> taskList))
                {
                    if (!taskList.Contains(task))
                    {
                        taskList.Add(task);
                        Logger.LogDebug($"Added ({entry}) {task.TaskName} to the scanner regsitry ({task.Location})");
                    }
                }
                else
                {
                    _scannerRegistry[entry] = new List<IWAQTask>() { task };
                    Logger.LogDebug($"Added ({entry}) {task.TaskName} to the scanner regsitry (didn't exist) ({task.Location})");
                }
            }
        }

        public void RemoveFromScannerRegistry(int entry, IWAQTask task)
        {
            lock (_scannerLock)
            {
                if (_scannerRegistry.TryGetValue(entry, out List<IWAQTask> taskList))
                {
                    if (taskList.Remove(task))
                    {
                        Logger.LogDebug($"Removed ({entry}) {task.TaskName} from the scanner registry ({task.Location})");
                    }
                    else
                    {
                        Logger.LogDebug($"[Scanner] {task.TaskName} wasn't registered under entry {entry} ({task.Location})");
                    }
                    if (taskList.Count <= 0)
                    {
                        _scannerRegistry.Remove(entry);
                        Logger.LogDebug($"Removed ENTRY {entry} from the scanner registry ({task.Location})");
                    }
                }
                else
                {
                    Logger.LogDebug($"[Scanner] Tried to remove {task.TaskName} but entry {entry} didn't exist ({task.Location})");
                }
            }
        }

        private List<GUIScanEntry> GuiScanEntries
        {
            get
            {
                List<GUIScanEntry> scanEntries = new List<GUIScanEntry>();
                foreach (KeyValuePair<int, List<IWAQTask>> entry in _scannerRegistry)
                {
                    foreach (IWAQTask task in entry.Value)
                    {
                        if (!scanEntries.Exists(entry => entry.TaskName == task.TaskName))
                        {
                            scanEntries.Add(new GUIScanEntry(entry.Key, task));
                        }
                        else
                        {
                            scanEntries.Find(entry => entry.TaskName == task.TaskName).AddOne(task);
                        }
                    }
                }
                return scanEntries;
            }
        }
    }
}
