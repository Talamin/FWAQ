using System;
using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class TaskValidityTests
    {
        // A level-40, fully-valid Azeroth task; each test flips only the facts it cares about.
        private static TaskValidityInput Base() => new TaskValidityInput
        {
            PlayerLevel = 40,
            IsInStartingZone = false,
            IsTimedOut = false,
            HasReputationMismatch = false,
            HasEnoughSkill = true,
            IsRecordedAsUnreachable = false,
            HasWorldMapArea = true,
            IsOutlands = false,
            IsNorthrend = false
        };

        private static readonly Func<bool> NotBlacklisted = () => false;
        private static readonly Func<bool> DarkPortalClosed = () => false;
        private static readonly Func<bool> DarkPortalOpen = () => true;

        private static TaskInvalidReason Evaluate(TaskValidityInput f, Func<bool> bl = null, Func<bool> dp = null)
            => TaskValidity.Evaluate(f, bl ?? NotBlacklisted, dp ?? DarkPortalClosed);

        [Fact]
        public void Fully_valid_task_is_valid()
        {
            Assert.Equal(TaskInvalidReason.Valid, Evaluate(Base()));
        }

        [Fact]
        public void Below_12_outside_starting_zone_sticks_to_starting_zone()
        {
            var f = Base();
            f.PlayerLevel = 8;
            f.IsInStartingZone = false;
            // even with other gates that would also fail, the starting-zone check wins (it's first).
            Assert.Equal(TaskInvalidReason.StickingToStartingZone, Evaluate(f, () => true, DarkPortalOpen));
        }

        [Fact]
        public void Below_12_inside_starting_zone_passes_that_check()
        {
            var f = Base();
            f.PlayerLevel = 8;
            f.IsInStartingZone = true;
            Assert.Equal(TaskInvalidReason.Valid, Evaluate(f));
        }

        [Fact]
        public void Timed_out_wins_over_later_checks()
        {
            var f = Base();
            f.IsTimedOut = true;
            Assert.Equal(TaskInvalidReason.TimedOut, Evaluate(f));
        }

        [Fact]
        public void Reputation_mismatch_is_invalid()
        {
            var f = Base();
            f.HasReputationMismatch = true;
            Assert.Equal(TaskInvalidReason.ReputationMismatch, Evaluate(f));
        }

        [Fact]
        public void Insufficient_skill_is_invalid()
        {
            var f = Base();
            f.HasEnoughSkill = false;
            Assert.Equal(TaskInvalidReason.InsufficientSkill, Evaluate(f));
        }

        [Fact]
        public void Recorded_unreachable_is_invalid()
        {
            var f = Base();
            f.IsRecordedAsUnreachable = true;
            Assert.Equal(TaskInvalidReason.Unreachable, Evaluate(f));
        }

        [Fact]
        public void Missing_world_map_area_is_invalid()
        {
            var f = Base();
            f.HasWorldMapArea = false;
            Assert.Equal(TaskInvalidReason.NoWorldMapArea, Evaluate(f));
        }

        [Fact]
        public void Blacklisted_zone_is_invalid()
        {
            Assert.Equal(TaskInvalidReason.ZoneBlacklisted, Evaluate(Base(), () => true));
        }

        [Fact]
        public void Outlands_below_60_sticks_to_azeroth()
        {
            var f = Base();
            f.PlayerLevel = 40;
            f.IsOutlands = true;
            Assert.Equal(TaskInvalidReason.StickingToAzeroth, Evaluate(f));
        }

        [Fact]
        public void Azeroth_at_65_with_dark_portal_open_sticks_to_outlands()
        {
            var f = Base();
            f.PlayerLevel = 65;
            f.IsOutlands = false;
            Assert.Equal(TaskInvalidReason.StickingToOutlands, Evaluate(f, NotBlacklisted, DarkPortalOpen));
        }

        [Fact]
        public void Azeroth_at_65_with_dark_portal_closed_is_still_valid()
        {
            var f = Base();
            f.PlayerLevel = 65;
            f.IsOutlands = false;
            Assert.Equal(TaskInvalidReason.Valid, Evaluate(f, NotBlacklisted, DarkPortalClosed));
        }

        [Fact]
        public void Outlands_at_65_is_valid_and_does_not_consult_dark_portal()
        {
            var f = Base();
            f.PlayerLevel = 65;
            f.IsOutlands = true;
            // The Dark-Portal lookup is expensive; in Outlands it must be skipped (would throw if consulted).
            Func<bool> dpThrows = () => throw new InvalidOperationException("dark portal must not be consulted");
            Assert.Equal(TaskInvalidReason.Valid, Evaluate(f, NotBlacklisted, dpThrows));
        }

        [Fact]
        public void Outside_northrend_at_75_sticks_to_northrend()
        {
            var f = Base();
            f.PlayerLevel = 75;
            f.IsNorthrend = false;
            Assert.Equal(TaskInvalidReason.StickingToNorthrend, Evaluate(f));
        }

        [Fact]
        public void In_northrend_at_75_is_valid()
        {
            var f = Base();
            f.PlayerLevel = 75;
            f.IsNorthrend = true;
            Assert.Equal(TaskInvalidReason.Valid, Evaluate(f));
        }

        [Fact]
        public void Expensive_checks_are_not_run_when_an_earlier_check_fails()
        {
            var f = Base();
            f.IsTimedOut = true;
            // Both lazy delegates throw - they must not be consulted because TimedOut wins first.
            Func<bool> blThrows = () => throw new InvalidOperationException("blacklist must not be scanned");
            Func<bool> dpThrows = () => throw new InvalidOperationException("dark portal must not be consulted");
            Assert.Equal(TaskInvalidReason.TimedOut, Evaluate(f, blThrows, dpThrows));
        }
    }
}
