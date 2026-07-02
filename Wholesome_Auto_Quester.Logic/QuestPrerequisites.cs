using System;
using System.Collections.Generic;

namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>
    /// Whether a quest can be picked up right now: its prerequisite chain plus any required skill threshold.
    /// A pure port of QuestManager.IsQuestPickable. This is the seam where the Phase 6 quest-chain DAG will grow:
    /// today the prerequisite rule is "ANY previous quest completed" (the original even comments doubt about
    /// ANY vs ALL), and the AND/OR/exclusive-group semantics will be refined here, with tests.
    /// </summary>
    public static class QuestPrerequisites
    {
        /// <param name="previousQuestIds">The quest's prerequisite quest ids (may be null/empty = no prerequisite).</param>
        /// <param name="isQuestCompleted">Predicate: is quest id X already completed? (Adapter passes the real lookup.)</param>
        /// <param name="requiresSkill">The quest gates on a profession/skill (RequiredSkillID &gt; 0).</param>
        /// <param name="playerSkillValue">The player's current value in that skill (0 when no skill is required).</param>
        /// <param name="requiredSkillPoints">The skill value the quest needs.</param>
        public static bool IsPickable(
            IReadOnlyList<int> previousQuestIds,
            Func<int, bool> isQuestCompleted,
            bool requiresSkill,
            int playerSkillValue,
            int requiredSkillPoints)
            => ReasonNotPickable(previousQuestIds, isQuestCompleted, requiresSkill, playerSkillValue, requiredSkillPoints) == null;

        /// <summary>Same checks as <see cref="IsPickable"/>, but returns a human-readable reason WHY the quest is
        /// not pickable yet (or null when it IS pickable). This is what makes "why didn't the bot take this quest?"
        /// visible in the debug log instead of a silent "None".</summary>
        public static string ReasonNotPickable(
            IReadOnlyList<int> previousQuestIds,
            Func<int, bool> isQuestCompleted,
            bool requiresSkill,
            int playerSkillValue,
            int requiredSkillPoints)
        {
            if (isQuestCompleted == null)
                throw new ArgumentNullException(nameof(isQuestCompleted));

            // Prerequisite chain: if the quest has previous quests and NONE of them is completed, it's not
            // available yet. ANY-completed semantics, faithful to the original (Phase 6 refines this).
            if (previousQuestIds != null && previousQuestIds.Count > 0
                && !AnyCompleted(previousQuestIds, isQuestCompleted))
            {
                return $"prerequisite quest not done (needs one of: {string.Join(", ", previousQuestIds)})";
            }

            // Required skill (e.g. a profession threshold) not yet reached.
            if (requiresSkill && playerSkillValue < requiredSkillPoints)
            {
                return $"required skill {playerSkillValue}/{requiredSkillPoints} too low";
            }

            return null;
        }

        private static bool AnyCompleted(IReadOnlyList<int> ids, Func<int, bool> isQuestCompleted)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (isQuestCompleted(ids[i]))
                    return true;
            }
            return false;
        }
    }
}
