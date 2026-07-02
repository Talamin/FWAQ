using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class TaskPriorityTests
    {
        // Relational tests (not magic numbers): the SCORING RELATIONSHIPS are the behaviour we care about.
        // Lower score = picked sooner.

        [Fact]
        public void Closer_task_scores_lower()
        {
            int near = TaskPriority.Compute(10, 1, 1.0, 0, false);
            int far = TaskPriority.Compute(100, 1, 1.0, 0, false);
            Assert.True(near < far);
        }

        [Fact]
        public void More_neighbours_lowers_the_score()
        {
            int alone = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int clustered = TaskPriority.Compute(100, 8, 1.0, 0, false);
            Assert.True(clustered < alone);
        }

        // The crux of the "only one quest per hub" behaviour: with the SAME distance and the SAME number of
        // neighbours, a kill task (spatialWeight 1.0) gets a far bigger cluster discount than a quest giver
        // (spatialWeight 0.25), so the kill pack wins. This test documents the asymmetry that Phase 7 fixes.
        [Fact]
        public void Kill_weight_beats_quest_giver_weight_for_the_same_cluster()
        {
            int killPack = TaskPriority.Compute(100, 6, 1.0, 0, false);
            int questGivers = TaskPriority.Compute(100, 6, 0.25, 0, false);
            Assert.True(killPack < questGivers);
        }

        [Fact]
        public void Higher_priority_shift_lowers_the_score()
        {
            int shift0 = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int shift2 = TaskPriority.Compute(100, 1, 1.0, 2, false);
            Assert.True(shift2 < shift0);
        }

        [Fact]
        public void Priority_shift_halves_per_step()
        {
            int shift0 = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int shift1 = TaskPriority.Compute(100, 1, 1.0, 1, false);
            Assert.Equal(shift0 >> 1, shift1);
        }

        [Fact]
        public void Different_continent_is_heavily_penalised()
        {
            int sameContinent = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int otherContinent = TaskPriority.Compute(100, 1, 1.0, 0, true);
            Assert.Equal(sameContinent << 10, otherContinent);
            Assert.True(otherContinent > sameContinent);
        }

        [Fact]
        public void A_near_other_continent_task_still_loses_to_a_far_same_continent_one()
        {
            int farSameContinent = TaskPriority.Compute(500, 1, 1.0, 0, false);
            int nearOtherContinent = TaskPriority.Compute(50, 1, 1.0, 0, true);
            Assert.True(farSameContinent < nearOtherContinent);
        }

        // --- Hub harvesting ---

        [Fact]
        public void A_lone_quest_giver_gets_no_hub_boost()
        {
            int noArg = TaskPriority.Compute(30, 4, 0.25, 1, false);
            int lone = TaskPriority.Compute(30, 4, 0.25, 1, false, hubPickupNeighbours: 1); // below HubMinPickups
            Assert.Equal(noArg, lone);
        }

        [Fact]
        public void A_quest_giver_in_a_hub_scores_lower_than_the_same_lone_one()
        {
            int lone = TaskPriority.Compute(30, 4, 0.25, 1, false, hubPickupNeighbours: 1);
            int inHub = TaskPriority.Compute(30, 4, 0.25, 1, false, hubPickupNeighbours: 4);
            Assert.True(inHub < lone);
        }

        [Fact]
        public void A_bigger_hub_pulls_harder()
        {
            int smallHub = TaskPriority.Compute(30, 4, 0.25, 1, false, hubPickupNeighbours: 2);
            int bigHub = TaskPriority.Compute(30, 8, 0.25, 1, false, hubPickupNeighbours: 8);
            Assert.True(bigHub < smallHub);
        }

        [Fact]
        public void A_town_of_quest_givers_now_beats_a_far_field_pack()
        {
            // The "only one quest per town" scenario.
            int townWithoutBoost = TaskPriority.Compute(30, 5, 0.25, 1, false);
            int farKillPack = TaskPriority.Compute(120, 10, 1.0, 1, false);
            int townHub = TaskPriority.Compute(30, 5, 0.25, 1, false, hubPickupNeighbours: 5);

            Assert.True(farKillPack < townWithoutBoost); // documents the original bug: the far pack won
            Assert.True(townHub < farKillPack);          // the fix: the hubbed town now wins
        }

        // --- Chain-aware "gateway" boost (Phase 6) ---

        [Fact]
        public void No_chain_value_means_no_chain_boost()
        {
            int noArg = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int zeroChain = TaskPriority.Compute(100, 1, 1.0, 0, false, chainValue: 0);
            Assert.Equal(noArg, zeroChain);
        }

        [Fact]
        public void A_gateway_quest_scores_lower_than_a_dead_end()
        {
            int deadEnd = TaskPriority.Compute(100, 1, 1.0, 0, false, chainValue: 0);
            int gateway = TaskPriority.Compute(100, 1, 1.0, 0, false, chainValue: 5);
            Assert.True(gateway < deadEnd);
        }

        [Fact]
        public void A_longer_chain_pulls_harder()
        {
            int shortChain = TaskPriority.Compute(100, 1, 1.0, 0, false, chainValue: 1);
            int longChain = TaskPriority.Compute(100, 1, 1.0, 0, false, chainValue: 6);
            Assert.True(longChain < shortChain);
        }

        // --- Batch turn-in (Phase 7 slice 1): defer a turn-in while local objective/pickup work remains ---

        [Fact]
        public void No_nearby_work_means_no_turn_in_deferral()
        {
            int noArg = TaskPriority.Compute(100, 1, 1.0, 0, false);
            int noWork = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 0);
            Assert.Equal(noArg, noWork);
        }

        [Fact]
        public void A_turn_in_is_deferred_while_local_work_remains()
        {
            // Same turn-in, scored higher (picked later) when there is still objective/pickup work nearby.
            int alone = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 0);
            int withWork = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 3);
            Assert.True(withWork > alone);
        }

        [Fact]
        public void More_local_work_defers_the_turn_in_harder()
        {
            int littleWork = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 1);
            int lotsOfWork = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 4);
            Assert.True(lotsOfWork > littleWork);
        }

        [Fact]
        public void The_turn_in_deferral_is_bounded_by_the_work_cap()
        {
            // Past the cap, piling on more nearby work does NOT defer any further - the penalty is bounded.
            int atCap = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: TaskPriority.TurnInDeferWorkCap);
            int wayOverCap = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: TaskPriority.TurnInDeferWorkCap + 50);
            Assert.Equal(atCap, wayOverCap);
        }

        [Fact]
        public void A_deferred_near_turn_in_still_beats_a_far_task_so_it_never_strands()
        {
            // Bounded deferral: even fully deferred, a close turn-in still out-scores much farther work, so the
            // bot eventually returns and turns in instead of being stranded forever.
            int deferredNearTurnIn = TaskPriority.Compute(30, 1, 1.0, 0, false, turnInDeferralWork: TaskPriority.TurnInDeferWorkCap);
            int farTask = TaskPriority.Compute(400, 1, 1.0, 0, false);
            Assert.True(deferredNearTurnIn < farTask);
        }

        // --- Region coalescing (Phase 7 slice 3): player-cluster OR NPC-region work both defer, via one max ---

        [Fact]
        public void Deferral_work_is_the_larger_of_player_and_region_clusters()
        {
            Assert.Equal(5, TaskPriority.TurnInDeferralWork(2, 5));
            Assert.Equal(5, TaskPriority.TurnInDeferralWork(5, 2));
        }

        [Fact]
        public void Region_work_alone_defers_a_turn_in_just_like_player_work()
        {
            // Slice 3: the player stands clear (0 nearby) but the turn-in NPC's region still has open work -> defer.
            int regionOnly = TaskPriority.TurnInDeferralWork(0, 3);
            int alone = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: 0);
            int coalesced = TaskPriority.Compute(100, 1, 1.0, 0, false, turnInDeferralWork: regionOnly);
            Assert.True(coalesced > alone);
        }

        [Fact]
        public void No_work_in_either_cluster_means_no_deferral()
        {
            Assert.Equal(0, TaskPriority.TurnInDeferralWork(0, 0));
        }

        // --- Class-quest "Zwang": class quests are pursued to completion regardless of distance / continent ---

        [Fact]
        public void A_class_quest_outranks_the_best_ordinary_task_even_far_and_cross_continent()
        {
            // Best-case ordinary task: nearby, densely clustered, already carrying the class-quest priority shift.
            int bestOrdinary = TaskPriority.Compute(5, 12, 1.0, 2, false);
            int farClassQuest = TaskPriority.Compute(4000, 1, 1.0, 0, isDifferentContinent: false, isClassQuest: true);
            int crossContinentClassQuest = TaskPriority.Compute(4000, 1, 1.0, 0, isDifferentContinent: true, isClassQuest: true);
            Assert.True(farClassQuest < bestOrdinary);
            Assert.True(crossContinentClassQuest < bestOrdinary);
        }

        [Fact]
        public void Class_quests_are_ordered_by_distance_nearest_first()
        {
            int near = TaskPriority.Compute(20, 1, 1.0, 0, false, isClassQuest: true);
            int far = TaskPriority.Compute(500, 1, 1.0, 0, false, isClassQuest: true);
            Assert.True(near < far);
        }

        [Fact]
        public void An_ordinary_task_never_scores_below_a_class_quest_even_at_extreme_distance()
        {
            // Ordinary scores are always >= 0; a class quest stays negative even at the distance clamp, so an
            // ordinary task can never out-score it.
            int ordinary = TaskPriority.Compute(5, 20, 1.0, 20, false);
            int worstCaseClassQuest = TaskPriority.Compute(900_000, 1, 1.0, 0, false, isClassQuest: true);
            Assert.True(ordinary >= 0);
            Assert.True(worstCaseClassQuest < ordinary);
        }
    }
}
