using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Wholesome_Auto_Quester.Helpers;

namespace Wholesome_Auto_Quester.Bot.ScriptedProfile
{
    /// <summary>
    /// Loads the embedded ordered "profiles" for scripted zones. Mirrors the <see cref="Database.QuestStepsData"/>
    /// loader pattern (data out of code, single source of truth). Currently only the Death Knight start profile.
    /// </summary>
    public static class ScriptedProfileData
    {
        private const string DeathKnightResource = "Wholesome_Auto_Quester.Database.DeathKnightStart.json";
        private static List<ScriptedProfileStep> _deathKnightStart;

        /// <summary>The ordered Death Knight start (Ebon Hold, map 609) profile. Empty list if it failed to load.</summary>
        public static List<ScriptedProfileStep> DeathKnightStart
        {
            get
            {
                if (_deathKnightStart == null)
                    _deathKnightStart = Load(DeathKnightResource);
                return _deathKnightStart;
            }
        }

        /// <summary>True if the quest is owned by a scripted profile (currently the DK start). Such quests are managed
        /// entirely by the profile executor, in strict order - the normal quester must never abandon or re-route them,
        /// even though many aren't in its DB list.</summary>
        public static bool IsProfileQuest(int questId) =>
            DeathKnightStart.Any(s => s.QuestId == questId);

        /// <summary>Distinct in-game names of every item a profile uses/produces - added to the Do-Not-Sell/Mail list so
        /// the inventory-manager plugin can't delete them as "deprecated" low-level quest items (Talamin).</summary>
        public static List<string> ProfileItemNames() =>
            DeathKnightStart
                .SelectMany(s => new[] { s.ItemName, s.ResultItemName }
                    .Concat(s.ProtectItems ?? Enumerable.Empty<string>()))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

        private static List<ScriptedProfileStep> Load(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Logger.LogError($"[ScriptedProfile] embedded resource {resourceName} not found");
                        return new List<ScriptedProfileStep>();
                    }
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        List<ScriptedProfileStep> steps =
                            JsonConvert.DeserializeObject<List<ScriptedProfileStep>>(reader.ReadToEnd())
                            ?? new List<ScriptedProfileStep>();
                        Logger.Log($"Loaded {steps.Count} scripted-profile steps from {resourceName}");
                        return steps;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[ScriptedProfile] failed to load {resourceName}: {e}");
                return new List<ScriptedProfileStep>();
            }
        }
    }
}
