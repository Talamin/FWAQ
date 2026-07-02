namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>Why a task is being put on timeout - drives how long (and whether it escalates).</summary>
    public enum TaskFailureKind
    {
        /// <summary>Reached the hotspot but the target isn't there (despawned / killed / on respawn).</summary>
        TargetNotFound,

        /// <summary>No path to the object.</summary>
        Unreachable,

        /// <summary>The objective is surrounded by too many hostiles to safely engage.</summary>
        SurroundedByHostiles,

        /// <summary>Movement kept getting stuck trying to reach it.</summary>
        Stuck
    }

    /// <summary>
    /// Graduated recovery: how long to time a failed task out. The idea is "retry soon after a likely-transient
    /// failure, back off only when it keeps failing" - so the bot doesn't permanently park a quest (the old
    /// 3-HOUR "unreachable" timeout) and doesn't churn back every 5 minutes (the old flat "target not found").
    /// The FIRST timeout is short; callers pass <c>exponentiallyLonger: true</c> so repeats double via the task's
    /// existing multiplier. Pure + tunable in one place.
    /// </summary>
    public static class RecoveryPolicy
    {
        public static int FirstTimeoutSeconds(TaskFailureKind kind)
        {
            switch (kind)
            {
                case TaskFailureKind.TargetNotFound:
                    return 60;            // respawn window - retry soon (was a flat 5 min with no escalation)
                case TaskFailureKind.Unreachable:
                    return 5 * 60;        // was 3 HOURS - a transient obstacle no longer parks the quest
                case TaskFailureKind.SurroundedByHostiles:
                    return 10 * 60;       // was 30 min
                case TaskFailureKind.Stuck:
                    return 5 * 60;
                default:
                    return 5 * 60;
            }
        }

        /// <summary>Repeats of every failure kind should escalate, so a genuinely-broken task backs off instead of
        /// being retried forever at the short interval.</summary>
        public static bool Escalates(TaskFailureKind kind) => true;
    }
}
