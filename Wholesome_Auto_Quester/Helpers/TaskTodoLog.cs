using Newtonsoft.Json;
using robotManager.Helpful;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Helpers
{
    /// <summary>
    /// Persistent "fix me later" list for content/navigation bugs the bot runs into unattended. Every SUSPICIOUS
    /// task bench (unreachable POI, gossip failure, missing item, ...) is recorded into
    /// &lt;WRobot&gt;\Settings\WAQ\TaskTodos.json — deduplicated per quest/task/reason with an occurrence counter and
    /// first/last-seen timestamps — so cases like "Shrine of Dath'Remar benched as [Scanner] Unreachable 200y out"
    /// can be reviewed and fixed in a batch (off-mesh link, hotspot tweak, quest blacklist) instead of scrolling
    /// session logs. Benign planner reasons (Completed, back-and-forth damping, the derived zone-blacklist bench)
    /// are not recorded. Never throws — a diagnostics file must not break the bot.
    /// </summary>
    internal static class TaskTodoLog
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, TodoEntry> _entries;   // key -> entry, lazy-loaded

        private static string FilePath => Path.Combine(Others.GetCurrentDirectory, "Settings", "WAQ", "TaskTodos.json");

        // Reasons that are normal planner behavior, not content bugs. "Zone is blacklisted" is the DERIVED bench
        // that follows a recorded "[Scanner] Unreachable" — recording it too would double every unreachable case.
        private static readonly string[] _benignReasons =
        {
            "Completed",
            "Avoid back and forth",
            "Zone is blacklisted",
        };

        public static void Record(IWAQTask task, string reason)
        {
            try
            {
                if (task == null || string.IsNullOrEmpty(reason)) return;
                if (_benignReasons.Any(b => reason.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)) return;

                lock (_lock)
                {
                    EnsureLoaded();

                    string key = $"{task.QuestId}|{task.TaskName}|{reason}";
                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    if (_entries.TryGetValue(key, out TodoEntry entry))
                    {
                        entry.Count++;
                        entry.LastSeen = now;
                    }
                    else
                    {
                        Vector3 me = ObjectManager.Me?.Position ?? new Vector3(0, 0, 0);
                        entry = new TodoEntry
                        {
                            Reason = reason,
                            QuestId = task.QuestId,
                            TaskName = task.TaskName,
                            TaskLocation = $"{task.Location?.X:F1} {task.Location?.Y:F1} {task.Location?.Z:F1}",
                            MapId = task.WorldMapArea?.mapID ?? -1,
                            ContinentId = Usefuls.ContinentId,
                            PlayerPosition = $"{me.X:F1} {me.Y:F1} {me.Z:F1}",
                            PlayerLevel = (int)(ObjectManager.Me?.Level ?? 0),
                            Count = 1,
                            FirstSeen = now,
                            LastSeen = now,
                        };
                        _entries[key] = entry;
                        Logger.Log($"[TaskTodo] NEW fix-me entry: {task.TaskName} — {reason} (Settings\\WAQ\\TaskTodos.json)");
                    }

                    Save();
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug($"[TaskTodo] Record failed (ignored): {e.Message}");
            }
        }

        private static void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, TodoEntry>();
            try
            {
                if (File.Exists(FilePath))
                {
                    List<TodoEntry> list = JsonConvert.DeserializeObject<List<TodoEntry>>(File.ReadAllText(FilePath))
                        ?? new List<TodoEntry>();
                    foreach (TodoEntry e in list)
                        _entries[$"{e.QuestId}|{e.TaskName}|{e.Reason}"] = e;
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug($"[TaskTodo] load failed, starting fresh (ignored): {e.Message}");
            }
        }

        private static void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            List<TodoEntry> list = _entries.Values
                .OrderByDescending(e => e.Count)
                .ThenBy(e => e.QuestId)
                .ToList();
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
        }

        private class TodoEntry
        {
            public string Reason;
            public int QuestId;
            public string TaskName;
            public string TaskLocation;
            public int MapId;
            public int ContinentId;
            public string PlayerPosition;
            public int PlayerLevel;
            public int Count;
            public string FirstSeen;
            public string LastSeen;
        }
    }
}
