using robotManager.Helpful;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    /// <summary>
    /// A coordinate-based task that USES a quest item at a fixed spot (e.g. the Shaman totem "use the waterskin at the
    /// water" ritual steps). Modelled on <see cref="WAQTaskExploreLocation"/>: it is NOT scanner-based (there is no
    /// world object to find — the spot is a bare coordinate) and reports <see cref="TaskInteraction.None"/>, so the
    /// travel is handled by WAQStateMoveToHotspot and the item-use by WAQStateUseItem once we're inside SearchRadius.
    /// Completion is game-driven: using the item flips the quest-log objective, and the objective-completion sweep in
    /// WAQQuest then drops this task from the pile.
    /// </summary>
    public class WAQTaskUseItem : WAQBaseTask
    {
        private readonly ModelQuestTemplate _questTemplate;

        public int ItemId { get; }
        public int CompleteItemId { get; }

        public WAQTaskUseItem(ModelQuestTemplate questTemplate, Vector3 location, int itemId, int completeItemId, int searchRadius, IContinentManager continentManager, int continent)
            : base(location, continent, $"Use item {itemId} for {questTemplate.LogTitle}", continentManager)
        {
            SearchRadius = searchRadius; // per-step tolerance (default 6y) so we stop OUTSIDE solid use-spots (shrine, etc.)
            _questTemplate = questTemplate;
            ItemId = itemId;
            CompleteItemId = completeItemId;
            IsClassQuest = questTemplate.QuestAddon?.AllowableClasses > 0;
            // Must outrank the turn-in task (PriorityShift 2): for "use item -> spawns the turn-in NPC" quests, the
            // quest is already ToTurnIn, so the turn-in task and this one coexist at the same spot. We MUST drink
            // first (to spawn the ender); once the item is consumed the scanner-forced turn-in takes over.
            PriorityShift = 8;
        }

        protected override string ReputationMismatch => _questTemplate.ReputationMismatch;
        protected override bool HasEnoughSkillForTask => true;
        protected override bool IsRecordedAsUnreachable => false;
        public override string TrackerColor => "Gold";
        public override bool IsObjectValidForTask(WoWObject wowObject) => throw new System.Exception($"Tried to scan for {TaskName}");
        public override void RegisterEntryToScanner(IWowObjectScanner scanner) { }
        public override void UnregisterEntryToScanner(IWowObjectScanner scanner) { }
        public override void PostInteraction(WoWObject wowObject) { }
        public override void RecordAsUnreachable() => throw new System.Exception($"Tried to record unreachable for {TaskName}");

        public override TaskInteraction InteractionType => TaskInteraction.None;
    }
}
