using robotManager.Helpful;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Bot.JSONManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Database;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Database.Objectives;
using Wholesome_Auto_Quester.GUI;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.Logic;
using WholesomeToolbox;
using wManager;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using static wManager.Wow.Helpers.Quest.PlayerQuest;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.Bot.QuestManagement
{
    public class QuestManager : IQuestManager
    {
        private readonly IContinentManager _continentManager;
        private readonly IJSONManager _jSONManager;
        private readonly Dictionary<int, int> _itemsGivingQuest = new Dictionary<int, int>(); // item id => quest
        private readonly IWowObjectScanner _objectScanner;
        private readonly QuestsTrackerGUI _tracker;
        private readonly List<IWAQQuest> _questList = new List<IWAQQuest>();
        private readonly object _questManagerLock = new object();
        private Timer _itemCheckTimer = new Timer();
        // questId -> how many not-yet-done follow-up quests it gates. Rebuilt (whole dict swapped) in
        // UpdateStatuses; read lock-free by GetChainValue (the reference assignment is atomic).
        private Dictionary<int, int> _chainValues = new Dictionary<int, int>();

        public QuestManager(
            IWowObjectScanner objectScanner,
            QuestsTrackerGUI questTrackerGUI,
            IJSONManager jSONManager,
            IContinentManager continentManager)
        {
            questTrackerGUI.Initialize(this);
            _tracker = questTrackerGUI;
            _objectScanner = objectScanner;
            _jSONManager = jSONManager;
            _continentManager = continentManager;
            Initialize();
        }

        private void RemoveAllQuests(List<IWAQQuest> questsToRemove)
        {
            lock (_questManagerLock)
            {
                foreach (IWAQQuest questToRemove in questsToRemove)
                {
                    foreach (IWAQTask taskToUnRegister in questToRemove.GetAllTasks())
                    {
                        taskToUnRegister.UnregisterEntryToScanner(_objectScanner);
                    }
                    _questList.Remove(questToRemove);
                }
            }
        }

        /// <summary>Refresh the quest list from the DB on demand — used when GrindOnly is toggled live so the switch
        /// takes effect at once (the only other triggers are init / level-up / zone change). GetQuestsFromDB itself
        /// honours the current GrindOnly: it clears the list when ON, (re)loads it when OFF.</summary>
        public void ReloadQuestsFromDB()
        {
            GetQuestsFromDB();
        }

        private void GetQuestsFromDB()
        {
            lock (_questManagerLock)
            {
                if (WholesomeAQSettings.CurrentSetting.GrindOnly)
                {
                    _questList.Clear();
                    _tracker.UpdateQuestsList(GuiQuestList);
                    return;
                }

                List<ModelQuestTemplate> dbQuestTemplates = _jSONManager.GetAvailableQuestsFromJSON();

                // Remove quests that are not supposed to be here anymore
                List<IWAQQuest> questsToRemove = _questList.FindAll(quest => !dbQuestTemplates.Exists(dbQ => dbQ.Id == quest.QuestTemplate.Id));
                RemoveAllQuests(questsToRemove);

                // Add quests if they don't already exist
                foreach (ModelQuestTemplate qTemplate in dbQuestTemplates)
                {
                    if (!_questList.Exists(quest => quest.QuestTemplate.Id == qTemplate.Id))
                    {
                        _questList.Add(new WAQQuest(qTemplate, _objectScanner, _continentManager));
                    }

                    // Quest started by item
                    if (qTemplate.StartItemTemplate != null
                        && qTemplate.StartItemTemplate.startquest > 0
                        && qTemplate.Id == qTemplate.StartItemTemplate.startquest
                        && !Quest.HasQuest(qTemplate.Id)
                        && !_itemsGivingQuest.ContainsKey(qTemplate.StartItemTemplate.Entry))
                    {
                        _itemsGivingQuest.Add(qTemplate.StartItemTemplate.Entry, qTemplate.Id);
                    }
                }

                UpdateStatuses();
            }
        }

        public void Initialize()
        {
            InitializeWAQSettings();
            GetQuestsFromDB();
            EventsLuaWithArgs.OnEventsLuaStringWithArgs += LuaEventHandler;
        }

        public void Dispose()
        {
            EventsLuaWithArgs.OnEventsLuaStringWithArgs -= LuaEventHandler;
            lock (_questManagerLock)
            {
                _questList.Clear();
            }
        }

        private void LuaEventHandler(string eventid, List<string> args)
        {
            switch (eventid)
            {
                case "QUEST_LOG_UPDATE":
                    Logger.LogDebug("QUEST_LOG_UPDATE");
                    UpdateStatuses();
                    break;
                case "QUEST_QUERY_COMPLETE":
                    UpdateCompletedQuests();
                    break;
                case "BAG_UPDATE":
                    CheckInventory();
                    break;
                case "PLAYER_LEVEL_UP":
                    if (ObjectManager.Me.Level < WholesomeAQSettings.CurrentSetting.StopAtLevel)
                    {
                        GetQuestsFromDB();
                    }
                    break;
                case "PLAYER_ENTERING_WORLD":
                    GetQuestsFromDB();
                    break;
            }
        }

        public List<IWAQTask> GetAllValidQuestTasks()
        {
            lock (_questManagerLock)
            {
                List<IWAQTask> allTasks = new List<IWAQTask>();
                foreach (IWAQQuest quest in _questList)
                {
                    allTasks.AddRange(quest.GetAllValidTasks());
                }
                return allTasks;
            }
        }

        public List<IWAQTask> GetAllInvalidQuestTasks()
        {
            lock (_questManagerLock)
            {
                List<IWAQTask> allTasks = new List<IWAQTask>();
                foreach (IWAQQuest quest in _questList)
                {
                    allTasks.AddRange(quest.GetAllInvalidTasks());
                }
                return allTasks;
            }
        }

        // Gateway value per quest = how many not-yet-done, in-our-DB quests it unlocks downstream (see
        // QuestChain). Call under _questManagerLock; swaps in a fresh immutable dict for lock-free reads.
        private void RecomputeChainValues()
        {
            Dictionary<int, ModelQuestTemplate> questsById = new Dictionary<int, ModelQuestTemplate>(_questList.Count);
            foreach (IWAQQuest quest in _questList)
                questsById[quest.QuestTemplate.Id] = quest.QuestTemplate;

            Dictionary<int, int> values = new Dictionary<int, int>(questsById.Count);
            foreach (KeyValuePair<int, ModelQuestTemplate> entry in questsById)
            {
                values[entry.Key] = QuestChain.DownstreamUnlockCount(
                    entry.Key,
                    qid => questsById.TryGetValue(qid, out ModelQuestTemplate t) ? t.NextQuestsIds : null,
                    ToolBox.IsQuestCompleted,
                    questsById.ContainsKey);
            }
            _chainValues = values;
        }

        public int GetChainValue(int questId)
        {
            Dictionary<int, int> map = _chainValues; // local copy of the reference (atomic) - lock-free read
            return map.TryGetValue(questId, out int value) ? value : 0;
        }

        private void CheckInventory()
        {
            if (!_itemCheckTimer.IsReady)
            {
                return;
            }
            _itemCheckTimer = new Timer(1000);

            lock (_questManagerLock)
            {
                List<WoWItem> bagItems = Bag.GetBagItem();

                // Get quest items from DoNotSellList
                List<string> dnsList = wManagerSetting.CurrentSetting.DoNotSellList;
                int WAQlistStartIndex = dnsList.IndexOf("WAQStart");
                int WAQlistEndIndex = dnsList.IndexOf("WAQEnd");
                int WAQListLength = WAQlistEndIndex - WAQlistStartIndex - 1;
                List<string> listQuestItems = dnsList.GetRange(WAQlistStartIndex + 1, WAQListLength);

                // Check for deprecated quest items
                foreach (WoWItem item in bagItems)
                {
                    if (item.GetItemInfo.ItemType == "Quest"
                        && item.GetItemInfo.ItemSubType == "Quest"
                        && !listQuestItems.Contains(item.Name)
                        && !_itemsGivingQuest.ContainsKey(item.Entry))
                    {
                        Logger.Log($"Deleting item {item.Name} because it's a deprecated quest item");
                        WTItem.DeleteItemByName(item.Name);
                        Thread.Sleep(300);
                    }
                }

                // Check items that give quests
                int itemFound = 0;
                foreach (KeyValuePair<int, int> entry in _itemsGivingQuest)
                {
                    if (bagItems.Exists(item => item.Entry == entry.Key))
                    {
                        IWAQQuest questToPick = _questList
                            .Find(quest => quest.QuestTemplate.Id == entry.Value
                                && quest.Status == QuestStatus.ToPickup);

                        if (questToPick != null)
                        {
                            Logger.Log($"Starting {questToPick.QuestTemplate.LogTitle} from {questToPick.QuestTemplate.StartItemTemplate.Name}");
                            WTItem.PickupQuestFromBagItem(questToPick.QuestTemplate.StartItemTemplate.Name);
                            _itemsGivingQuest.Remove(itemFound);
                            break;
                        }
                    }
                }
            }
        }

        private void UpdateStatuses()
        {
            Dictionary<int, Quest.PlayerQuest> logQuests = Quest.GetLogQuestId().ToDictionary(quest => quest.ID);
            List<string> itemsToAddToDNSList = new List<string>();

            lock (_questManagerLock)
            {
                // First loop on newly completed quests to ensure pickup unlocks.
                // A quest we held as ToTurnIn that vanished from the log was EITHER turned in OR abandoned.
                // Only the server's finished-set proves an actual turn-in. Without that guard, "gone from log"
                // wrongly records ABANDONED quests as completed - especially 0-objective talk/use quests that
                // hit ToTurnIn on pickup (e.g. shaman 1530 "Find Brine") - which poisons ListCompletedQuests
                // and falsely satisfies downstream prereqs (1535 thought 1530 done -> Brine phantom loop).
                // If it left the log without being finished, the ladder below re-derives the real status.
                foreach (IWAQQuest quest in _questList)
                {
                    if (quest.Status == QuestStatus.ToTurnIn
                        && !logQuests.ContainsKey(quest.QuestTemplate.Id)
                        && Quest.FinishedQuestSet.Contains(quest.QuestTemplate.Id))
                    {
                        quest.ChangeStatusTo(QuestStatus.Completed);
                        continue;
                    }
                }

                // Update loop - the per-quest status DECISION is now a pure, unit-tested function
                // (QuestStatusLadder.Decide). Here we only gather the WRobot facts, apply the decided status,
                // and keep the side effect (Do-Not-Sell list) that depends on it.
                foreach (IWAQQuest quest in _questList)
                {
                    QuestStatusLadder.QuestDecision decision = QuestStatusLadder.DecideWithReason(BuildStatusInput(quest, logQuests));
                    QuestStatus newStatus = decision.Status;

                    // Items of quests we will interact with must not be sold / mailed.
                    if (newStatus == QuestStatus.ToPickup
                        || newStatus == QuestStatus.ToTurnIn
                        || newStatus == QuestStatus.InProgress)
                    {
                        itemsToAddToDNSList.AddRange(GetItemsStringsList(quest));
                    }

                    // Pass the reason so the status-change log explains WHY (especially the otherwise-silent None).
                    quest.ChangeStatusTo(newStatus, decision.Reason);
                }

                ToolBox.UpdateObjectiveCompletionDict(_questList
                    .Where(quest => quest.Status == QuestStatus.InProgress)
                    .Select(quest => quest.QuestTemplate.Id).ToArray());

                // Chain-aware scoring: recompute each quest's gateway value (completion changed here).
                RecomputeChainValues();

                // loop for clearing up finished objectives
                foreach (IWAQQuest quest in _questList)
                {
                    quest.CheckForFinishedObjectives();
                }

                // Second loop for unfit quests in the log
                if (WholesomeAQSettings.CurrentSetting.AbandonUnfitQuests)
                {
                    foreach (KeyValuePair<int, Quest.PlayerQuest> logQuest in logQuests)
                    {
                        IWAQQuest waqQuest = _questList.Find(q => q.QuestTemplate.Id == logQuest.Key);
                        if (waqQuest == null)
                        {
                            AbandonQuest(logQuest.Key, "Quest not in our DB list");
                        }
                        else
                        {
                            if (logQuest.Value.State == StateFlag.Failed)
                            {
                                AbandonQuest(waqQuest.QuestTemplate.Id, "Failed");
                                AddQuestToBlackList(waqQuest.QuestTemplate.Id, "Failed");
                                continue;
                            }
                            // A use-item class-quest step (data in ClassQuestSteps.json) has NO DB-derivable objective,
                            // so GetAllObjectives() is empty for it — but it is NOT unfit: WAQTaskUseItem drives it.
                            // Without this guard such quests get abandoned + blacklisted the instant they're picked up.
                            if (logQuest.Value.State == StateFlag.None
                                && waqQuest.GetAllObjectives().Count <= 0
                                && ClassQuestStepsData.GetSteps(waqQuest.QuestTemplate.Id).Count <= 0)
                            {
                                AbandonQuest(waqQuest.QuestTemplate.Id, "In progress with no objectives");
                                AddQuestToBlackList(waqQuest.QuestTemplate.Id, "In progress with no objectives");
                                continue;
                            }
                            if (waqQuest.QuestTemplate.QuestLevel < ObjectManager.Me.Level - WholesomeAQSettings.CurrentSetting.LevelDeltaMinus - 1
                                && (waqQuest.QuestTemplate.QuestAddon == null || waqQuest.QuestTemplate.QuestAddon.AllowableClasses == 0))
                            {
                                AbandonQuest(waqQuest.QuestTemplate.Id, "Underleveled");
                                AddQuestToBlackList(waqQuest.QuestTemplate.Id, "Underleveled");
                                continue;
                            }
                        }
                    }
                }

                // WAQ Do Not Sell List
                int WAQlistStartIndex = wManagerSetting.CurrentSetting.DoNotSellList.IndexOf("WAQStart");
                int WAQlistEndIndex = wManagerSetting.CurrentSetting.DoNotSellList.IndexOf("WAQEnd");
                int WAQListLength = WAQlistEndIndex - WAQlistStartIndex - 1;
                List<string> initialWAQList = wManagerSetting.CurrentSetting.DoNotSellList.GetRange(WAQlistStartIndex + 1, WAQListLength);
                if (!initialWAQList.SequenceEqual(itemsToAddToDNSList))
                {
                    foreach (string item in initialWAQList)
                    {
                        if (!itemsToAddToDNSList.Contains(item))
                        {
                            Logger.LogDebug($"Removed {item} from Do Not Sell List");
                        }
                    }
                    foreach (string item in itemsToAddToDNSList)
                    {
                        if (!initialWAQList.Contains(item))
                        {
                            Logger.LogDebug($"Added {item} to Do Not Sell List");
                        }
                    }
                    wManagerSetting.CurrentSetting.DoNotSellList.RemoveRange(WAQlistStartIndex + 1, WAQListLength);
                    wManagerSetting.CurrentSetting.DoNotSellList.InsertRange(WAQlistStartIndex + 1, itemsToAddToDNSList);

                    // WAQ Do not mail list
                    WAQlistStartIndex = wManagerSetting.CurrentSetting.DoNotMailList.IndexOf("WAQStart");
                    WAQlistEndIndex = wManagerSetting.CurrentSetting.DoNotMailList.IndexOf("WAQEnd");
                    WAQListLength = WAQlistEndIndex - WAQlistStartIndex - 1;
                    wManagerSetting.CurrentSetting.DoNotMailList.RemoveRange(WAQlistStartIndex + 1, WAQListLength);
                    wManagerSetting.CurrentSetting.DoNotMailList.InsertRange(WAQlistStartIndex + 1, itemsToAddToDNSList);

                    wManagerSetting.CurrentSetting.Save();
                }

                _tracker.UpdateQuestsList(GuiQuestList);
            }
        }

        private void AbandonQuest(int questId, string reason)
        {
            Logger.Log($"Abandonning quest {questId} ({reason})");
            WTQuestLog.AbandonQuest(questId);
            Thread.Sleep(500);
        }

        private void UpdateCompletedQuests()
        {
            lock (_questManagerLock)
            {
                if (Quest.FinishedQuestSet.Count > 0)
                {
                    List<int> questsSavedFromServer = new List<int>();
                    foreach (int questId in Quest.FinishedQuestSet)
                    {
                        if (ToolBox.SaveQuestAsCompleted(questId))
                        {
                            questsSavedFromServer.Add(questId);
                        }
                    }

                    if (questsSavedFromServer.Count > 0)
                    {
                        List<IWAQQuest> questsToRemove = _questList.FindAll(quest => questsSavedFromServer.Contains(quest.QuestTemplate.Id));
                        RemoveAllQuests(questsToRemove);
                        _tracker.UpdateQuestsList(GuiQuestList);
                        UpdateStatuses();
                        WholesomeAQSettings.CurrentSetting.Save();
                    }
                    return;
                }
                Logger.LogDebug($"Server has not sent our quests yet");
            }
        }

        public void AddQuestToBlackList(int questId, string reason, bool triggerStatusUpdate = true)
        {
            lock (_questManagerLock)
            {
                if (!WholesomeAQSettings.CurrentSetting.BlackListedQuests.Exists(blq => blq.Id == questId))
                {
                    WholesomeAQSettings.CurrentSetting.BlackListedQuests.Add(new BlackListedQuest(questId, reason));
                    WholesomeAQSettings.CurrentSetting.Save();
                    Logger.Log($"The quest {questId} has been blacklisted ({reason})");
                    MovementManager.StopMove();
                    if (triggerStatusUpdate)
                    {
                        UpdateStatuses();
                    }
                }
            }
        }

        public void RemoveQuestFromBlackList(int questId, string reason, bool triggerStatusUpdate = true)
        {
            lock (_questManagerLock)
            {
                BlackListedQuest questToRemove = WholesomeAQSettings.CurrentSetting.BlackListedQuests.Find(blq => blq.Id == questId);
                if (questToRemove.Id != 0)
                {
                    WholesomeAQSettings.CurrentSetting.BlackListedQuests.Remove(questToRemove);
                    WholesomeAQSettings.CurrentSetting.Save();
                    MovementManager.StopMove();
                    Logger.Log($"The quest {questId} has been removed from the blacklist ({reason})");
                    if (triggerStatusUpdate)
                    {
                        UpdateStatuses();
                    }
                }
            }
        }

        // Gathers the WRobot facts about one quest into a plain input for the pure QuestStatusLadder.
        private QuestStatusInput BuildStatusInput(IWAQQuest quest, Dictionary<int, Quest.PlayerQuest> logQuests)
        {
            int questId = quest.QuestTemplate.Id;

            QuestLogState logState = QuestLogState.NotInLog;
            if (logQuests.TryGetValue(questId, out Quest.PlayerQuest logQuest))
            {
                switch (logQuest.State)
                {
                    case StateFlag.Complete:
                        logState = QuestLogState.Complete;
                        break;
                    case StateFlag.Failed:
                        logState = QuestLogState.Failed;
                        break;
                    default:
                        logState = QuestLogState.InProgress;
                        break;
                }
            }

            bool exclusiveGroupSatisfied = false;
            if (quest.QuestTemplate.QuestAddon?.ExclusiveGroup > 0 && logState == QuestLogState.NotInLog)
            {
                exclusiveGroupSatisfied = quest.QuestTemplate.QuestAddon.ExclusiveQuests.Any(qId =>
                    qId != questId && (ToolBox.IsQuestCompleted(qId) || logQuests.ContainsKey(qId)));
            }

            string notPickableReason = NotPickableReason(quest);

            return new QuestStatusInput
            {
                DbConditionsMet = quest.AreDbConditionsMet,
                IsBlacklisted = quest.IsQuestBlackListed,
                ExclusiveGroupSatisfied = exclusiveGroupSatisfied,
                IsCompleted = ToolBox.IsQuestCompleted(questId),
                IsPickable = notPickableReason == null,
                NotPickableReason = notPickableReason,
                LogState = logState,
            };
        }

        // Returns WHY the quest can't be picked up yet (prerequisite chain / required skill), or null if it can.
        // The WRobot facts are gathered here; the decision lives in the unit-tested QuestPrerequisites.
        private string NotPickableReason(IWAQQuest quest)
        {
            ModelQuestTemplateAddon addon = quest.QuestTemplate.QuestAddon;
            bool requiresSkill = addon != null && addon.RequiredSkillID > 0;
            int playerSkillValue = requiresSkill ? (int)Skill.GetValue((SkillLine)addon.RequiredSkillID) : 0;
            int requiredSkillPoints = addon?.RequiredSkillPoints ?? 0;

            return QuestPrerequisites.ReasonNotPickable(
                quest.QuestTemplate.PreviousQuestsIds,
                ToolBox.IsQuestCompleted,
                requiresSkill,
                playerSkillValue,
                requiredSkillPoints);
        }

        private void InitializeWAQSettings()
        {
            // The static quest blacklist now lives in Database/QuestBlacklist.json (embedded resource).
            // Faction-specific entries ("Horde"/"Alliance") apply only to that faction; "Both" always.
            bool isHorde = WTPlayer.IsHorde();
            foreach (QuestBlacklistEntry blacklistEntry in QuestBlacklistData.Entries)
            {
                if (blacklistEntry.Faction == "Horde" && !isHorde) continue;
                if (blacklistEntry.Faction == "Alliance" && isHorde) continue;
                AddQuestToBlackList(blacklistEntry.Id, blacklistEntry.Reason, false);
            }

            // Quests we can now do via a ClassQuestStep (use-item at a location) must never remain blacklisted from an
            // earlier run that flagged them "in progress with no objectives". Clear them defensively on startup.
            foreach (ClassQuestStep step in ClassQuestStepsData.Entries)
            {
                RemoveQuestFromBlackList(step.QuestId, "Has a ClassQuestStep (use-item)", false);
            }

            if (!wManagerSetting.CurrentSetting.DoNotSellList.Contains("WAQStart") || !wManagerSetting.CurrentSetting.DoNotSellList.Contains("WAQEnd"))
            {
                wManagerSetting.CurrentSetting.DoNotSellList.Remove("WAQStart");
                wManagerSetting.CurrentSetting.DoNotSellList.Remove("WAQEnd");
                wManagerSetting.CurrentSetting.DoNotSellList.Add("WAQStart");
                wManagerSetting.CurrentSetting.DoNotSellList.Add("WAQEnd");
                wManagerSetting.CurrentSetting.Save();
            }

            if (!wManagerSetting.CurrentSetting.DoNotMailList.Contains("WAQStart") || !wManagerSetting.CurrentSetting.DoNotMailList.Contains("WAQEnd"))
            {
                wManagerSetting.CurrentSetting.DoNotMailList.Remove("WAQStart");
                wManagerSetting.CurrentSetting.DoNotMailList.Remove("WAQEnd");
                wManagerSetting.CurrentSetting.DoNotMailList.Add("WAQStart");
                wManagerSetting.CurrentSetting.DoNotMailList.Add("WAQEnd");
                wManagerSetting.CurrentSetting.Save();
            }
        }

        private List<string> GetItemsStringsList(IWAQQuest quest)
        {
            List<string> result = new List<string>();

            if (quest.QuestTemplate.StartItemTemplate != null)
            {
                result.Add(quest.QuestTemplate.StartItemTemplate.Name);
            }

            foreach (KillLootObjective klo in quest.QuestTemplate.KillLootObjectives)
            {
                if (!result.Contains(klo.ItemTemplate.Name))
                {
                    result.Add(klo.ItemTemplate.Name);
                }
            }

            foreach (KillLootObjective klo in quest.QuestTemplate.PrerequisiteLootObjectives)
            {
                if (!result.Contains(klo.ItemTemplate.Name))
                {
                    result.Add(klo.ItemTemplate.Name);
                }
            }

            foreach (GatherObjective go in quest.QuestTemplate.GatherObjectives)
            {
                if (!result.Contains(go.ItemTemplate.Name))
                {
                    result.Add(go.ItemTemplate.Name);
                }
            }

            foreach (GatherObjective go in quest.QuestTemplate.PrerequisiteGatherObjectives)
            {
                if (!result.Contains(go.ItemTemplate.Name))
                {
                    result.Add(go.ItemTemplate.Name);
                }
            }

            // Class-quest use-item steps: their consumable AND the item it turns into (e.g. Empty -> Filled Waterskin)
            // have NO derivable objective, so they never land in the loot lists above. Protect BOTH names on the
            // Do-Not-Sell list while the quest is active, or the Inventory-Manager plugin deletes the freshly-filled
            // item as a "deprecated" low-level quest item and the objective is lost (Daniel).
            foreach (ClassQuestStep step in ClassQuestStepsData.GetSteps(quest.QuestTemplate.Id))
            {
                if (!string.IsNullOrEmpty(step.ItemName) && !result.Contains(step.ItemName))
                {
                    result.Add(step.ItemName);
                }
                if (!string.IsNullOrEmpty(step.CompleteItemName) && !result.Contains(step.CompleteItemName))
                {
                    result.Add(step.CompleteItemName);
                }
            }

            return result;
        }

        private List<IWAQQuest> GuiQuestList
        {
            get
            {
                lock (_questManagerLock)
                {
                    Vector3 myPos = ObjectManager.Me.PositionWithoutType;
                    return _questList
                        .OrderBy(quest => quest.Status)
                        .ThenBy(quest =>
                        {
                            if (quest.QuestTemplate.CreatureQuestGivers.Count <= 0) return float.MaxValue;
                            return quest.GetClosestQuestGiverDistance(myPos);
                        }).ToList();
                }
            }
        }
    }
}
