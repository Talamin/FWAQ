using robotManager.FiniteStateMachine;
using System.Collections.Generic;
using System.Threading;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using wManager;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Runs a <see cref="WAQTaskUseItemOnCreature"/>: approach the scanned target, USE the quest item on it, then hand
    /// off. Sits ABOVE WAQStateKill so that — while the task is still in its "use the item" phase — the kill state
    /// doesn't engage first. After the item is used the task flips to KillAndLoot (hostile target → the normal kill/loot
    /// flow finishes it) or is benched briefly (friendly target → the objective-completion sweep drops it once the use
    /// credited the objective; if it didn't, we move on instead of looping).
    /// </summary>
    class WAQStateUseItemOnCreature : State, IWAQState
    {
        private readonly IWowObjectScanner _scanner;

        public WAQStateUseItemOnCreature(IWowObjectScanner scanner)
        {
            _scanner = scanner;
        }

        public override string DisplayName { get; set; } = "WAQ Use Item on Creature";

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || _scanner.ActiveWoWObject.wowObject == null
                    || _scanner.ActiveWoWObject.task.InteractionType != TaskInteraction.UseItemOnTarget
                    || !ObjectManager.Me.IsValid)
                    return false;

                DisplayName = _scanner.ActiveWoWObject.task.TaskName;
                return true;
            }
        }

        public override void Run()
        {
            var (gameObject, task) = _scanner.ActiveWoWObject;

            // GameObject variant ("use-item-on-go" steps): approach into use range and use the item NEAR the GO.
            // Quest items of this kind are spell-focus / area-targeted - proximity matters, not clicking the GO
            // (which could trigger the object's own use effect instead of the item's).
            if (gameObject is WoWGameObject goTarget && task is WAQTaskUseItemOnGameObject goTask)
            {
                RunOnGameObject(goTarget, goTask);
                return;
            }

            if (!(gameObject is WoWUnit target) || !(task is WAQTaskUseItemOnCreature useTask))
                return;

            if (ToolBox.HostilesAreAround(target, task))
                return;

            // Approach until within item-use range.
            if (target.Position.DistanceTo(ObjectManager.Me.Position) > 4.5f)
            {
                if (!ToolBox.IHaveLineOfSightOn(target))
                {
                    if (!MovementManager.InMovement)
                        MovementManager.Go(PathFinder.FindPath(target.Position));
                    return;
                }
                if (!MovementManager.InMovement)
                    MovementManager.Go(PathFinder.FindPath(target.Position));
                return;
            }

            MovementManager.StopMove();

            if (!ItemsManager.HasItemById((uint)useTask.ItemId))
            {
                // We don't (or no longer) hold the item. If the target is hostile, still finish it via the kill flow;
                // otherwise bench so the bot doesn't sit on a target it can't action.
                if (target.IsAttackable)
                    useTask.SwitchToKillLoot();
                else
                    useTask.PutTaskOnTimeout("No item to use on target", 60, true);
                return;
            }

            // Select the target and use the item on it.
            Interact.InteractGameObject(target.GetBaseAddress);
            Thread.Sleep(200);
            Logger.Log($"[UseItemOnCreature] Using item {useTask.ItemId} on {target.Name}");
            ItemsManager.UseItem((uint)useTask.ItemId);
            Usefuls.WaitIsCasting();
            Thread.Sleep(1500);

            // Hand off. Hostile: let the normal kill/loot flow finish it (the kill or the use will have credited the
            // objective; either way the sweep drops the task when done).
            if (target.IsAttackable)
            {
                useTask.SwitchToKillLoot();
            }
            else
            {
                // Friendly target (e.g. an awakened Lazy Peon): the use just credited THIS one. Blacklist its guid for
                // a bit so the scanner skips it and moves to the NEXT, still-sleeping peon. Re-hitting the same
                // already-awoken peon gives NO new credit - that (with the per-spawn task mapping onto the same few
                // nearby peons) is why it crawled at 1/5. We deliberately do NOT time out the TASK: the objective
                // needs several DIFFERENT peons, and the whole task list clears itself once the quest flips to
                // ToTurnIn. (Talamin's idea: use item -> briefly blacklist the peon -> move on.)
                wManagerSetting.AddBlackList(target.Guid, 30000, true);
                Logger.Log($"[UseItemOnCreature] {target.Name} awoken - blacklisted 30s, moving to next peon");
            }
        }

        private void RunOnGameObject(WoWGameObject target, WAQTaskUseItemOnGameObject useTask)
        {
            if (ToolBox.HostilesAreAround(target, useTask))
                return;

            if (target.Position.DistanceTo(ObjectManager.Me.Position) > 5f)
            {
                if (!MovementManager.InMovement)
                    MovementManager.Go(PathFinder.FindPath(target.Position));
                return;
            }

            MovementManager.StopMove();

            if (!ItemsManager.HasItemById((uint)useTask.ItemId))
            {
                useTask.PutTaskOnTimeout("No item to use on gameobject", 60, true);
                return;
            }

            Logger.Log($"[UseItemOnGO] Using item {useTask.ItemId} near {target.Name}");
            ItemsManager.UseItem((uint)useTask.ItemId);
            Usefuls.WaitIsCasting();
            Thread.Sleep(1500);

            // The use credited this GO (or it needs a respawn either way). Blacklist its guid briefly so the scanner
            // moves to the next spawn; the objective-completion sweep drops the whole task once the count is done.
            wManagerSetting.AddBlackList(target.Guid, 30000, true);
        }
    }
}
