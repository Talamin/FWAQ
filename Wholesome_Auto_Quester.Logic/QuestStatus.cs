// Quest status, shared between the pure decision logic (QuestStatusLadder) and the WRobot-bound
// QuestManager / WAQQuest. Kept in the GLOBAL namespace to match its original location (it used to live at
// the bottom of QuestManager.cs), so every existing consumer compiles unchanged.
//
// IMPORTANT: the numeric order is load-bearing - the quest tracker GUI sorts by (int)QuestStatus
// (OrderBy(quest => quest.Status)). Do not reorder these members.
public enum QuestStatus
{
    Unchecked,
    ToTurnIn,
    InProgress,
    ToPickup,
    DBConditionsNotMet,
    Failed,
    None,
    Completed,
    Blacklisted
}
