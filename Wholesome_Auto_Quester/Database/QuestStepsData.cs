using Newtonsoft.Json;
using robotManager.Helpful;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Wholesome_Auto_Quester.Helpers;

namespace Wholesome_Auto_Quester.Database
{
    /// <summary>
    /// One DB-derived "how to do this quest step" entry for a quest (of ANY kind — not just class quests) whose
    /// objective WAQ cannot fully derive on its own. Actions: "use-item" (use an item at a coord), "explore" (reach an
    /// area), "use-item-on-npc" (use an item on a world-spawned creature). Coords come from quest_poi / the spell-focus
    /// GameObject; items + mechanic from the DB (item_template on-use) and the old EasyQuest profiles.
    /// </summary>
    public class QuestStep
    {
        public int QuestId { get; set; }
        public string Action { get; set; } = "use-item"; // "use-item" (at a coord), "explore" (reach an area), "use-item-on-npc"
        public int TargetEntry { get; set; }             // for "use-item-on-npc": the creature entry to use the item on
        public int ItemId { get; set; }                  // the item to USE at the location
        public int CompleteItemId { get; set; }          // the item USING it grants (for diagnostics only)
        public string ItemName { get; set; }             // exact in-game name of ItemId (for the Do-Not-Sell guard)
        public string CompleteItemName { get; set; }     // exact in-game name of CompleteItemId (Do-Not-Sell guard)
        public int ObjectiveIndex { get; set; } = 1;     // 1-based quest-log objective this step satisfies
        public int Map { get; set; }                     // WoW continent/map id (0 EK, 1 Kalimdor, 530 Outland, 571 Northrend)
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int Tolerance { get; set; }               // how close (yards) we must get to the spot to use the item
        public string Comment { get; set; }

        public Vector3 GetPosition => new Vector3(X, Y, Z);
        // Default 6y: many use-spots are solid objects (e.g. the "Shaman Shrine" Goober GO) — stop OUTSIDE them and use
        // the item from there, instead of pathfinding into the object and getting stuck (Talamin). Override per step.
        public int UseRadius => Tolerance > 0 ? Tolerance : 6;
    }

    /// <summary>
    /// Static DB-derived quest-steps table, loaded from the embedded Database/QuestSteps.json. Mirrors the
    /// <see cref="QuestBlacklistData"/> loader pattern (data out of code, single source of truth).
    /// </summary>
    public static class QuestStepsData
    {
        private const string ResourceName = "Wholesome_Auto_Quester.Database.QuestSteps.json";
        private static readonly List<QuestStep> _empty = new List<QuestStep>();
        private static List<QuestStep> _entries;
        private static Dictionary<int, List<QuestStep>> _byQuest;

        public static List<QuestStep> Entries
        {
            get
            {
                EnsureLoaded();
                return _entries;
            }
        }

        /// <summary>All steps for a given quest, or an empty list if none are curated.</summary>
        public static List<QuestStep> GetSteps(int questId)
        {
            EnsureLoaded();
            return _byQuest.TryGetValue(questId, out List<QuestStep> list) ? list : _empty;
        }

        private static void EnsureLoaded()
        {
            if (_entries != null)
                return;

            _entries = new List<QuestStep>();
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        Logger.LogError($"[QuestSteps] embedded resource {ResourceName} not found");
                    }
                    else
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            _entries = JsonConvert.DeserializeObject<List<QuestStep>>(reader.ReadToEnd())
                                ?? new List<QuestStep>();
                        }
                    }
                }
                Logger.Log($"Loaded {_entries.Count} quest steps from data");
            }
            catch (Exception e)
            {
                Logger.LogError($"[QuestSteps] failed to load: {e}");
                _entries = new List<QuestStep>();
            }

            _byQuest = _entries
                .GroupBy(e => e.QuestId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }
}
