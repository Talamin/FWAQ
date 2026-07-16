using robotManager.Helpful;
using System.Linq;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.Logic;
using wManager;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    public abstract class WAQBaseTask : IWAQTask
    {
        private Timer _timeOutTimer = new Timer();
        private int _timeoutMultiplicator = 1;
        private string _timeOutReason = "";
        private WAQPath _longPathToTask;

        public Vector3 Location { get; }
        public string TaskName { get; }
        public ModelWorldMapArea WorldMapArea { get; }
        public double SpatialWeight { get; protected set; } = 1.0;
        public int PriorityShift { get; protected set; } = 1;
        public virtual bool IsQuestGiverPickup => false;
        public virtual bool IsTurnInQuest => false;
        public bool IsClassQuest { get; protected set; } // set by tasks whose quest is class-restricted (AllowableClasses>0)
        public virtual int QuestId => 0;
        public int SearchRadius { get; protected set; } = 15;
        private bool IsTimedOut => !_timeOutTimer.IsReady;
        public string InvalidityReason { get; private set; } = " ";
        public WAQPath LongPathToTask
        {
            get
            {
                // Only recalculate when we've drifted enough that the cached length is stale. This path feeds the
                // task-SELECTION comparisons in TaskManager (is this task a big detour / farther than the active
                // one?) — NOT the actual movement (WRobot's mover owns that), so a coarse length is fine. The
                // recompute is a LIVE cross-zone pathfind, so a tight 70yd threshold made it the #1 UpdateTaskPile
                // cost on long treks; 150yd roughly halves the recomputes without changing the coarse comparisons.
                if (_longPathToTask == null
                    || _longPathToTask.Path.First().DistanceTo(ObjectManager.Me.Position) > 150)
                {
                    _longPathToTask = ToolBox.GetWAQPath(ObjectManager.Me.Position, Location);
                }
                return _longPathToTask;
            }
        }
        public bool IsValid
        {
            get
            {
                // A quest-giver PICKUP for a quest we ALREADY hold or have already completed can never succeed. The
                // status ladder normally drops the pickup task when the quest leaves ToPickup, but a task left
                // registered on a shared giver (Gornek both STARTS and ENDS "Cutting Teeth" 788) could still be
                // re-selected by the scanner and loop "Failed to pick up" after the quest was long since turned in.
                // Reject it here so an already-taken/completed pickup can never be actioned again (Talamin).
                if (IsQuestGiverPickup && QuestId > 0
                    && (Quest.HasQuest(QuestId) || ToolBox.IsQuestCompleted(QuestId)))
                {
                    InvalidityReason = "Quest already taken or completed";
                    return false;
                }

                // Gather the cheap facts; the ladder + leveling-zone progression lives in (unit-tested)
                // TaskValidity.Evaluate. ReputationMismatch is captured once (it's also the reason string).
                bool hasWorldMapArea = WorldMapArea != null;
                string reputationMismatch = ReputationMismatch;

                TaskValidityInput facts = new TaskValidityInput
                {
                    PlayerLevel = ObjectManager.Me.Level,
                    IsInStartingZone = hasWorldMapArea && IsInMyStartingZone(),
                    IsTimedOut = IsTimedOut,
                    HasReputationMismatch = reputationMismatch != null,
                    HasEnoughSkill = HasEnoughSkillForTask,
                    IsRecordedAsUnreachable = IsRecordedAsUnreachable,
                    HasWorldMapArea = hasWorldMapArea,
                    IsOutlands = hasWorldMapArea && WorldMapArea.Continent == WAQContinent.Outlands,
                    IsNorthrend = hasWorldMapArea && WorldMapArea.Continent == WAQContinent.Northrend,
                };

                // The two expensive checks stay LAZY (a blacklist scan; the two O(n) Dark-Portal completed-quest
                // lookups) so they only run if reached - preserving the original short-circuit.
                TaskInvalidReason reason = TaskValidity.Evaluate(
                    facts,
                    () => wManagerSetting.IsBlackListedZone(Location),
                    () => ToolBox.IsQuestCompleted(9407) || ToolBox.IsQuestCompleted(10119));

                switch (reason)
                {
                    case TaskInvalidReason.StickingToStartingZone:
                        InvalidityReason = "Sticking to starting zone";
                        return false;
                    case TaskInvalidReason.TimedOut:
                        InvalidityReason = _timeOutReason;
                        return false;
                    case TaskInvalidReason.ReputationMismatch:
                        InvalidityReason = reputationMismatch;
                        return false;
                    case TaskInvalidReason.InsufficientSkill:
                        InvalidityReason = "Insufficient skill";
                        return false;
                    case TaskInvalidReason.Unreachable:
                        InvalidityReason = "Unreachable";
                        return false;
                    case TaskInvalidReason.NoWorldMapArea:
                        InvalidityReason = "Unable to record world map area";
                        return false;
                    case TaskInvalidReason.ZoneBlacklisted:
                        PutTaskOnTimeout("Zone is blacklisted", 60 * 30, true);
                        InvalidityReason = "Zone is blacklisted";
                        return false;
                    case TaskInvalidReason.StickingToAzeroth:
                        InvalidityReason = "Sticking to Azeroth";
                        return false;
                    case TaskInvalidReason.StickingToOutlands:
                        InvalidityReason = "Sticking to Outlands";
                        return false;
                    case TaskInvalidReason.StickingToNorthrend:
                        InvalidityReason = "Sticking to Northrend";
                        return false;
                    default:
                        InvalidityReason = "";
                        return true;
                }
            }
        }

        public WAQBaseTask(Vector3 location, int continent, string taskName, IContinentManager continentManager)
        {
            Location = location;
            TaskName = taskName;
            WorldMapArea = continentManager.GetWorldMapAreaFromPoint(location, continent);
        }

        protected abstract bool IsRecordedAsUnreachable { get; }
        protected abstract bool HasEnoughSkillForTask { get; }
        protected abstract string ReputationMismatch { get; }
        public abstract TaskInteraction InteractionType { get; }
        public abstract string TrackerColor { get; }
        public abstract bool IsObjectValidForTask(WoWObject wowObject);
        public abstract void RegisterEntryToScanner(IWowObjectScanner scanner);
        public abstract void UnregisterEntryToScanner(IWowObjectScanner scanner);
        public abstract void PostInteraction(WoWObject wowObject);
        public abstract void RecordAsUnreachable();

        private bool IsInMyStartingZone()
        {
            WoWRace myRace = ObjectManager.Me.WowRace;
            if (myRace == WoWRace.Human) return WorldMapArea.IsHumanStartingZone;
            if (myRace == WoWRace.Dwarf || myRace == WoWRace.Gnome) return WorldMapArea.IsDwarfStartingZone;
            if (myRace == WoWRace.NightElf) return WorldMapArea.IsElfStartingZone;
            if (myRace == WoWRace.Draenei) return WorldMapArea.IsDraneiStartingZone;
            if (myRace == WoWRace.Orc || myRace == WoWRace.Troll) return WorldMapArea.IsOrcStartingZone;
            if (myRace == WoWRace.Undead) return WorldMapArea.IsUndeadStartingZone;
            if (myRace == WoWRace.Tauren) return WorldMapArea.IsTaurenStartingZone;
            if (myRace == WoWRace.BloodElf) return WorldMapArea.IsBloodElfStartingZone;
            Logger.LogError($"Couldn't detect your race");
            return false;
        }

        // Cap the escalation, KIND-AWARE via the base timeout (the base value already encodes the kind, so we avoid
        // threading a TaskFailureKind through the interface + every task's shadowed PutTaskOnTimeout):
        //  - SHORT base (<= 150s, e.g. TargetNotFound 60s) = the "patrol" kind. Every spawn is its own task so the
        //    planner already patrols them; an UNBOUNDED doubling backoff made a spread-out/contested kill area (17
        //    Arcane Patrollers over ~390x360y, "Major Malfunction" benched 39x) go cold and abandon the quest. Cap
        //    TIGHT (x4 -> ~4min) so it keeps patrolling at a bounded cadence.
        //  - LONG base (> 150s, e.g. Unreachable 5min / SurroundedByHostiles 10min / Stuck 15min) = genuinely broken
        //    or dangerous. A tight x4 cap would pull the bot back to a broken high-priority (e.g. class) objective
        //    every ~20min forever; a looser x16 cap lets it back off (Unreachable -> up to 80min) while still never
        //    growing without bound (Timer-overflow hygiene). Genuinely dead tasks are surfaced by TaskTodoLog.
        private const int PatrolBaseSeconds = 150;    // base <= this = patrol kind (retry often)
        private const int PatrolMaxMultiplicator = 4;
        private const int BrokenMaxMultiplicator = 16;

        public void PutTaskOnTimeout(string reason, int timeInSeconds = 0, bool exponentiallyLonger = false)
        {
            if (!IsTimedOut)
            {
                if (timeInSeconds < 30)
                {
                    timeInSeconds = 60 * 5;
                }
                Logger.Log($"Putting task {TaskName} on time out for {timeInSeconds * _timeoutMultiplicator} seconds. Reason: {reason}");
                TaskTodoLog.Record(this, reason);   // persist suspicious benches as fix-me entries (benign reasons filtered inside)
                _timeOutReason = reason;
                _timeOutTimer = new Timer(timeInSeconds * 1000 * _timeoutMultiplicator);
                if (exponentiallyLonger)
                {
                    int cap = timeInSeconds <= PatrolBaseSeconds ? PatrolMaxMultiplicator : BrokenMaxMultiplicator;
                    _timeoutMultiplicator = System.Math.Min(_timeoutMultiplicator * 2, cap);
                }
            }
        }
    }
}
