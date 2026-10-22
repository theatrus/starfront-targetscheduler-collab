namespace StarfrontCollab.Plugin;

/// The sequencer uses the same profile, settings and conversation lock as the
/// options page and background check-ins.
public interface IAssignmentFetcher
{
    IReadOnlyList<string> FetchIssues { get; }
    Task FetchTonightAsync(CancellationToken token);
}
