using System;

namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>
    /// Task scoring: lower score = picked sooner. A pure, unit-tested port of TaskManager.CalculatePriority.
    /// The WRobot-bound caller measures the distance and counts the spatial neighbours; the formula lives here.
    /// </summary>
    public static class TaskPriority
    {
        // Distance-to-priority exponent. Kept identical to the original ("magic").
        public const double Magic = 1.32;

        // Hub harvesting (fixes "only one quest per town"): a quest giver clustered with at least HubMinPickups
        // OTHER pickable quest givers gets an extra cluster discount that scales with the hub size, so the bot
        // clears a town's available quests before leaving for a far field objective. HubBoostPerPickup is the
        // tuning knob (higher = stronger pull to finish a hub). Lone givers / non-pickup tasks are unaffected.
        public const int HubMinPickups = 2;
        public const double HubBoostPerPickup = 0.5;

        // Chain-aware "gateway" boost (Phase 6): a quest that gates more not-yet-done follow-ups gets a gentle
        // extra discount, so the bot prefers gateway quests (whose chain then unlocks / fills the hub). The
        // tuning knob; 0 = off.
        public const double ChainBoostPerQuest = 0.3;

        // Batch turn-in (Phase 7 slice 1): hold a completed quest's turn-in BACK while there is still actionable
        // objective/pickup work clustered around the player, so the bot drains the local area and batches ONE
        // return trip instead of running back to the quest giver after every single quest. The penalty is
        // BOUNDED (a turn-in is never deferred forever - cap below) and only kicks in once the nearby open-work
        // count reaches TurnInDeferMinWork. Lowering TurnInDeferPerWork weakens it; 0 disables it entirely.
        public const int TurnInDeferMinWork = 1;        // start deferring once at least this much open work is nearby
        public const double TurnInDeferPerWork = 0.6;   // penalty growth per nearby open task
        public const int TurnInDeferWorkCap = 4;        // cap the counted work so the penalty stays bounded (~5x)

        // Class-quest "Zwang" (Daniel): class quests unlock core mechanics (Shaman totems, etc.) and are pursued to
        // completion regardless of distance OR continent. They score in a dedicated tier BELOW every ordinary task
        // (which are always >= 0), so they always sort first; among themselves the nearest step wins. No cluster /
        // hub / deferral / continent adjustment — the TravelManager makes the cross-continent trip once selected.
        public const int ClassQuestBasePriority = -1_000_000;

        /// <summary>
        /// Scores a task. Faithful to the original integer-truncation order.
        /// </summary>
        /// <param name="taskDistance">Distance from the player to the task.</param>
        /// <param name="neighbourCount">Tasks within the cluster radius, INCLUDING this task itself (the original
        /// KD-tree radial search returns self too). More neighbours → bigger cluster bonus → lower (better) score.</param>
        /// <param name="spatialWeight">Per-task cluster weight. NOTE the deliberate asymmetry that drives the
        /// "only one quest per hub" behaviour: quest-giver pickups use 0.25 while kill tasks use 1.0, so a dense
        /// field pack out-scores a cluster of nearby quest givers. Phase 7 (hub harvesting) addresses this.</param>
        /// <param name="priorityShift">Right-shift applied to the score (each step roughly halves it = higher priority).</param>
        /// <param name="isDifferentContinent">When true the score is shifted left by 10 (a huge penalty), so
        /// same-continent work is always preferred.</param>
        /// <param name="hubPickupNeighbours">For a quest-giver pickup task: how many pickable quest givers are
        /// clustered with it (INCLUDING itself). 0 for non-pickup tasks. &gt;= <see cref="HubMinPickups"/> triggers
        /// the hub boost. Default 0 keeps the original behaviour for every existing caller.</param>
        /// <param name="turnInDeferralWork">For a quest TURN-IN task: how many actionable objective/pickup tasks
        /// are still clustered around the PLAYER (not the turn-in NPC). &gt;= <see cref="TurnInDeferMinWork"/> defers
        /// the turn-in (bounded penalty) so the bot finishes the local area first. 0 for every other task and for a
        /// turn-in with no nearby open work, keeping the original behaviour for every existing caller.</param>
        public static int Compute(
            double taskDistance,
            int neighbourCount,
            double spatialWeight,
            int priorityShift,
            bool isDifferentContinent,
            int hubPickupNeighbours = 0,
            int chainValue = 0,
            int turnInDeferralWork = 0,
            bool isClassQuest = false)
        {
            // Class quests are their own top tier — always below any ordinary task (>= 0), ordered by raw distance
            // (nearest step first). Distance is clamped so the score stays safely negative even for a far/odd
            // cross-continent straight-line, so class quests never fall back into the ordinary range.
            if (isClassQuest)
                return ClassQuestBasePriority + (int)Math.Min(taskDistance < 0 ? 0 : taskDistance, 900_000);

            int priority = (int)Math.Pow(taskDistance, Magic);

            double locationWeight = 1.0 + neighbourCount * spatialWeight;
            priority = (int)(priority / Math.Pow(locationWeight, Magic));

            // Hub harvesting: extra discount for a quest giver standing in a hub of pickable quest givers.
            if (hubPickupNeighbours >= HubMinPickups)
            {
                double hubWeight = 1.0 + hubPickupNeighbours * HubBoostPerPickup;
                priority = (int)(priority / Math.Pow(hubWeight, Magic));
            }

            // Gateway boost: a quest that gates more not-yet-done follow-ups is worth doing sooner.
            if (chainValue > 0)
            {
                double chainWeight = 1.0 + chainValue * ChainBoostPerQuest;
                priority = (int)(priority / Math.Pow(chainWeight, Magic));
            }

            // Batch turn-in: make a turn-in look farther (defer it) while local objective/pickup work remains, so
            // the bot clears the area and batches the return. Bounded by the work cap, so it never strands a quest.
            if (turnInDeferralWork >= TurnInDeferMinWork)
            {
                int countedWork = Math.Min(turnInDeferralWork, TurnInDeferWorkCap);
                double deferWeight = 1.0 + countedWork * TurnInDeferPerWork;
                priority = (int)(priority * Math.Pow(deferWeight, Magic));
            }

            priority >>= priorityShift;

            if (isDifferentContinent)
                priority <<= 10;

            return priority;
        }

        /// <summary>
        /// Turn-in deferral driver (the value fed to <see cref="Compute"/>'s <c>turnInDeferralWork</c>). A turn-in is
        /// held back if EITHER the player's immediate cluster (slice 1) OR the turn-in NPC's own region (slice 3)
        /// still has open quest work. Returns the LARGER of the two counts - deliberately a max, not a sum, so the
        /// single bounded penalty covers both signals and a turn-in is never double-penalised (which could strand it).
        /// </summary>
        /// <param name="playerClusterWork">Open quest tasks (objectives + pickups, not turn-ins) near the player.</param>
        /// <param name="regionClusterWork">Open quest tasks near the turn-in NPC's own location (the destination region).</param>
        public static int TurnInDeferralWork(int playerClusterWork, int regionClusterWork)
            => Math.Max(playerClusterWork, regionClusterWork);
    }
}
