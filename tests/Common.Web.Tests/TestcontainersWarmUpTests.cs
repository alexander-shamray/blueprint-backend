using System.Text.RegularExpressions;
using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="TestcontainersWarmUp"/> does with a type initialiser that threw.</summary>
public class TestcontainersWarmUpTests
{
    private sealed class Poisoned
    {
        public static readonly int Value = Throw();

        private static int Throw() => throw new RegexMatchTimeoutException();
    }

    [Fact]
    public void A_failed_type_initialiser_is_reworded_to_name_the_step_and_its_cause()
    {
        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(
            () => TestcontainersWarmUp.Ensure(() => _ = Poisoned.Value));

        thrown.Message.ShouldContain("Testcontainers' static initialisation failed (RegexMatchTimeoutException)");
        thrown.InnerException.ShouldBeOfType<TypeInitializationException>();
    }

    [Fact]
    public void A_failure_that_is_not_a_type_initialiser_passes_through_unchanged()
    {
        Should.Throw<TimeoutException>(() => TestcontainersWarmUp.Ensure(() => throw new TimeoutException()));
    }

    [Fact]
    public void The_real_initialisers_run_without_a_daemon()
    {
        Should.NotThrow(TestcontainersWarmUp.Ensure);
    }
}
