using Newtonsoft.Json;
using robotManager.Helpful;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Wholesome_Auto_Quester.Database;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.JSONManagement
{
    public class JSONManager : IJSONManager
    {
        private object _lock = new object();
        private bool _logFilter = false;
        private readonly FullJSONModel _fullJsonModel;
        private readonly Dictionary<int, JSONModelCreatureTemplate> _creatureTemplatesDic = new Dictionary<int, JSONModelCreatureTemplate>();
        private readonly Dictionary<int, JSONModelGameObjectTemplate> _gameObjectTemplatesDic = new Dictionary<int, JSONModelGameObjectTemplate>();
        private readonly Dictionary<int, JSONModelItemTemplate> _itemTemplatesDic = new Dictionary<int, JSONModelItemTemplate>();
        private readonly Dictionary<int, JSONModelSpell> _spellsDic = new Dictionary<int, JSONModelSpell>();
        private readonly Dictionary<int, JSONModelCreatureTemplate> _creatureTemplatesToGrindDic = new Dictionary<int, JSONModelCreatureTemplate>();

        /// <summary>Creature entries dropped from the grind list even though the DB ships them — holiday/critter mobs
        /// that are never a valid grind target and only pollute grind selection. Filtered out at load (below), so no
        /// consumer of CreaturesToGrind ever sees them. Add IDs here to exclude more.
        /// 32820 = "Wild Turkey" (a level-1 Pilgrim's Bounty critter).</summary>
        private static readonly HashSet<int> GrindExclusions = new HashSet<int> { 32820 };

        public JSONManager()
        {
            using (StreamReader reader = new StreamReader(Others.GetCurrentDirectory + @"Data\AQ.json"))
            {
                string jsonFile = reader.ReadToEnd();
                var settings = new JsonSerializerSettings
                {
                    Error = (sender, args) =>
                    {
                        Logger.LogError($"Deserialization error: {args.CurrentObject} => {args.ErrorContext.Error}");
                    }
                };

                _fullJsonModel = JsonConvert.DeserializeObject<FullJSONModel>(jsonFile, settings);
            }

            // Drop excluded grind creatures (e.g. Wild Turkey 32820) up front, so BOTH the dictionary below and
            // GetCreatureTemplatesToGrindFromJSON see the filtered list — the entry is effectively removed from
            // CreaturesToGrind without touching the embedded 26 MB AQ.zip.
            int removed = _fullJsonModel.CreaturesToGrind?.RemoveAll(c => GrindExclusions.Contains(c.entry)) ?? 0;
            if (removed > 0)
                Logger.Log($"Excluded {removed} creature(s) from the grind list ({string.Join(", ", GrindExclusions)}).");

            // fill dictionaries
            foreach (JSONModelCreatureTemplate jmct in _fullJsonModel.CreatureTemplates)
            {
                _creatureTemplatesDic.Add(jmct.entry, jmct);
            }
            foreach (JSONModelGameObjectTemplate jmGot in _fullJsonModel.GameObjectTemplates)
            {
                _gameObjectTemplatesDic.Add(jmGot.entry, jmGot);
            }
            foreach (JSONModelItemTemplate jmit in _fullJsonModel.ItemTemplates)
            {
                _itemTemplatesDic.Add(jmit.Entry, jmit);
            }
            foreach (JSONModelSpell jms in _fullJsonModel.Spells)
            {
                _spellsDic.Add(jms.Id, jms);
            }
            foreach (JSONModelCreatureTemplate jmct in _fullJsonModel.CreaturesToGrind)
            {
                _creatureTemplatesToGrindDic.Add(jmct.entry, jmct);
            }
        }

        public void Initialize()
        {
        }

        public void Dispose()
        {
        }

        public List<ModelWorldMapArea> GetWorldMapAreasFromJSON()
        {
            lock (_lock)
            {
                List<ModelWorldMapArea> result = new List<ModelWorldMapArea>();
                foreach (JSONModelWorldMapArea jmwma in _fullJsonModel.WorldMapAreas)
                {
                    result.Add(new ModelWorldMapArea(jmwma));
                }
                return result;
            }
        }

        public List<ModelCreatureTemplate> GetCreatureTemplatesToGrindFromJSON()
        {
            lock (_lock)
            {
                List<ModelCreatureTemplate> result = new List<ModelCreatureTemplate>();
                foreach (JSONModelCreatureTemplate jmct in _fullJsonModel.CreaturesToGrind)
                {
                    result.Add(new ModelCreatureTemplate(jmct, _creatureTemplatesToGrindDic));
                }
                return result;
            }
        }

        public List<ModelQuestTemplate> GetAvailableQuestsFromJSON()
        {
            lock (_lock)
            {
                List<JSONModelQuestTemplate> JSONquests = new List<JSONModelQuestTemplate>(_fullJsonModel.QuestTemplates);
                List<ModelQuestTemplate> result = new List<ModelQuestTemplate>();

                // Quest-density visibility: how many quests the level window (LevelDeltaPlus / LevelDeltaMinus) drops.
                // above = recoverable by raising LevelDeltaPlus; below = trimmable by lowering LevelDeltaMinus.
                int droppedAboveWindow = 0;
                int droppedBelowWindow = 0;
                int notYetMinLevel = 0;

                int levelDeltaMinus = System.Math.Max((int)ObjectManager.Me.Level - WholesomeAQSettings.CurrentSetting.LevelDeltaMinus, 1);
                int levelDeltaPlus = (int)ObjectManager.Me.Level + WholesomeAQSettings.CurrentSetting.LevelDeltaPlus;

                int myClass = (int)ToolBox.GetClass();
                int myFaction = (int)ToolBox.GetFaction();
                int myLevel = (int)ObjectManager.Me.Level;

                List<int> logQuestsIds = Quest.GetLogQuestId().Select(q => q.ID).ToList();

                // Quests to force get from the DB
                List<int> questsIdsToForce = new List<int>();
                if (WTPlayer.IsHorde())
                {
                    questsIdsToForce.Add(9407); // Through the dark portal
                }
                else
                {
                    questsIdsToForce.Add(10119); // Through the dark portal
                }

                // Load quest list from JSON
                foreach (JSONModelQuestTemplate jsonQuestTemplate in JSONquests)
                {
                    if (jsonQuestTemplate.MinLevel > myLevel)
                    {
                        notYetMinLevel++;
                        continue;
                    }

                    if (!questsIdsToForce.Contains(jsonQuestTemplate.Id)
                        && (jsonQuestTemplate.QuestLevel > levelDeltaPlus || jsonQuestTemplate.QuestLevel < levelDeltaMinus)
                        && jsonQuestTemplate.QuestLevel != -1
                        && (!logQuestsIds.Contains(jsonQuestTemplate.Id) || jsonQuestTemplate.QuestLevel > levelDeltaPlus))
                    {
                        //if (_logFilter) Logger.LogDebug($"[{jsonQuestTemplate.Id}] {jsonQuestTemplate.LogTitle} has been removed (invalid level)");
                        if (jsonQuestTemplate.QuestLevel > levelDeltaPlus) droppedAboveWindow++;
                        else droppedBelowWindow++;
                        continue;
                    }

                    if (myLevel < 60 && (jsonQuestTemplate.Id == 9407 || jsonQuestTemplate.Id == 10119))
                    {
                        if (_logFilter) Logger.LogDebug($"[{jsonQuestTemplate.Id}] {jsonQuestTemplate.LogTitle} has been removed (Wait lvl 60 for dark portal)");
                        continue;
                    }

                    if (jsonQuestTemplate.AllowableRaces > 0 && (jsonQuestTemplate.AllowableRaces & myFaction) == 0)
                    {
                        if (_logFilter) Logger.LogDebug($"[{jsonQuestTemplate.Id}] {jsonQuestTemplate.LogTitle} has been removed (Not for my race)");
                        continue;
                    }

                    ModelQuestTemplate quest = new ModelQuestTemplate(
                            jsonQuestTemplate,
                            _creatureTemplatesDic,
                            _gameObjectTemplatesDic,
                            _itemTemplatesDic,
                            _spellsDic
                        );

                    // Class quests (AllowableClasses>0) are always for the player's own faction (the race filter above
                    // already excluded the wrong faction) and unlock important mechanics — so NEVER strip their
                    // givers/enders on the faction reaction. That reaction was wrongly dropping the Shaman totem quest
                    // NPCs (Telf Joolam / Kranal Fiss), so WAQ never tracked "Call of Fire" and couldn't turn it in.
                    bool isClassQuest = quest.QuestAddon?.AllowableClasses > 0;
                    if (!isClassQuest)
                    {
                        quest.CreatureQuestGivers.RemoveAll(creature => !creature.IsNeutralOrFriendly);
                        quest.CreatureQuestEnders.RemoveAll(creature => !creature.IsNeutralOrFriendly);
                    }

                    if (quest.QuestAddon?.AllowableClasses > 0
                        && (quest.QuestAddon?.AllowableClasses & myClass) == 0)
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (Not for my class)");
                        continue;
                    }

                    if (quest.KillLootObjectives.Count > 0 && quest.KillLootObjectives.All(klo => !klo.CreatureLootTemplate.CreatureTemplate.IsValidForKill))
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (All enemies are invalid 0)");
                        continue;
                    }

                    if (quest.KillObjectives.Count > 0 && quest.KillObjectives.All(ko => !ko.CreatureTemplate.IsValidForKill))
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (All enemies are invalid 1)");
                        continue;
                    }

                    if (quest.KillLootObjectives.Any(klo => klo.ItemTemplate.Class != 12))
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (Kill loot objective is not quest item)");
                        continue;
                    }

                    // Count GAMEOBJECT givers/enders too — a quest handed in at a game object (e.g. the Fire totem
                    // "Brazier of the Dormant Flame", the ender of Call of Fire 1526) has no creature ender but is
                    // perfectly doable; the old check ignored GOs and dropped it. And never drop a class quest here.
                    if (!isClassQuest
                        && quest.CreatureQuestGivers.Count <= 0 && quest.CreatureQuestEnders.Count <= 0
                        && quest.GameObjectQuestGivers.Count <= 0 && quest.GameObjectQuestEnders.Count <= 0)
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (no quest giver, no quest turner)");
                        continue;
                    }

                    if (!isClassQuest
                        && quest.CreatureQuestGivers.Count > 0
                        && !quest.CreatureQuestGivers.Any(qg => qg.IsNeutralOrFriendly)
                        && quest.GameObjectQuestGivers.Count <= 0)
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (Not for my faction)");
                        continue;
                    }

                    // Quests whose start/drop item casts a spell (use-item quests) are normally dropped — WAQ couldn't
                    // do them. But a quest we have a curated ClassQuestStep for IS doable now (WAQTaskUseItem), so keep
                    // it. This is exactly what was filtering out the Shaman totem ritual quests (Earth Sapta, etc.).
                    if ((quest.StartItemTemplate != null && quest.StartItemTemplate.HasASpellAttached
                        || quest.ItemDrop1Template != null && quest.ItemDrop1Template.HasASpellAttached
                        || quest.ItemDrop2Template != null && quest.ItemDrop2Template.HasASpellAttached
                        || quest.ItemDrop3Template != null && quest.ItemDrop3Template.HasASpellAttached
                        || quest.ItemDrop4Template != null && quest.ItemDrop4Template.HasASpellAttached)
                        && ClassQuestStepsData.GetSteps(quest.Id).Count == 0)
                    {
                        if (_logFilter) Logger.LogDebug($"[{quest.Id}] {quest.LogTitle} has been removed (Active start/prerequisite item)");
                        continue;
                    }

                    if (quest.KillLootObjectives.Any(klo => klo.ItemTemplate.HasASpellAttached))
                    {
                        if (_logFilter) Logger.Log($"[{quest.Id}] {quest.LogTitle} has been removed (Active loot item)");
                        continue;
                    }

                    // TEMP diagnostic: confirm curated class-quest steps survive all filters, and that their
                    // giver/ender links were kept (giver count > 0 means the pickup task can be built).
                    if (ClassQuestStepsData.GetSteps(quest.Id).Count > 0)
                        Logger.Log($"[ClassQuest {quest.Id}] '{quest.LogTitle}' KEPT — givers={quest.CreatureQuestGivers.Count} enders={quest.CreatureQuestEnders.Count}");

                    result.Add(quest);
                }

                // One summary line per JSON (re)load - quantifies the level-window lever so Plus/Minus can be tuned
                // on data, not guesswork. 'above' = quests recoverable by raising LevelDeltaPlus.
                Logger.Log($"[Quest density] level window [{levelDeltaMinus}-{levelDeltaPlus}] (delta -{WholesomeAQSettings.CurrentSetting.LevelDeltaMinus}/+{WholesomeAQSettings.CurrentSetting.LevelDeltaPlus}): kept {result.Count}; level window dropped {droppedAboveWindow} above-level, {droppedBelowWindow} below-level; {notYetMinLevel} not-yet (min level too high)");

                if (WholesomeAQSettings.CurrentSetting.DevMode)
                {
                    Stopwatch stopwatchJSON = Stopwatch.StartNew();
                    Logger.Log($"Building Debug JSON ({result.Count} quests). Please wait...");
                    try
                    {
                        File.Delete(Others.GetCurrentDirectory + @"\Data\AQDebug.json");
                        using (StreamWriter file = File.CreateText(Others.GetCurrentDirectory + @"\Data\AQDebug.json"))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            serializer.ContractResolver = ShouldSerializeContractResolver.Instance;
                            serializer.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                            serializer.DefaultValueHandling = DefaultValueHandling.Ignore;
                            serializer.NullValueHandling = NullValueHandling.Ignore;
                            serializer.Serialize(file, result);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.LogError("WriteJSONFromDBResult > " + e.Message);
                    }
                    Logger.Log($"Process time (Debug JSON processing) : {stopwatchJSON.ElapsedMilliseconds} ms");
                }

                return result;
            }
        }
    }
}
