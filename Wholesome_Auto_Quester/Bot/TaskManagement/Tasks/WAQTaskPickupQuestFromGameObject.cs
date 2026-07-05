using System.Threading;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.DBC;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    public class WAQTaskPickupQuestFromGameObject : WAQBaseScannableTask
    {
        private ModelGameObjectTemplate _gameObjectTemplate;
        private ModelQuestTemplate _questTemplate;

        public WAQTaskPickupQuestFromGameObject(ModelQuestTemplate questTemplate, ModelGameObjectTemplate goTemplate, ModelGameObject gameObject, IContinentManager continentManager)
            : base(gameObject.GetSpawnPosition, gameObject.map, $"Pick up {questTemplate.LogTitle} from {goTemplate.name}", goTemplate.entry,
                  gameObject.spawntimesecs, gameObject.guid, continentManager)
        {
            _gameObjectTemplate = goTemplate;
            _questTemplate = questTemplate;

            SpatialWeight = 0.25;
            if (_questTemplate.QuestAddon?.AllowableClasses > 0)
            {
                PriorityShift = 2;
                IsClassQuest = true;
            }
            _questTemplate = questTemplate;
        }

        public new void PutTaskOnTimeout(string reason, int timeInSeconds, bool exponentiallyLonger)
            => base.PutTaskOnTimeout(reason, timeInSeconds > 0 ? timeInSeconds : DefaultTimeOutDuration, exponentiallyLonger);

        public override bool IsObjectValidForTask(WoWObject wowObject)
        {
            if (wowObject is WoWObject)
            {
                return true;
            }
            return false;
        }

        public override void PostInteraction(WoWObject wowObject)
        {
            Usefuls.WaitIsCastingAndLooting();
            WoWGameObject pickUpTarget = (WoWGameObject)wowObject;
            if (!WTGossip.IsQuestGiverFrameActive)
            {
                MovementManager.StopMove();
                Interact.InteractGameObject(pickUpTarget.GetBaseAddress);
                Usefuls.WaitIsCasting();
                if (!QuestLUAHelper.WaitForQuestGiverFrame())
                {
                    PutTaskOnTimeout($"Couldn't open quest frame", 20, true);
                }
            }
            else
            {
                if (!QuestLUAHelper.GossipPickupQuest(_questTemplate.LogTitle, _questTemplate.Id))
                {
                    // Frame opened but the object-giver didn't offer the quest: a core-scripted gate we have no data
                    // for. Record the refusal so the planner stops routing here until the next level-up (Talamin).
                    ToolBox.MarkQuestPickupRefused(_questTemplate.Id);
                    PutTaskOnTimeout("Failed pickup gossip", 15 * 60, true);
                }
            }
        }

        public override string TrackerColor => "DodgerBlue";
        public override TaskInteraction InteractionType => TaskInteraction.Interact;
        public override bool IsQuestGiverPickup => true;
        public override int QuestId => _questTemplate.Id;
        protected override string ReputationMismatch => _questTemplate.ReputationMismatch;
        protected override bool HasEnoughSkillForTask => DBCLocks.IsLockValid(_gameObjectTemplate.type, _gameObjectTemplate.Data0);
    }
}
