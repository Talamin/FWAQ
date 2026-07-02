using robotManager.Helpful;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    public interface IWAQTask
    {
        Vector3 Location { get; }
        string TaskName { get; }
        ModelWorldMapArea WorldMapArea { get; }
        double SpatialWeight { get; }
        int PriorityShift { get; }
        bool IsQuestGiverPickup { get; } // true only for "pick up quest from NPC/GO" tasks (hub harvesting)
        bool IsTurnInQuest { get; } // true only for "turn in quest to NPC/GO" tasks (batch turn-in deferral)
        bool IsClassQuest { get; } // true if the quest is class-restricted (AllowableClasses>0) — never defer its turn-in
        int QuestId { get; } // the quest this task belongs to, or 0 (used for chain-aware scoring)
        string TrackerColor { get; }
        int SearchRadius { get; } // limit for MoveToHotspot state
        TaskInteraction InteractionType { get; }
        bool IsValid { get; }
        string InvalidityReason { get; }
        WAQPath LongPathToTask { get; }

        void PostInteraction(WoWObject wowObject);
        void RegisterEntryToScanner(IWowObjectScanner scanner);
        void UnregisterEntryToScanner(IWowObjectScanner scanner);
        void PutTaskOnTimeout(string reason, int timeInSeconds = 0, bool exponentiallyLonger = false);
        bool IsObjectValidForTask(WoWObject wowObject);
        void RecordAsUnreachable();
    }
}