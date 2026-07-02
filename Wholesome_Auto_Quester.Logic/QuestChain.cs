using System.Collections.Generic;

namespace Wholesome_Auto_Quester.Logic
{
    /// <summary>
    /// Quest-chain graph logic (Phase 6). Pure + unit-testable. Designed around two facts Daniel flagged:
    /// WoW quests unlock sequentially via prerequisites, and the AQ database deliberately OMITS custom-behaviour
    /// quests — so the NextQuests graph has intentional GAPS and must never block or loop. Hence: gap-safe
    /// (unknown ids are skipped, not traversed) and cycle-safe (a visited set).
    /// </summary>
    public static class QuestChain
    {
        private static readonly IReadOnlyList<int> Empty = new int[0];

        /// <summary>
        /// How many NOT-yet-completed, in-our-DB quests are gated downstream of <paramref name="questId"/> via the
        /// NextQuests graph — i.e. how much follow-up content doing this quest helps open up. A higher value means
        /// a better "gateway" quest (worth doing sooner so its chain unlocks). Traversal stops at completed quests
        /// (their successors are already reachable) and at unknown ids (DB gaps).
        /// </summary>
        public static int DownstreamUnlockCount(
            int questId,
            System.Func<int, IReadOnlyList<int>> nextQuestIds,
            System.Func<int, bool> isCompleted,
            System.Func<int, bool> isKnown)
        {
            if (nextQuestIds == null || isCompleted == null || isKnown == null)
                return 0;

            var visited = new HashSet<int>();
            visited.Add(questId); // a quest never gates itself, even if a cycle leads back to it
            var stack = new Stack<int>();

            foreach (int next in nextQuestIds(questId) ?? Empty)
                stack.Push(next);

            int count = 0;
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                if (!visited.Add(q))
                    continue;            // cycle-safe
                if (!isKnown(q))
                    continue;            // gap-safe: a dropped custom-behaviour quest — ignore it
                if (isCompleted(q))
                    continue;            // already done: doesn't gate further, and isn't content to unlock

                count++;                 // an incomplete, known, reachable quest this gateway helps open
                foreach (int next in nextQuestIds(q) ?? Empty)
                    stack.Push(next);
            }

            return count;
        }
    }
}
