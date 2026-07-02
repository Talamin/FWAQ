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
    /// One curated "how to do this quest step" entry for a quest whose objective CANNOT be derived from the DB
    /// (no loot source / completed by using an item at a spot). The location comes from quest_poi, the item + mechanic
    /// from the old EasyQuest profiles. Currently only the "use-item" action is implemented (Phase 1 vertical slice).
    /// </summary>
    public class ClassQuestStep
    {
        public int QuestId { get; set; }
        public string Action { get; set; } = "use-item"; // only "use-item" is handled for now
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
        // the item from there, instead of pathfinding into the object and getting stuck (Daniel). Override per step.
        public int UseRadius => Tolerance > 0 ? Tolerance : 6;
    }

    /// <summary>
    /// Static "class-quest steps" table, loaded from the embedded Database/ClassQuestSteps.json. Mirrors the
    /// <see cref="QuestBlacklistData"/> loader pattern (data out of code, single source of truth).
    /// </summary>
    public static class ClassQuestStepsData
    {
        private const string ResourceName = "Wholesome_Auto_Quester.Database.ClassQuestSteps.json";
        private static readonly List<ClassQuestStep> _empty = new List<ClassQuestStep>();
        private static List<ClassQuestStep> _entries;
        private static Dictionary<int, List<ClassQuestStep>> _byQuest;

        public static List<ClassQuestStep> Entries
        {
            get
            {
                EnsureLoaded();
                return _entries;
            }
        }

        /// <summary>All steps for a given quest, or an empty list if none are curated.</summary>
        public static List<ClassQuestStep> GetSteps(int questId)
        {
            EnsureLoaded();
            return _byQuest.TryGetValue(questId, out List<ClassQuestStep> list) ? list : _empty;
        }

        private static void EnsureLoaded()
        {
            if (_entries != null)
                return;

            _entries = new List<ClassQuestStep>();
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        Logger.LogError($"[ClassQuestSteps] embedded resource {ResourceName} not found");
                    }
                    else
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            _entries = JsonConvert.DeserializeObject<List<ClassQuestStep>>(reader.ReadToEnd())
                                ?? new List<ClassQuestStep>();
                        }
                    }
                }
                Logger.Log($"Loaded {_entries.Count} class-quest steps from data");
            }
            catch (Exception e)
            {
                Logger.LogError($"[ClassQuestSteps] failed to load: {e}");
                _entries = new List<ClassQuestStep>();
            }

            _byQuest = _entries
                .GroupBy(e => e.QuestId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }
}
