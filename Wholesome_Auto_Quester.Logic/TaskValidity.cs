using System;

namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>Why a task is (in)valid. The first failing check in <see cref="TaskValidity.Evaluate"/> wins,
    /// mirroring the original WAQBaseTask.IsValid order. The adapter maps this back to the user-facing reason
    /// string and any side effect (e.g. the blacklisted-zone timeout).</summary>
    public enum TaskInvalidReason
    {
        Valid,
        StickingToStartingZone,
        TimedOut,
        ReputationMismatch,
        InsufficientSkill,
        Unreachable,
        NoWorldMapArea,
        ZoneBlacklisted,
        StickingToAzeroth,
        StickingToOutlands,
        StickingToNorthrend
    }

    /// <summary>The CHEAP facts about a task's validity, gathered eagerly by the adapter. The two genuinely
    /// expensive checks (blacklist scan, and the O(n) Dark-Portal completed-quest lookups) are passed to
    /// <see cref="TaskValidity.Evaluate"/> as lazy delegates so they only run if reached - preserving the
    /// original short-circuit.</summary>
    public sealed class TaskValidityInput
    {
        public uint PlayerLevel { get; set; }
        public bool IsInStartingZone { get; set; }
        public bool IsTimedOut { get; set; }
        public bool HasReputationMismatch { get; set; }
        public bool HasEnoughSkill { get; set; }
        public bool IsRecordedAsUnreachable { get; set; }
        public bool HasWorldMapArea { get; set; }
        public bool IsOutlands { get; set; }
        public bool IsNorthrend { get; set; }
    }

    /// <summary>
    /// The task validity ladder + the level→continent leveling progression (stick to Azeroth &lt; 60, to Outlands
    /// 60-70 once the Dark Portal is open, to Northrend 70-80). Pure port of WAQBaseTask.IsValid; the order of the
    /// checks is the behaviour.
    /// </summary>
    public static class TaskValidity
    {
        public static TaskInvalidReason Evaluate(
            TaskValidityInput facts,
            Func<bool> isZoneBlacklisted,
            Func<bool> isDarkPortalOpened)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            if (isZoneBlacklisted == null) throw new ArgumentNullException(nameof(isZoneBlacklisted));
            if (isDarkPortalOpened == null) throw new ArgumentNullException(nameof(isDarkPortalOpened));

            // Below 12 we stay in the racial starting zone.
            if (facts.PlayerLevel < 12 && !facts.IsInStartingZone)
                return TaskInvalidReason.StickingToStartingZone;

            if (facts.IsTimedOut)
                return TaskInvalidReason.TimedOut;

            if (facts.HasReputationMismatch)
                return TaskInvalidReason.ReputationMismatch;

            if (!facts.HasEnoughSkill)
                return TaskInvalidReason.InsufficientSkill;

            if (facts.IsRecordedAsUnreachable)
                return TaskInvalidReason.Unreachable;

            if (!facts.HasWorldMapArea)
                return TaskInvalidReason.NoWorldMapArea;

            // Lazy: a blacklist scan - only run once the cheap checks pass.
            if (isZoneBlacklisted())
                return TaskInvalidReason.ZoneBlacklisted;

            // Stay out of Outlands until 60.
            if (facts.PlayerLevel < 60 && facts.IsOutlands)
                return TaskInvalidReason.StickingToAzeroth;

            // 60-70 (once the Dark Portal is open) stay in Outlands. Lazy: the Dark-Portal check is two O(n)
            // completed-quest lookups, and the `< 70` guard short-circuits it for level-70+ characters.
            if (facts.PlayerLevel < 70 && !facts.IsOutlands && isDarkPortalOpened())
                return TaskInvalidReason.StickingToOutlands;

            // 70-80 stay in Northrend.
            if (facts.PlayerLevel >= 70 && facts.PlayerLevel <= 80 && !facts.IsNorthrend)
                return TaskInvalidReason.StickingToNorthrend;

            return TaskInvalidReason.Valid;
        }
    }
}
