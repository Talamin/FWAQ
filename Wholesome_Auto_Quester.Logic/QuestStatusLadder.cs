using System;

namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>Where a quest currently sits in the player's quest log (mapped from WRobot's StateFlag by the
    /// adapter, so this stays WRobot-free).</summary>
    public enum QuestLogState
    {
        NotInLog,
        InProgress,
        Complete,
        Failed
    }

    /// <summary>The facts the status ladder needs about ONE quest, gathered by the WRobot-bound caller. Plain
    /// data, no behaviour - this is what makes the decision unit-testable offline.</summary>
    public sealed class QuestStatusInput
    {
        /// <summary>All DB conditions for the quest are satisfied (or there are none).</summary>
        public bool DbConditionsMet { get; set; }

        /// <summary>The quest is on the user/blacklist.</summary>
        public bool IsBlacklisted { get; set; }

        /// <summary>The quest belongs to an exclusive group and another quest in that group has already been
        /// completed / taken, so this one can never be done. The caller bakes in the "not currently in the log"
        /// condition.</summary>
        public bool ExclusiveGroupSatisfied { get; set; }

        /// <summary>The quest id is in the player's completed set.</summary>
        public bool IsCompleted { get; set; }

        /// <summary>Prerequisites + required skill are met, so the quest could be picked up.</summary>
        public bool IsPickable { get; set; }

        /// <summary>Where the quest sits in the log right now.</summary>
        public QuestLogState LogState { get; set; }

        /// <summary>When the quest is NOT pickable, a human reason why (prerequisite/skill); null when pickable.
        /// Carried so <see cref="QuestStatusLadder.DecideWithReason"/> can surface it for the otherwise-silent
        /// "None" case — pure debugging visibility, it does not affect the decision.</summary>
        public string NotPickableReason { get; set; }
    }

    /// <summary>
    /// The quest status ladder: decides a single quest's <see cref="QuestStatus"/> from a snapshot of facts.
    /// Extracted verbatim from QuestManager.UpdateStatuses so it can be unit-tested without WRobot. The order of
    /// the checks IS the behaviour (earlier checks win), so it mirrors the original precedence exactly.
    /// </summary>
    public static class QuestStatusLadder
    {
        /// <summary>A status decision plus a human-readable reason — for debug visibility into WHY a quest landed
        /// where it did (especially the otherwise-silent "None").</summary>
        public struct QuestDecision
        {
            public readonly QuestStatus Status;
            public readonly string Reason;
            public QuestDecision(QuestStatus status, string reason) { Status = status; Reason = reason; }
        }

        /// <summary>The status alone (back-compat for callers/tests that don't need the reason).</summary>
        public static QuestStatus Decide(QuestStatusInput input) => DecideWithReason(input).Status;

        public static QuestDecision DecideWithReason(QuestStatusInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            // Precedence matches the original UpdateStatuses loop top-to-bottom.
            if (!input.DbConditionsMet)
                return new QuestDecision(QuestStatus.DBConditionsNotMet, "DB conditions not met");

            if (input.IsBlacklisted)
                return new QuestDecision(QuestStatus.Blacklisted, "blacklisted");

            if (input.ExclusiveGroupSatisfied)
                return new QuestDecision(QuestStatus.Completed, "exclusive group already taken/done");

            if (input.IsCompleted)
                return new QuestDecision(QuestStatus.Completed, "already completed");

            if (input.IsPickable && input.LogState == QuestLogState.NotInLog)
                return new QuestDecision(QuestStatus.ToPickup, "pickable");

            switch (input.LogState)
            {
                case QuestLogState.Complete:
                    return new QuestDecision(QuestStatus.ToTurnIn, "complete in log");
                case QuestLogState.Failed:
                    return new QuestDecision(QuestStatus.Failed, "failed in log");
                case QuestLogState.InProgress:
                    return new QuestDecision(QuestStatus.InProgress, "in progress");
                default:
                    // NotInLog AND not pickable: the otherwise-silent "None". Surface why it can't be picked up.
                    return new QuestDecision(QuestStatus.None,
                        input.NotPickableReason != null
                            ? "not pickable - " + input.NotPickableReason
                            : "not pickable");
            }
        }
    }
}
