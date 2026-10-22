using NINA.Core.Model;
using NINA.Sequencer.Utility;
using Xunit;

namespace StarfrontCollab.Plugin.Tests;

public sealed class FetchTonightTests
{
    private sealed class Fetcher(Func<CancellationToken, Task> fetch) : IAssignmentFetcher
    {
        public IReadOnlyList<string> FetchIssues { get; set; } = [];
        public Task FetchTonightAsync(CancellationToken token) => fetch(token);
    }

    [Fact]
    public async Task DoesNotCompleteUntilTheAssignmentsAreApplied()
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = new FetchTonight(new Fetcher(token => applied.Task.WaitAsync(token)));
        var execution = step.Execute(null!, CancellationToken.None);
        Assert.False(execution.IsCompleted);
        applied.SetResult();
        await execution;
    }

    [Fact]
    public async Task AFailedPullFailsTheInstructionAndDefaultsToAbort()
    {
        var failure = new InvalidOperationException("The assignment service is unavailable.");
        var step = new FetchTonight(new Fetcher(_ => Task.FromException(failure)));
        var error = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => step.Execute(null!, CancellationToken.None));
        Assert.Same(failure, error.InnerException);
        Assert.Equal(InstructionErrorBehavior.AbortOnError, step.ErrorBehavior);
    }

    [Fact]
    public async Task ValidationRefusesToFetchWithoutConfiguration()
    {
        var called = false;
        var fetcher = new Fetcher(_ => { called = true; return Task.CompletedTask; }) { FetchIssues = ["Sign in first."] };
        var step = new FetchTonight(fetcher);
        Assert.False(step.Validate());
        Assert.Equal("Sign in first.", Assert.Single(step.Issues));
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => step.Execute(null!, CancellationToken.None));
        Assert.False(called);
        fetcher.FetchIssues = [];
        Assert.True(step.Validate());
        Assert.Empty(step.Issues);
    }

    [Fact]
    public async Task CancellationRemainsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var step = new FetchTonight(new Fetcher(token => Task.Delay(Timeout.Infinite, token)));
        var execution = step.Execute(null!, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task CloneKeepsTheFetcherAndUserErrorPolicy()
    {
        var calls = 0;
        var step = new FetchTonight(new Fetcher(_ => { calls++; return Task.CompletedTask; }))
        {
            Name = "Before TS",
            Attempts = 2,
            ErrorBehavior = InstructionErrorBehavior.SkipInstructionSetOnError,
        };
        var copy = Assert.IsType<FetchTonight>(step.Clone());
        Assert.Equal(step.Name, copy.Name);
        Assert.Equal(step.Attempts, copy.Attempts);
        Assert.Equal(step.ErrorBehavior, copy.ErrorBehavior);
        await copy.Execute(null!, CancellationToken.None);
        Assert.Equal(1, calls);
    }
}
