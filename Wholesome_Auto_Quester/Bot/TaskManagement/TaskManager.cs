using robotManager.Helpful;
using Supercluster.KDTree;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Bot.GrindManagement;
using Wholesome_Auto_Quester.Bot.QuestManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Bot.TravelManagement;
using Wholesome_Auto_Quester.GUI;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.Logic;
using WholesomeToolbox;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement
{
    internal class TaskManager : ITaskManager
    {
        private readonly IQuestManager _questManager;
        private readonly IGrindManager _grindManager;
        private readonly IWowObjectScanner _objectScanner;
        private readonly ITravelManager _travelManager;
        private readonly IContinentManager _continentManager;
        private readonly QuestsTrackerGUI _tracker;
        private readonly List<IWAQTask> _taskPile = new List<IWAQTask>();
        private readonly List<IWAQTask> _grindTasks = new List<IWAQTask>();
        private bool _isRunning = false;
        private bool _lastGrindOnly; // tracks the GrindOnly setting so a live toggle triggers a quest-list refresh
        private int _tick;
        private Dictionary<IWAQTask, int> _snappedTasks = new Dictionary<IWAQTask, int>(); // guid, times scanned

        public IWAQTask ActiveTask { get; private set; }

        public TaskManager(
            IWowObjectScanner scanner, 
            IQuestManager questManager, 
            IGrindManager grindManager,
            QuestsTrackerGUI questTrackerGUI, 
            ITravelManager travelManager,
            IContinentManager continentManager)
        {
            _continentManager = continentManager;
            _travelManager = travelManager;
            _objectScanner = scanner;
            _questManager = questManager;
            _grindManager = grindManager;
            _tracker = questTrackerGUI;
            Initialize();
        }

        public void Initialize()
        {
            _isRunning = true;
            // Seed the GrindOnly tracker from the loaded setting so the first planner cycle doesn't see a spurious
            // "transition" (QuestManager.Initialize already loaded/cleared the list to match the current value).
            _lastGrindOnly = WholesomeAQSettings.CurrentSetting?.GrindOnly ?? false;
            Task.Run(async () =>
            {
                while (_isRunning)
                {
                    // Guard the whole pass: an exception here used to kill the planning thread, freezing
                    // ActiveTask. The await is now outside the guard so the loop also yields (no busy-spin)
                    // while out of game / paused.
                    try
                    {
                        if (Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause)
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            UpdateTaskPile();
                            BlacklistHelper.CleanupBlacklist();
                            PerfLog.Record("TaskManager.UpdateTaskPile", sw.ElapsedMilliseconds);
                        }
                        PerfLog.DumpIfDue();
                    }
                    catch (Exception e)
                    {
                        Logger.LogError($"[TaskManager] UpdateTaskPile crashed (recovered): {e}");
                    }
                    await Task.Delay(500);
                }
            });
            EventsLuaWithArgs.OnEventsLuaStringWithArgs += LuaEventHandler;
        }
        
        private void LuaEventHandler(string eventid, List<string> args)
        {            
            if (eventid == "PLAYER_LEVEL_UP")
            {
                ClearGrindTasks();
                ActiveTask = null;
                MovementManager.StopMove();
            }            
        }
        
        public void Dispose()
        {
            _taskPile.Clear();
            EventsLuaWithArgs.OnEventsLuaStringWithArgs -= LuaEventHandler;
            _isRunning = false;
        }

        private void ClearGrindTasks()
        {
            foreach (IWAQTask grindTask in _grindTasks)
            {
                grindTask.UnregisterEntryToScanner(_objectScanner);
            }
            _grindTasks.Clear();
        }

        private void AddTaskToPile(IWAQTask task)
        {
            if (!_taskPile.Contains(task))
            {
                _taskPile.Add(task);
            }
            else
            {
                Logger.LogDebug($"Tried to add {task.TaskName} to the TaskPile but it already existed - skipping");
            }
        }

        public void UpdateTaskPile()
        {
            _tick++;
            WoWLocalPlayer me = ObjectManager.Me;
            if (me.IsOnTaxi
                || me.IsDead
                || !me.IsValid
                || Fight.InFight
                || _travelManager.ShouldTravel
                || me.HaveBuff("Drink")
                || me.HaveBuff("Food")
                || MovementManager.InMovement && WTPathFinder.GetCurrentPathRemainingDistance() > 200 && _tick % 5 != 0)
            {
                return;
            }

            Stopwatch watch = new Stopwatch();
            watch.Start();

            _taskPile.Clear();

            Vector3 myPosition = ObjectManager.Me.Position;
            List<IWAQTask> tasksToAdd = new List<IWAQTask>();

            // Quests. Pick up a LIVE GrindOnly toggle (via the overlay / config) and refresh the quest list this same
            // cycle, so Grind-only -> Quester resumes questing immediately instead of waiting for the next zone change
            // or level-up (the only other GetQuestsFromDB triggers). GrindOnly -> Grind-only clears it (handled in the
            // reload too). The reload is one-off per toggle, so its cost doesn't hit the steady-state planner.
            bool grindOnly = WholesomeAQSettings.CurrentSetting.GrindOnly;
            if (grindOnly != _lastGrindOnly)
            {
                _lastGrindOnly = grindOnly;
                Logger.Log($"Grind only toggled {(grindOnly ? "ON" : "OFF")} - refreshing quest list");
                _questManager.ReloadQuestsFromDB();
            }
            if (!grindOnly)
            {
                tasksToAdd.AddRange(_questManager.GetAllValidQuestTasks());
            }

            // Add grind tasks if nothing else is valid
            if (tasksToAdd.Count <= 0)
            {
                if (_grindTasks.Count <= 0)
                {
                    List<IWAQTask> allGrindTasks = _grindManager.GetGrindTasks;
                    _grindTasks.AddRange(allGrindTasks);
                    foreach (IWAQTask grindTask in allGrindTasks)
                    {
                        grindTask.RegisterEntryToScanner(_objectScanner);
                    }
                }
                _tracker.UpdateInvalids(_grindTasks.FindAll(task => !task.IsValid));
                tasksToAdd.AddRange(_grindTasks.FindAll(task => task.IsValid));
            }
            else
            {
                _tracker.UpdateInvalids(_questManager.GetAllInvalidQuestTasks());
                if (_grindTasks.Count > 0)
                {
                    ClearGrindTasks();
                }
            }

            // Sub-phase timing (gated behind AllowStopWatch) to pin WHICH part of UpdateTaskPile eats the 400-999ms
            // in dense hubs before optimising it - the per-task 64yd RadialSearch in CalculatePriority is the prime
            // suspect (it scales with task density), but Phase 1 taught us to measure before cutting.
            long msValid = watch.ElapsedMilliseconds;
            PerfLog.Record("TaskPile.1 GetValid", msValid);

            var spaceTree = BuildTree(tasksToAdd);
            long msTree = watch.ElapsedMilliseconds;
            PerfLog.Record("TaskPile.2 BuildTree", msTree - msValid);

            List<GUITask> guiTasks = new List<GUITask>();
            foreach (IWAQTask task in tasksToAdd)
            {
                guiTasks.Add(new GUITask(CalculatePriority(myPosition, spaceTree, task), task));
            }
            PerfLog.Record("TaskPile.3 Priority", watch.ElapsedMilliseconds - msTree);

            guiTasks = guiTasks.OrderBy(task => task.Priority).ToList();
            foreach (GUITask guiTask in guiTasks)
            {
                AddTaskToPile(guiTask.Task);
            }

            _tracker.UpdateTasksList(guiTasks);

            if (_taskPile.Count <= 0)
            {
                Logger.LogError($"No task available");
                return;
            }

            // If a wow object is found, we force the closest task
            if (_objectScanner.ActiveWoWObject != (null, null))
            {
                ActiveTask = _objectScanner.ActiveWoWObject.task;
                Logger.LogWatchTask($"TASKM FORCE CLOSEST", watch.ElapsedMilliseconds);
                return;
            }

            // Get closest task
            IWAQTask closestTask = _taskPile[0];

            // Check if travel is needed
            if (_travelManager.IsTravelRequired(closestTask))
            {
                ActiveTask = closestTask;
                Logger.LogWatchTask($"TASKM TRAVEL REQUIRED", watch.ElapsedMilliseconds);
                return;
            }
            /*
            // Only change task if > 30 yards diff
            if (ActiveTask != null
                && closestTask.Location != ActiveTask.Location
                && closestTask.Location.DistanceTo(myPosition) > 30
                && closestTask.Location.DistanceTo(ActiveTask.Location) < 30)
            {
                return;
            }
            */
            WAQPath pathToClosestTask = closestTask.LongPathToTask;

            // Avoid snap back and forth
            if (ActiveTask != null && MovementManager.InMovement)
            {
                float remainingDistance = WTPathFinder.GetCurrentPathRemainingDistance();
                if (remainingDistance > 200 && pathToClosestTask.Distance > remainingDistance)
                {
                    Logger.LogWatchTask($"TASKM AVOID SNAP", watch.ElapsedMilliseconds);
                    return;
                }
            }

            // Detect big detours
            if (pathToClosestTask.Distance > myPosition.DistanceTo(closestTask.Location) * 2)
            {
                int closestTaskPriorityScore = CalculatePriority(myPosition, spaceTree, closestTask);

                for (int i = 0; i < _taskPile.Count - 1; i++)
                {

                    if (i > 1) break;

                    // (Perf) The old `WAQPath pathToNewTask = _taskPile[i].LongPathToTask` was computed here and
                    // NEVER used. LongPathToTask is a LIVE cross-zone pathfind, so on a long trek it spiked
                    // UpdateTaskPile to ~900ms. This detour pick compares PRIORITIES (which already factor distance
                    // via the straight-line/cluster scoring), not path length, so the path was dead weight. Removed.
                    int newTaskPriority = CalculatePriority(myPosition, spaceTree, _taskPile[i]);

                    if (newTaskPriority < closestTaskPriorityScore)
                    {
                        closestTaskPriorityScore = newTaskPriority;
                        closestTask = _taskPile[i];
                    }

                    if (closestTaskPriorityScore < _taskPile[i + 1].Location.DistanceTo(myPosition))
                        break;
                }
            }

            // only set new task on long distance if it's far apart from previous
            if (closestTask != null && ActiveTask != null
                && MovementManager.InMovement
                && WTPathFinder.GetCurrentPathRemainingDistance() > 200
                && ActiveTask.Location.DistanceTo(closestTask.Location) < 500)
            {
                Logger.LogWatchTask($"TASKM TOO CLOSE TO SWITCH", watch.ElapsedMilliseconds);
                return;
            }

            // Avoid task snap
            if (_snappedTasks.Count > 3) _snappedTasks.Clear();
            if (closestTask != ActiveTask)
            {
                if (_snappedTasks.TryGetValue(closestTask, out int amount))
                {
                    _snappedTasks[closestTask]++;
                    if (_snappedTasks[closestTask] > 3)
                    {
                        closestTask.PutTaskOnTimeout("Avoid back and forth", 30, true);
                        return;
                    }
                }
                else
                    _snappedTasks.Add(closestTask, 1);
            }

            Logger.LogWatchTask($"TASKM FOUND ACTIVE", watch.ElapsedMilliseconds);
            ActiveTask = closestTask;
        }

        // Batch turn-in (slice 1): radius around the PLAYER inside which still-open objective/pickup work holds a
        // turn-in back, so the bot drains the local cluster before making one batched return trip to the quest giver.
        private const float TurnInBatchRadius = 80.0f;

        // Region coalescing (slice 3): radius around the turn-in NPC's OWN location inside which still-open quest
        // work holds the turn-in back, so the bot finishes a hub/area and turns everything in on one visit instead
        // of returning per quest. Wider than the player cluster; set to 0 to disable slice 3 (slice 1 still runs).
        private const float RegionCoalesceRadius = 150.0f;

        // Counts actionable quest work (objectives + pickups; NOT turn-ins, NOT grind) within radius of a point.
        private static int CountOpenQuestWork(KDTree<float, IWAQTask> spaceTree, float x, float y, float z, float radius)
        {
            int count = 0;
            foreach (var (_, neighbour) in spaceTree.RadialSearch(new float[] { x, y, z }, radius))
            {
                if (neighbour.QuestId > 0 && !neighbour.IsTurnInQuest)
                    count++;
            }
            return count;
        }

        private int CalculatePriority(Vector3 myPosition, KDTree<float, IWAQTask> spaceTree, IWAQTask task)
        {
            // WRobot-bound facts gathered here; the scoring FORMULA lives in (unit-tested) TaskPriority.Compute.
            float taskDistance = myPosition.DistanceTo(task.Location);
            var neighbours = spaceTree.RadialSearch(new float[] { task.Location.X, task.Location.Y, task.Location.Z }, 64.0f);
            bool differentContinent = task.WorldMapArea.Continent != _continentManager.MyMapArea.Continent;

            // Hub harvesting: for a quest-giver pickup, count the pickable quest givers clustered with it (incl.
            // itself) so a town's quests get grabbed together instead of one-then-leave.
            int hubPickupNeighbours = 0;
            if (task.IsQuestGiverPickup)
            {
                foreach (var (_, neighbour) in neighbours)
                {
                    if (neighbour.IsQuestGiverPickup)
                        hubPickupNeighbours++;
                }
            }

            // Chain-aware scoring: a gateway pickup (one that unlocks more not-yet-done follow-ups) gets a
            // gentle boost so the bot prefers it - its chain then unlocks / fills the area.
            int chainValue = task.QuestId > 0 ? _questManager.GetChainValue(task.QuestId) : 0;

            // Batch turn-in / region coalescing: for a turn-in task, count still-open quest work (objectives +
            // pickups) both around the PLAYER (slice 1: finish the immediate cluster) and around the turn-in NPC's
            // own region (slice 3: finish a hub/area before turning in). The larger count drives one bounded
            // deferral penalty. Only paid for the handful of turn-in tasks per cycle.
            int turnInDeferralWork = 0;
            // Class quests (AllowableClasses>0) unlock important mechanics (totems, etc.) — NEVER defer their turn-in
            // behind a cluster of ordinary quests. Once a class quest is ready to hand in, get it done (Daniel).
            // The whole "class quest = forced priority" (Zwang) is gated behind the ClassQuestsEnabled setting so the
            // user can turn it off; when off, class quests are treated as ordinary quests here.
            bool classQuestForced = task.IsClassQuest && WholesomeAQSettings.CurrentSetting.ClassQuestsEnabled;
            if (task.IsTurnInQuest && !classQuestForced)
            {
                int workNearPlayer = CountOpenQuestWork(spaceTree, myPosition.X, myPosition.Y, myPosition.Z, TurnInBatchRadius);
                int workNearNpc = RegionCoalesceRadius > 0f
                    ? CountOpenQuestWork(spaceTree, task.Location.X, task.Location.Y, task.Location.Z, RegionCoalesceRadius)
                    : 0;
                turnInDeferralWork = TaskPriority.TurnInDeferralWork(workNearPlayer, workNearNpc);
            }

            return TaskPriority.Compute(taskDistance, neighbours.Length, task.SpatialWeight, task.PriorityShift, differentContinent, hubPickupNeighbours, chainValue, turnInDeferralWork, classQuestForced);
        }

        private double Distance(float[] x, float[] y)
        {
            double dist = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                dist += (x[i] - y[i]) * (x[i] - y[i]);
            }
            return dist;
        }

        private KDTree<float, IWAQTask> BuildTree(List<IWAQTask> tasks)
        {
            var tasksVectors = tasks.Select(x => new float[] { x.Location.X, x.Location.Y, x.Location.Z }).ToArray();
            var tasksArray = tasks.ToArray();
            return new KDTree<float, IWAQTask>(3, tasksVectors, tasksArray, Distance);
        }
    }
}
