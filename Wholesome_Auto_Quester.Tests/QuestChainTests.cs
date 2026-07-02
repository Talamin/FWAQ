using System.Collections.Generic;
using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class QuestChainTests
    {
        // Build a tiny quest graph from a dictionary id -> next ids; everything is "known" unless told otherwise.
        private static int Count(
            int from,
            Dictionary<int, int[]> graph,
            HashSet<int> completed = null,
            HashSet<int> known = null)
        {
            completed = completed ?? new HashSet<int>();
            return QuestChain.DownstreamUnlockCount(
                from,
                id => graph.TryGetValue(id, out int[] n) ? n : null,
                id => completed.Contains(id),
                id => known == null ? true : known.Contains(id));
        }

        [Fact]
        public void No_next_quests_gates_nothing()
        {
            var g = new Dictionary<int, int[]> { { 1, new int[0] } };
            Assert.Equal(0, Count(1, g));
        }

        [Fact]
        public void One_incomplete_follow_up_counts_one()
        {
            var g = new Dictionary<int, int[]> { { 1, new[] { 2 } }, { 2, new int[0] } };
            Assert.Equal(1, Count(1, g));
        }

        [Fact]
        public void A_full_incomplete_chain_counts_every_downstream_quest()
        {
            var g = new Dictionary<int, int[]>
            {
                { 1, new[] { 2 } }, { 2, new[] { 3 } }, { 3, new[] { 4 } }, { 4, new int[0] }
            };
            Assert.Equal(3, Count(1, g)); // 2,3,4
        }

        [Fact]
        public void Traversal_stops_at_a_completed_quest()
        {
            // 1 -> 2(completed) -> 3 : since 2 is done, 3 is already reachable, so 1 gates nothing new.
            var g = new Dictionary<int, int[]> { { 1, new[] { 2 } }, { 2, new[] { 3 } }, { 3, new int[0] } };
            Assert.Equal(0, Count(1, g, completed: new HashSet<int> { 2 }));
        }

        [Fact]
        public void Unknown_follow_up_is_skipped_db_gap()
        {
            // 1 -> 2 (NOT in our DB, a dropped custom-behaviour quest). It must be ignored, not block/loop.
            var g = new Dictionary<int, int[]> { { 1, new[] { 2 } } };
            Assert.Equal(0, Count(1, g, known: new HashSet<int> { 1 }));
        }

        [Fact]
        public void A_cycle_does_not_loop_or_double_count()
        {
            var g = new Dictionary<int, int[]> { { 1, new[] { 2 } }, { 2, new[] { 1 } } };
            Assert.Equal(1, Count(1, g)); // only 2 (1 is the source, not counted)
        }

        [Fact]
        public void A_branching_chain_counts_the_whole_reachable_set_once()
        {
            // 1 -> {2,3}; 2 -> 4; 3 -> 4 (diamond). 4 counted once.
            var g = new Dictionary<int, int[]>
            {
                { 1, new[] { 2, 3 } }, { 2, new[] { 4 } }, { 3, new[] { 4 } }, { 4, new int[0] }
            };
            Assert.Equal(3, Count(1, g)); // 2,3,4
        }
    }
}
