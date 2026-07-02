using Wholesome_Auto_Quester.Logic;
using Xunit;

namespace Wholesome_Auto_Quester.Tests
{
    public class RecoveryPolicyTests
    {
        [Fact]
        public void Unreachable_is_no_longer_a_three_hour_park()
        {
            int seconds = RecoveryPolicy.FirstTimeoutSeconds(TaskFailureKind.Unreachable);
            Assert.True(seconds <= 15 * 60, $"unreachable first timeout should be minutes, not hours, was {seconds}s");
        }

        [Fact]
        public void Target_not_found_retries_soon()
        {
            // The respawn case: a short first window so the bot comes back once the mob is back.
            Assert.True(RecoveryPolicy.FirstTimeoutSeconds(TaskFailureKind.TargetNotFound) <= 120);
        }

        [Fact]
        public void Every_failure_kind_has_a_sane_positive_first_timeout()
        {
            foreach (TaskFailureKind kind in System.Enum.GetValues(typeof(TaskFailureKind)))
            {
                int seconds = RecoveryPolicy.FirstTimeoutSeconds(kind);
                Assert.True(seconds > 0 && seconds <= 30 * 60, $"{kind} first timeout out of range: {seconds}s");
            }
        }

        [Fact]
        public void All_failure_kinds_escalate_on_repeat()
        {
            foreach (TaskFailureKind kind in System.Enum.GetValues(typeof(TaskFailureKind)))
            {
                Assert.True(RecoveryPolicy.Escalates(kind));
            }
        }
    }
}
