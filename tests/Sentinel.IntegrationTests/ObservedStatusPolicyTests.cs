using Sentinel.Application.Monitoring;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class ObservedStatusPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Healthy", "UP")]
    [InlineData("HttpError", "DOWN")]
    [InlineData("Timeout", "DOWN")]
    [InlineData("DnsFailure", "DOWN")]
    [InlineData("ConnectionFailure", "DOWN")]
    [InlineData("NetworkFailure", "DOWN")]
    [InlineData("UnsafeDestination", "DOWN")]
    [InlineData("UnexpectedOutcome", "DOWN")]
    public void FreshPersistedOutcomeDeterminesStatus(string outcome, string expected)
    {
        Assert.Equal(expected, ObservedStatusPolicy.GetStatus(true, 60, Now.AddSeconds(-1), outcome, Now));
    }

    [Fact]
    public void HealthyResultOneSecondInFutureIsUnknown()
    {
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 60,
            Now.AddSeconds(1), "Healthy", Now));
    }

    [Fact]
    public void FailedResultFarInFutureIsUnknown()
    {
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 60,
            Now.AddYears(1), "Timeout", Now));
    }

    [Fact]
    public void ResultAtCurrentUtcTimeIsEligible()
    {
        Assert.Equal("UP", ObservedStatusPolicy.GetStatus(true, 60, Now, "Healthy", Now));
        Assert.Equal("DOWN", ObservedStatusPolicy.GetStatus(true, 60, Now, "HttpError", Now));
    }

    [Fact]
    public void DisabledMonitorIsUnknownEvenWithFreshHealthyOrFailedHistory()
    {
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(false, 60, Now, "Healthy", Now));
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(false, 60, Now, "Timeout", Now));
    }

    [Fact]
    public void MissingObservationIsUnknown()
    {
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 60, null, null, Now));
    }

    [Fact]
    public void StalenessUsesStrictFiveMinuteFloor()
    {
        Assert.Equal("UP", ObservedStatusPolicy.GetStatus(true, 60,
            Now.AddSeconds(-300), "Healthy", Now));
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 60,
            Now.AddTicks(-TimeSpan.FromSeconds(300).Ticks - 1), "Healthy", Now));
    }

    [Fact]
    public void StalenessUsesThreeIntervalsWhenLongerThanFiveMinutes()
    {
        Assert.Equal("DOWN", ObservedStatusPolicy.GetStatus(true, 120,
            Now.AddSeconds(-360), "HttpError", Now));
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 120,
            Now.AddTicks(-TimeSpan.FromSeconds(360).Ticks - 1), "HttpError", Now));
    }

    [Fact]
    public void StalenessComparesUtcInstantsAcrossOffsets()
    {
        var sameInstantWithOffset = Now.AddSeconds(-300).ToOffset(TimeSpan.FromHours(2));
        Assert.Equal("UP", ObservedStatusPolicy.GetStatus(true, 60,
            sameInstantWithOffset, "Healthy", Now));
        Assert.Equal("UNKNOWN", ObservedStatusPolicy.GetStatus(true, 60,
            sameInstantWithOffset.AddTicks(-1), "Healthy", Now));
    }
}
