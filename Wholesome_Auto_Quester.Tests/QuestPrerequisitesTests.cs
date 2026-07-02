using System;
using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class QuestPrerequisitesTests
    {
        private static readonly Func<int, bool> NoneCompleted = _ => false;

        [Fact]
        public void No_prerequisites_and_no_skill_is_pickable()
        {
            Assert.True(QuestPrerequisites.IsPickable(new int[0], NoneCompleted, false, 0, 0));
        }

        [Fact]
        public void Null_previous_list_means_no_prerequisite()
        {
            Assert.True(QuestPrerequisites.IsPickable(null, NoneCompleted, false, 0, 0));
        }

        [Fact]
        public void Prerequisite_blocks_when_no_previous_quest_is_completed()
        {
            Assert.False(QuestPrerequisites.IsPickable(new[] { 10, 20 }, NoneCompleted, false, 0, 0));
        }

        [Fact]
        public void Prerequisite_satisfied_when_any_previous_quest_is_completed()
        {
            // ANY-completed semantics (faithful to the original).
            Assert.True(QuestPrerequisites.IsPickable(new[] { 10, 20 }, id => id == 20, false, 0, 0));
        }

        [Fact]
        public void Skill_blocks_when_below_threshold()
        {
            Assert.False(QuestPrerequisites.IsPickable(new int[0], NoneCompleted, true, 100, 150));
        }

        [Fact]
        public void Skill_ok_when_at_or_above_threshold()
        {
            Assert.True(QuestPrerequisites.IsPickable(new int[0], NoneCompleted, true, 150, 150));
        }

        [Fact]
        public void Both_prerequisite_and_skill_must_pass()
        {
            // Prerequisite met, but the skill is too low -> not pickable.
            Assert.False(QuestPrerequisites.IsPickable(new[] { 10 }, id => id == 10, true, 10, 50));
        }

        // --- ReasonNotPickable: the human reason behind the bool (debug visibility) ---

        [Fact]
        public void ReasonNotPickable_is_null_when_pickable()
        {
            Assert.Null(QuestPrerequisites.ReasonNotPickable(new[] { 10 }, id => id == 10, false, 0, 0));
        }

        [Fact]
        public void ReasonNotPickable_names_the_missing_prerequisite_quests()
        {
            string reason = QuestPrerequisites.ReasonNotPickable(new[] { 10, 20 }, NoneCompleted, false, 0, 0);
            Assert.Contains("prerequisite", reason);
            Assert.Contains("10", reason);
            Assert.Contains("20", reason);
        }

        [Fact]
        public void ReasonNotPickable_reports_the_skill_gap()
        {
            string reason = QuestPrerequisites.ReasonNotPickable(new int[0], NoneCompleted, true, 100, 150);
            Assert.Contains("skill", reason);
            Assert.Contains("100", reason);
            Assert.Contains("150", reason);
        }
    }
}
