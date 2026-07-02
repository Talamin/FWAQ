using System.Collections.Generic;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;

namespace Wholesome_Auto_Quester.Bot.QuestManagement
{
    public interface IQuestManager : ICycleable
    {
        List<IWAQTask> GetAllValidQuestTasks();
        List<IWAQTask> GetAllInvalidQuestTasks();
        void ReloadQuestsFromDB(); // refresh the quest list now (e.g. after a live Grind-only toggle)
        int GetChainValue(int questId); // how many not-yet-done follow-up quests this one gates (chain scoring)
        public void AddQuestToBlackList(int questId, string reason, bool triggerStatusUpdate = true);
        public void RemoveQuestFromBlackList(int questId, string reason, bool triggerStatusUpdate = true);
    }
}
