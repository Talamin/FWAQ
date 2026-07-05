using robotManager.FiniteStateMachine;
using System.Collections.Generic;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    class WAQStateLoot : State, IWAQState
    {
        private readonly IWowObjectScanner _scanner;

        public WAQStateLoot(IWowObjectScanner scanner)
        {
            _scanner = scanner;
        }

        public override string DisplayName { get; set; } = "WAQ Loot";

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || _scanner.ActiveWoWObject.wowObject == null
                    || _scanner.ActiveWoWObject.task.InteractionType != TaskInteraction.KillAndLoot
                    || !ObjectManager.Me.IsValid)
                    return false;

                var (gameObject, task) = _scanner.ActiveWoWObject;
                if (!(gameObject is WoWUnit unitToLoot))
                {
                    return false;
                }

                if (unitToLoot.IsDead
                    && unitToLoot.IsLootable)
                {
                    DisplayName = task.TaskName;
                    return true;
                }

                return false;
            }
        }

        public override void Run()
        {
            var (gameObject, task) = _scanner.ActiveWoWObject;

            if (!(gameObject is WoWUnit lootTarget))
            {
                return;
            }

            if (ToolBox.HostilesAreAround(lootTarget, task))
            {
                return;
            }

            ToolBox.CheckIfZReachable(gameObject.Position);

            Logger.Log($"Looting {lootTarget.Name}");
            LootingTask.Pulse(new List<WoWUnit> { lootTarget });

            task.PostInteraction(lootTarget);

            // Some class-quest kills SPAWN their turn-in object a beat after the mob dies (e.g. the "Brazier of
            // Everfount" for Call of Water, like the Fire Pyre). Hold briefly so it appears + gets scanned before the
            // bot re-evaluates and wanders off to another quest (Talamin: the turn-in spawned ~1s after the kill and the
            // bot skipped it; a stop/restart then walked to it cleanly).
            if (task.IsClassQuest)
            {
                System.Threading.Thread.Sleep(3000);
            }
        }
    }
}
