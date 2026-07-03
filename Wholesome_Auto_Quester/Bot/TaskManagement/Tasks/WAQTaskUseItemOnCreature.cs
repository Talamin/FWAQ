using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    /// <summary>
    /// "Use a quest item ON a target creature" (e.g. Morbent's Bane on Morbent, a net on a beast). This task is
    /// generated INSTEAD of the native kill task for the target, so the scanner is unambiguous (the scanner picks a
    /// task by Location-distance, not priority — two tasks on one creature would flip-flop). The bot uses the item
    /// first (<see cref="States.WAQStateUseItemOnCreature"/>); then, if the target is still up, it FLIPS to
    /// KillAndLoot so the normal kill/loot flow finishes a hostile target. If using the item was itself the credit
    /// (or the target turns friendly / despawns), the quest's objective-completion sweep drops this task.
    /// </summary>
    public class WAQTaskUseItemOnCreature : WAQBaseScannableTask
    {
        private readonly ModelQuestTemplate _questTemplate;
        private TaskInteraction _interaction = TaskInteraction.UseItemOnTarget;

        public int ItemId { get; }

        public WAQTaskUseItemOnCreature(ModelQuestTemplate questTemplate, ModelCreatureTemplate creatureTemplate, ModelCreature creature, int itemId, IContinentManager continentManager)
            : base(creature.GetSpawnPosition, creature.map, $"Use item {itemId} on {creatureTemplate.Name} for {questTemplate.LogTitle}", creatureTemplate.Entry,
                  creature.spawnTimeSecs, creature.guid, continentManager)
        {
            _questTemplate = questTemplate;
            ItemId = itemId;
            PriorityShift = 2;
            if (_questTemplate.QuestAddon?.AllowableClasses > 0)
            {
                IsClassQuest = true;
            }
        }

        /// <summary>Called by the state once the item has been used: hand off to the normal kill/loot flow.</summary>
        public void SwitchToKillLoot() => _interaction = TaskInteraction.KillAndLoot;

        public bool ItemUsed => _interaction != TaskInteraction.UseItemOnTarget;

        public new void PutTaskOnTimeout(string reason, int timeInSeconds, bool exponentiallyLonger)
            => base.PutTaskOnTimeout(reason, timeInSeconds > 0 ? timeInSeconds : DefaultTimeOutDuration, exponentiallyLonger);

        public override bool IsObjectValidForTask(WoWObject wowObject)
        {
            // Accept the target whether hostile or friendly (we may only need to USE the item on it), and when dead+lootable.
            if (wowObject is WoWUnit unit)
            {
                return unit.IsAlive || (unit.IsDead && unit.IsLootable);
            }
            return false;
        }

        public override void PostInteraction(WoWObject wowObject)
        {
            // Once flipped to KillAndLoot, mirror the native kill task's completion check.
            if (_interaction == TaskInteraction.KillAndLoot
                && wowObject is WoWUnit unit
                && unit.IsDead && !unit.IsLootable && unit.Position.DistanceTo(Location) < 30)
            {
                PutTaskOnTimeout("Completed");
            }
        }

        public override TaskInteraction InteractionType => _interaction;
        public override string TrackerColor => "Gold";
        protected override bool HasEnoughSkillForTask => true;
        protected override string ReputationMismatch => _questTemplate.ReputationMismatch;
    }
}
