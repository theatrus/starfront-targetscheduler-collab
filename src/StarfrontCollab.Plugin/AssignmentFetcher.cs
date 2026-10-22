using System.ComponentModel.Composition;

namespace StarfrontCollab.Plugin;

/// NINA composes the manifest and sequence instructions in separate containers.
/// Resolve the running controller at execution time, including for saved sequences
/// and clones created before the plugin has initialized.
[Export(typeof(IAssignmentFetcher))]
internal sealed class AssignmentFetcher : IAssignmentFetcher
{
    private static CollabController? controller;

    internal static CollabController? Controller
    {
        get => Volatile.Read(ref controller);
        set => Volatile.Write(ref controller, value);
    }

    public IReadOnlyList<string> FetchIssues => Controller?.FetchIssues ?? ["Starfront TargetScheduler Collab is not running."];

    public Task FetchTonightAsync(CancellationToken token) => Controller?.FetchTonightAsync(token)
        ?? throw new InvalidOperationException("Starfront TargetScheduler Collab is not running.");
}
