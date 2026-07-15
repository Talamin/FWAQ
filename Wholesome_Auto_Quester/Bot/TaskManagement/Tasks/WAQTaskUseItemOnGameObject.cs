using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    /// <summary>
    /// "Use a quest item ON a target GameObject" (e.g. the Explosive Stick of Gann on the Bael Modan Flying Machine).
    /// Generated INSTEAD of the native interact task for the GO when a "use-item-on-go" step exists, so the scanner
    /// is unambiguous (same replacement pattern as <see cref="WAQTaskUseItemOnCreature"/>). The state
    /// (<see cref="States.WAQStateUseItemOnCreature"/>) walks into use range and uses the item near the GO - quest
    /// items of this kind are spell-focus / area-targeted, so proximity is what matters, not clicking the GO (which
    /// could trigger the object's OWN use effect instead). The objective-completion sweep drops the task once the
    /// use credited the objective.
    /// </summary>
    public class WAQTaskUseItemOnGameObject : WAQBaseScannableTask
    {
        private readonly ModelQuestTemplate _questTemplate;
        private readonly ModelGameObjectTemplate _gameObjectTemplate;

        public int ItemId { get; }

        public WAQTaskUseItemOnGameObject(ModelQuestTemplate questTemplate, ModelGameObjectTemplate goTemplate, ModelGameObject gameObject, int itemId, IContinentManager continentManager)
            : base(gameObject.GetSpawnPosition, gameObject.map, $"Use item {itemId} on {goTemplate.name} for {questTemplate.LogTitle}", goTemplate.entry,
                  gameObject.spawntimesecs, gameObject.guid, continentManager)
        {
            _questTemplate = questTemplate;
            _gameObjectTemplate = goTemplate;
            ItemId = itemId;
            PriorityShift = 2;
            if (_questTemplate.QuestAddon?.AllowableClasses > 0)
            {
                IsClassQuest = true;
            }
        }

        public new void PutTaskOnTimeout(string reason, int timeInSeconds, bool exponentiallyLonger)
            => base.PutTaskOnTimeout(reason, timeInSeconds > 0 ? timeInSeconds : DefaultTimeOutDuration, exponentiallyLonger);

        public override bool IsObjectValidForTask(WoWObject wowObject) => wowObject is WoWGameObject;

        public override void PostInteraction(WoWObject wowObject)
        {
            // Nothing to do - the state benches/blacklists after the item use; the objective sweep drops the task.
        }

        public override TaskInteraction InteractionType => TaskInteraction.UseItemOnTarget;
        public override string TrackerColor => "Gold";
        protected override bool HasEnoughSkillForTask => true;
        protected override string ReputationMismatch => _questTemplate.ReputationMismatch;
    }
}
