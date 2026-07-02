using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Wholesome_Auto_Quester.Helpers;

namespace Wholesome_Auto_Quester.Database
{
    /// <summary>One curated, always-blacklisted quest (bugged / unreachable / too many mobs / wrong faction / ...).</summary>
    public class QuestBlacklistEntry
    {
        public int Id { get; set; }
        public string Faction { get; set; } = "Both"; // "Both" | "Horde" | "Alliance"
        public string Reason { get; set; }
    }

    /// <summary>
    /// The static quest blacklist, loaded from the embedded Database/QuestBlacklist.json (Phase 3: data out of
    /// code). Previously ~115 hardcoded AddQuestToBlackList calls in QuestManager.InitializeWAQSettings.
    /// </summary>
    public static class QuestBlacklistData
    {
        private const string ResourceName = "Wholesome_Auto_Quester.Database.QuestBlacklist.json";
        private static List<QuestBlacklistEntry> _entries;

        public static List<QuestBlacklistEntry> Entries
        {
            get
            {
                if (_entries != null)
                    return _entries;

                _entries = new List<QuestBlacklistEntry>();
                try
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                    {
                        if (stream == null)
                        {
                            Logger.LogError($"[QuestBlacklist] embedded resource {ResourceName} not found");
                            return _entries;
                        }
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            _entries = JsonConvert.DeserializeObject<List<QuestBlacklistEntry>>(reader.ReadToEnd())
                                ?? new List<QuestBlacklistEntry>();
                        }
                    }
                    Logger.Log($"Loaded {_entries.Count} blacklisted quests from data");
                }
                catch (Exception e)
                {
                    Logger.LogError($"[QuestBlacklist] failed to load: {e}");
                    _entries = new List<QuestBlacklistEntry>();
                }
                return _entries;
            }
        }
    }
}
