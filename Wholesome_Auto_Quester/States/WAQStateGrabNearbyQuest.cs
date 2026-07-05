using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Threading;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Opportunistic "grab a quest you're walking past" state. The scanner already activates the CLOSEST matching
    /// task object, and <see cref="WAQStateInteract"/> would interact with it — but that state sits below the travel
    /// states, so while TRAVELLING to a distant objective the bot walks straight past a nearer turn-in or quest giver.
    /// This state does the SAME interaction but at a higher priority than Travel, and ONLY when the active object is a
    /// TURN-IN or a QUEST-GIVER PICKUP within <see cref="GrabRange"/> yards — a quick "we're right here anyway" grab,
    /// then it yields (travel resumes).
    ///
    /// PRIORITY: placed BELOW Trainers / ToTown / the Wholesome Vendors states (see the FSM order in WAQBot), so a
    /// town errand — TRAINING on level-up, sell, repair — is NEVER preempted: after a ding the bot goes and trains
    /// instead of getting intercepted here into accepting quests and wandering off (the regression Talamin hit at 32).
    /// It also sits BELOW combat / loot / regen, so it never interrupts a fight or looting; and the scanner's own
    /// unreachable handling clears the active object if the NPC can't be reached, so it can't preempt forever.
    /// </summary>
    internal class WAQStateGrabNearbyQuest : State, IWAQState
    {
        private readonly IWowObjectScanner _scanner;

        /// <summary>Only grab a turn-in/pickup whose NPC is at most this far (straight line). Small enough that the
        /// path is usually ~the same, i.e. genuinely "on the way" rather than a detour.</summary>
        private const float GrabRange = 150f;

        /// <summary>Don't opportunistically grab a turn-in/pickup unless at least this many NORMAL bag slots are free.
        /// A turn-in hands us a reward item (and some pickups hand a "provided" quest item), so grabbing one with a
        /// (near-)full bag would leave no room for the reward — a fresh problem. Below the margin we walk on and let
        /// the normal flow / Vendors plugin free space first (it sells at &lt;= 2 free), then the turn-in happens later.</summary>
        private const int MinFreeBagSlots = 2;

        public override string DisplayName { get; set; } = "WAQ Grab nearby quest";

        public WAQStateGrabNearbyQuest(IWowObjectScanner scanner)
        {
            _scanner = scanner;
        }

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid)
                    return false;

                var (wowObject, task) = _scanner.ActiveWoWObject;
                if (wowObject == null || task == null)
                    return false;

                // Only the "right here" cases: a turn-in or a quest-giver pickup we can interact with, in range.
                if (!(task.IsTurnInQuest || task.IsQuestGiverPickup)
                    || task.InteractionType != TaskInteraction.Interact)
                    return false;

                if (wowObject.Position.DistanceTo(ObjectManager.Me.Position) > GrabRange)
                    return false;

                // Bag-space guard: never grab a reward-bearing turn-in/pickup with a (near-)full bag — there'd be no
                // room for the reward. Checked last so the Lua bag read only runs when an in-range grab is otherwise on.
                if (Bag.GetContainerNumFreeSlotsNormalType < MinFreeBagSlots)
                    return false;

                DisplayName = "WAQ grab: " + task.TaskName;
                return true;
            }
        }

        public override void Run()
        {
            var (wowObject, task) = _scanner.ActiveWoWObject;
            if (wowObject == null || task == null)
                return;

            if (ToolBox.ShouldStateBeInterrupted(task, wowObject) || ToolBox.HostilesAreAround(wowObject, task))
                return;

            Vector3 myPos = ObjectManager.Me.Position;
            float interactDistance = 3f + wowObject.Scale;

            if (MovementManager.CurrentPath.Count <= 2)
                ToolBox.CheckIfZReachable(wowObject.Position);

            if (wowObject.Position.DistanceTo(myPos) > interactDistance)
            {
                if (!MovementManager.InMovement)
                {
                    Logger.Log($"Grabbing nearby {wowObject.Name} for {task.TaskName}.");
                    MovementManager.Go(PathFinder.FindPath(wowObject.Position));
                }
                return;
            }

            MovementManager.StopMove();
            Thread.Sleep(200);
            Interact.InteractGameObject(wowObject.GetBaseAddress);
            Thread.Sleep(200);
            task.PostInteraction(wowObject);
            Thread.Sleep(1000);
        }
    }
}
