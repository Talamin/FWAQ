using System.Threading;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    public class WAQTaskPickupQuestFromCreature : WAQBaseScannableTask
    {
        private ModelQuestTemplate _questTemplate;

        public WAQTaskPickupQuestFromCreature(ModelQuestTemplate questTemplate, ModelCreatureTemplate creatureTemplate, ModelCreature creature, IContinentManager continentManager)
            : base(creature.GetSpawnPosition, creature.map, $"Pick up {questTemplate.LogTitle} from {creatureTemplate.Name}", creatureTemplate.Entry,
                  creature.spawnTimeSecs, creature.guid, continentManager)
        {
            _questTemplate = questTemplate;

            SpatialWeight = 0.25;
            if (_questTemplate.QuestAddon?.AllowableClasses > 0)
            {
                PriorityShift = 2;
                IsClassQuest = true;
            }
            // Through the Dark Portal
            if (_questTemplate.Id == 9407 || questTemplate.Id == 10119)
            {
                PriorityShift = 20;
            }
        }

        public new void PutTaskOnTimeout(string reason, int timeInSeconds, bool exponentiallyLonger)
            => base.PutTaskOnTimeout(reason, timeInSeconds > 0 ? timeInSeconds : DefaultTimeOutDuration, exponentiallyLonger);

        public override bool IsObjectValidForTask(WoWObject wowObject)
        {
            // Any unit here already matches THIS task's registered giver entry, so it IS our quest giver - accept
            // it alive or DEAD. Some givers are permanent corpses you interact with; the old hardcoded dead-entry
            // allow-list missed any not on it. A normal giver that's merely killed self-corrects via the bench.
            return wowObject is WoWUnit;
        }

        public override void PostInteraction(WoWObject wowObject)
        {
            WoWUnit pickUpTarget = (WoWUnit)wowObject;
            if (!WTGossip.IsQuestGiverFrameActive)
            {
                MovementManager.StopMove();
                Interact.InteractGameObject(pickUpTarget.GetBaseAddress);
                if (!QuestLUAHelper.WaitForQuestGiverFrame())
                {
                    PutTaskOnTimeout($"Couldn't open quest frame", 20, true);
                }
            }
            else
            {
                // A quest freshly unlocked by turning in its prerequisite can take a few seconds to appear in the
                // giver's gossip (server chain delay - repeatedly observed on the shaman totem chains, e.g. Brine
                // offering 1535 only seconds after 1530 was handed in). Re-open the gossip and retry a few times
                // while we're standing here, instead of benching for 15 min and letting the class-quest "Zwang"
                // drag us off to a far quest with the chain stalled.
                bool pickedUp = QuestLUAHelper.GossipPickupQuest(_questTemplate.LogTitle, _questTemplate.Id);
                for (int attempt = 0; !pickedUp && attempt < 3; attempt++)
                {
                    Lua.LuaDoString("CloseGossip()"); // drop the stale gossip list so re-interacting pulls a fresh one
                    Thread.Sleep(2000);
                    Interact.InteractGameObject(pickUpTarget.GetBaseAddress);
                    if (!QuestLUAHelper.WaitForQuestGiverFrame())
                    {
                        break;
                    }
                    pickedUp = QuestLUAHelper.GossipPickupQuest(_questTemplate.LogTitle, _questTemplate.Id);
                }

                if (!pickedUp)
                {
                    // The giver's frame opened but the quest wasn't in its offer list, even after the retries above:
                    // the server isn't offering it yet (a core-scripted gate we have NO data for - no PrevQuestID, no
                    // conditions row). Record the refusal so the planner stops routing here until the next level-up,
                    // instead of re-crossing the zone every time the short bench expires (Talamin: "learn from refusal").
                    ToolBox.MarkQuestPickupRefused(_questTemplate.Id);
                    PutTaskOnTimeout("Failed pickup gossip", 30, true);
                }
                else
                {
                    Thread.Sleep(1000);
                }
            }
        }

        protected override string ReputationMismatch => _questTemplate.ReputationMismatch;
        public override string TrackerColor => "DodgerBlue";
        public override TaskInteraction InteractionType => TaskInteraction.Interact;
        public override bool IsQuestGiverPickup => true;
        public override int QuestId => _questTemplate.Id;
        protected override bool HasEnoughSkillForTask => true;
    }
}
