using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Threading;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Runs a <see cref="WAQTaskUseItem"/>: once WAQStateMoveToHotspot has carried us inside the task's SearchRadius,
    /// this state uses the quest item at the spot. It sits ABOVE WAQStateMoveToHotspot in the FSM so that, while we're
    /// in range, MoveToHotspot never gets to time the task out as "couldn't find target" (its arrival-timeout is meant
    /// for scanned targets that turned out to be gone; a use-item spot has no scanned target).
    ///
    /// Completion is game-driven: using the item flips the quest-log objective, WAQQuest's objective-completion sweep
    /// then drops the task, ActiveTask changes and NeedToRun goes false. Guards against loops: if we never had the
    /// item, or repeated uses don't complete the objective, the task is benched with graduated recovery.
    /// </summary>
    class WAQStateUseItem : State, IWAQState
    {
        private readonly ITaskManager _taskManager;
        private WAQTaskUseItem _trackedTask;
        private int _attempts;
        private robotManager.Helpful.Timer _useThrottle = new robotManager.Helpful.Timer(); // ready immediately on first arrival

        private const int MaxAttempts = 4;

        public override string DisplayName { get; set; } = "WAQ Use Item";

        public WAQStateUseItem(ITaskManager taskManager)
        {
            _taskManager = taskManager;
        }

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid)
                    return false;

                // Broad on purpose: as soon as a use-item task is active AND we're at the spot, we own the tick — this
                // is what keeps the lower-priority MoveToHotspot from timing the task out while we work.
                if (_taskManager.ActiveTask is WAQTaskUseItem task
                    && task.IsValid
                    && task.Location.DistanceTo(ObjectManager.Me.Position) <= task.SearchRadius)
                {
                    DisplayName = task.TaskName;
                    return true;
                }

                return false;
            }
        }

        public override void Run()
        {
            if (!(_taskManager.ActiveTask is WAQTaskUseItem task))
                return;

            // New task since last time? Reset the per-task counters.
            if (!ReferenceEquals(task, _trackedTask))
            {
                _trackedTask = task;
                _attempts = 0;
                _useThrottle = new robotManager.Helpful.Timer(); // ready
            }

            MovementManager.StopMove();

            if (!ItemsManager.HasItemById((uint)task.ItemId))
            {
                if (_attempts == 0)
                {
                    // We arrived but never held the consumable — can't perform this step here. Bench it so the bot
                    // moves on instead of standing on the spot forever (graduated so we don't churn back every minute).
                    Logger.Log($"[UseItem] Missing item {task.ItemId} for {task.TaskName}; benching this step.");
                    task.PutTaskOnTimeout("Missing the item to use", 60 * 5, true);
                }
                else
                {
                    // We already used it (consumed). RELEASE this high-priority step so the follow-up takes over —
                    // either the NPC that just spawned HERE (scanner-forced turn-in, e.g. the Earth manifestation) or a
                    // turn-in at a DIFFERENT NPC/spot (e.g. back at the waterskin quest giver). Without releasing, this
                    // task would pin the bot to the use-spot forever.
                    Logger.Log($"[UseItem] {task.TaskName}: item consumed — handing off to the turn-in.");
                    task.PutTaskOnTimeout("Item used, handing off to turn-in", 90, false);
                }
                _trackedTask = null;
                return;
            }

            if (!_useThrottle.IsReady || WTItem.GetItemCooldown(task.ItemId) > 0)
                return;

            // Adaptive approach: the first use is tried from wherever MoveToHotspot dropped us (~SearchRadius, ~6y).
            // If a prior use did NOT consume the item we're most likely just short of the real spot (classic case:
            // standing on the shore of a "fill the waterskin at the water" point) — so step ONTO the exact hotspot
            // before retrying (GoToTask swims into the water if the fill-point is a swim node) instead of failing
            // repeatedly from the same distance. Generalises "use at 6y, close in if it doesn't take" without having
            // to hand-tune every coordinate.
            if (_attempts > 0)
            {
                float distToSpot = task.Location.DistanceTo(ObjectManager.Me.Position);
                if (distToSpot > 1.0f)
                {
                    Logger.Log($"[UseItem] {task.TaskName}: last use didn't take — closing in on the exact spot (was {distToSpot:F1}y).");
                    // Tight distanceToReach: the default (~4-5y) makes ToPosition think we already arrived (we're
                    // inside SearchRadius) and return WITHOUT moving. 1y makes it path as close as the navmesh allows.
                    GoToTask.ToPosition(task.Location, 1f);

                    // GoToTask follows the navmesh and STOPS at the collision edge of solid world objects (e.g. the
                    // Tarren Mill well the Red Waterskin fills at — it parks ~2.8y out and never gets closer). If a gap
                    // remains, drive STRAIGHT at the spot with MoveTo (no A*) to push the last couple of yards right up
                    // against it, so we land inside the game's use-range instead of failing from just too far.
                    if (task.Location.DistanceTo(ObjectManager.Me.Position) > 1.5f)
                    {
                        MovementManager.MoveTo(task.Location);
                        Thread.Sleep(1300);
                        Logger.Log($"[UseItem] {task.TaskName}: after direct MoveTo now {task.Location.DistanceTo(ObjectManager.Me.Position):F1}y from spot.");
                    }
                    MovementManager.StopMove();
                }
            }

            Logger.Log($"[UseItem] Using item {task.ItemId} at {task.Location} for {task.TaskName} (attempt {_attempts + 1}/{MaxAttempts})");
            Thread.Sleep(200);
            ItemsManager.UseItem((uint)task.ItemId);
            Usefuls.WaitIsCasting();
            // Some of these quests SPAWN the turn-in NPC when the item is used (e.g. the totem manifestations). Hold on
            // the spot for a beat so it appears + gets scanned before we yield; after this the item is consumed and we
            // simply wait here (StopMove) until the scanner-forced turn-in to the spawned NPC takes over.
            Thread.Sleep(3000);

            _attempts++;
            _useThrottle = new robotManager.Helpful.Timer(4000); // let the quest log update before another attempt

            if (_attempts >= MaxAttempts)
            {
                // Repeated uses didn't complete the objective — most likely the wrong spot. Bench and move on.
                Logger.Log($"[UseItem] {task.TaskName} still not complete after {MaxAttempts} uses; benching this step.");
                task.PutTaskOnTimeout("Use-item didn't complete the objective", 60 * 5, true);
                _trackedTask = null;
            }
        }
    }
}
