namespace StarfrontCollab;

/// When and what a telescope tells the server about itself.
public static class CheckIn
{
    /// Sent only in the last check-in, when N.I.N.A. closes.
    public const string Offline = "offline";

    /// Retries never wait longer than this, so a short outage does not leave
    /// the telescope past the server's 25-minute online window.
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(10);

    /// Mount events (connect, park, unpark, slew) wait this long, so N.I.N.A.'s
    /// device information has caught up before the position is read.
    public static readonly TimeSpan AfterMountEvent = TimeSpan.FromSeconds(3);

    /// Mount-triggered check-ins are at least this far apart.
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);

    /// The state shown to other telescopes. A running N.I.N.A. with no mount
    /// connected is `idle`, never `offline`.
    public static string State(bool mountConnected, bool slewing, bool atPark, bool tracking, bool exposing) =>
        !mountConnected ? (exposing ? "exposing" : "idle")
        : slewing ? "slewing"
        : exposing ? "exposing"
        : atPark ? "parked"
        : tracking ? "tracking"
        : "idle";

    /// How long to wait after a failed check-in: the interval, doubling with
    /// each failure, but never past 10 minutes unless the interval is longer.
    public static TimeSpan RetryWait(int failures, TimeSpan interval)
    {
        var doubled = interval.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 6));
        var cap = Math.Max(interval.TotalSeconds, MaxRetryWait.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Min(doubled, cap));
    }

    /// When the next check-in should run after a mount event.
    public static DateTimeOffset AfterMount(DateTimeOffset now, DateTimeOffset lastCheckIn)
    {
        var soonest = now + AfterMountEvent;
        var spaced = lastCheckIn + MinGap;
        return spaced > soonest ? spaced : soonest;
    }
}
