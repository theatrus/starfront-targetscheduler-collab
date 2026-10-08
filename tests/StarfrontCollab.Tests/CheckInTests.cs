using Xunit;

namespace StarfrontCollab.Tests;

public sealed class CheckInTests
{
    [Theory]
    [InlineData(false, false, false, false, false, "idle")]
    [InlineData(false, false, false, false, true, "exposing")]
    [InlineData(true, true, false, true, true, "slewing")]
    [InlineData(true, false, false, true, true, "exposing")]
    [InlineData(true, false, true, false, false, "parked")]
    [InlineData(true, false, false, true, false, "tracking")]
    [InlineData(true, false, false, false, false, "idle")]
    public void StateDescribesTheRig(bool mount, bool slewing, bool atPark, bool tracking, bool exposing, string expected) =>
        Assert.Equal(expected, CheckIn.State(mount, slewing, atPark, tracking, exposing));

    [Fact]
    public void ARunningRigIsNeverReportedOffline()
    {
        foreach (var mount in new[] { false, true })
            foreach (var exposing in new[] { false, true })
                Assert.NotEqual(CheckIn.Offline, CheckIn.State(mount, false, false, false, exposing));
    }

    [Theory]
    [InlineData(1, 5, 5)]
    [InlineData(2, 5, 10)]
    [InlineData(3, 5, 10)]
    [InlineData(9, 5, 10)]
    [InlineData(1, 1, 1)]
    [InlineData(4, 1, 8)]
    [InlineData(6, 1, 10)]
    [InlineData(3, 30, 30)]
    public void RetriesStayInsideTheOnlineWindow(int failures, int intervalMinutes, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), CheckIn.RetryWait(failures, TimeSpan.FromMinutes(intervalMinutes)));
        // The server counts a telescope online for 25 minutes after any contact.
        if (intervalMinutes <= 10) Assert.True(CheckIn.RetryWait(failures, TimeSpan.FromMinutes(intervalMinutes)) < TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void MountEventsCheckInSoonButNotTooOften()
    {
        var now = new DateTimeOffset(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddSeconds(3), CheckIn.AfterMount(now, now.AddMinutes(-4)));
        Assert.Equal(now.AddSeconds(10), CheckIn.AfterMount(now, now.AddSeconds(-5)));
    }
}
