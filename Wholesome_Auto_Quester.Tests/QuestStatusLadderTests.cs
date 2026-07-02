using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class QuestStatusLadderTests
    {
        // A quest that, with no other facts set, decides to None - so each test flips exactly the facts it cares about.
        private static QuestStatusInput Base() => new QuestStatusInput
        {
            DbConditionsMet = true,
            IsBlacklisted = false,
            ExclusiveGroupSatisfied = false,
            IsCompleted = false,
            IsPickable = false,
            LogState = QuestLogState.NotInLog
        };

        [Fact]
        public void DbConditionsNotMet_wins_over_everything_else()
        {
            var i = Base();
            i.DbConditionsMet = false;
            i.IsBlacklisted = true;
            i.IsCompleted = true;
            i.IsPickable = true;
            i.LogState = QuestLogState.Complete;
            Assert.Equal(QuestStatus.DBConditionsNotMet, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Blacklisted_wins_over_completed_and_pickable()
        {
            var i = Base();
            i.IsBlacklisted = true;
            i.IsCompleted = true;
            i.IsPickable = true;
            Assert.Equal(QuestStatus.Blacklisted, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void ExclusiveGroupSatisfied_marks_completed()
        {
            var i = Base();
            i.ExclusiveGroupSatisfied = true;
            i.IsPickable = true; // would otherwise be ToPickup
            Assert.Equal(QuestStatus.Completed, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Completed_set_marks_completed()
        {
            var i = Base();
            i.IsCompleted = true;
            i.IsPickable = true; // completed wins over pickable
            Assert.Equal(QuestStatus.Completed, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Pickable_and_not_in_log_is_ToPickup()
        {
            var i = Base();
            i.IsPickable = true;
            Assert.Equal(QuestStatus.ToPickup, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Pickable_but_already_in_log_follows_the_log_state()
        {
            var i = Base();
            i.IsPickable = true;
            i.LogState = QuestLogState.InProgress;
            // In the log already -> the log state wins, not ToPickup.
            Assert.Equal(QuestStatus.InProgress, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Log_complete_is_ToTurnIn()
        {
            var i = Base();
            i.LogState = QuestLogState.Complete;
            Assert.Equal(QuestStatus.ToTurnIn, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Log_failed_is_Failed()
        {
            var i = Base();
            i.LogState = QuestLogState.Failed;
            Assert.Equal(QuestStatus.Failed, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void In_progress_in_log_is_InProgress()
        {
            var i = Base();
            i.LogState = QuestLogState.InProgress;
            Assert.Equal(QuestStatus.InProgress, QuestStatusLadder.Decide(i));
        }

        [Fact]
        public void Not_in_log_and_not_pickable_is_None()
        {
            var i = Base();
            Assert.Equal(QuestStatus.None, QuestStatusLadder.Decide(i));
        }

        // --- DecideWithReason: the debug-visibility variant (status + human reason) ---

        [Fact]
        public void DecideWithReason_surfaces_the_not_pickable_reason_for_None()
        {
            var i = Base();
            i.NotPickableReason = "prerequisite quest not done (needs one of: 42)";
            var d = QuestStatusLadder.DecideWithReason(i);
            Assert.Equal(QuestStatus.None, d.Status);
            Assert.Contains("prerequisite quest not done", d.Reason);
        }

        [Fact]
        public void DecideWithReason_None_without_a_detail_still_says_not_pickable()
        {
            var d = QuestStatusLadder.DecideWithReason(Base());
            Assert.Equal(QuestStatus.None, d.Status);
            Assert.Equal("not pickable", d.Reason);
        }

        [Fact]
        public void DecideWithReason_blacklisted_carries_its_reason()
        {
            var i = Base();
            i.IsBlacklisted = true;
            var d = QuestStatusLadder.DecideWithReason(i);
            Assert.Equal(QuestStatus.Blacklisted, d.Status);
            Assert.Equal("blacklisted", d.Reason);
        }

        [Fact]
        public void Decide_and_DecideWithReason_agree_on_the_status()
        {
            var i = Base();
            i.IsPickable = true; // ToPickup
            Assert.Equal(QuestStatusLadder.Decide(i), QuestStatusLadder.DecideWithReason(i).Status);
        }
    }
}
