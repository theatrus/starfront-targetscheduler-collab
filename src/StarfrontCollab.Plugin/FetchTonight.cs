using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;

namespace StarfrontCollab.Plugin;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Fetch tonight")]
[ExportMetadata("Description", "Send the current equipment profile, fetch tonight's Starfront assignments and apply them to Target Scheduler before continuing.")]
[ExportMetadata("Category", "Starfront TargetScheduler Collab")]
[ExportMetadata("Icon", "DownloadSVG")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class FetchTonight : SequenceItem, IValidatable
{
    private readonly IAssignmentFetcher fetcher;
    private IList<string> issues = [];

    [ImportingConstructor]
    public FetchTonight(IAssignmentFetcher fetcher)
    {
        this.fetcher = fetcher;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }

    public IList<string> Issues
    {
        get => issues;
        private set { issues = value; RaisePropertyChanged(); }
    }

    public bool Validate()
    {
        Issues = [.. fetcher.FetchIssues];
        return Issues.Count == 0;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Validate()) throw new SequenceEntityFailedException(string.Join(" ", Issues));
        progress?.Report(new ApplicationStatus { Status = "Starfront: fetching and applying tonight's assignments" });
        try
        {
            await fetcher.FetchTonightAsync(token);
            progress?.Report(new ApplicationStatus { Status = "Starfront: tonight's assignments applied to Target Scheduler" });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new SequenceEntityFailedException("Starfront: could not fetch and apply tonight's assignments. " + error.Message, error);
        }
    }

    public override object Clone()
    {
        var copy = new FetchTonight(fetcher);
        copy.CopyMetaData(this);
        return copy;
    }
}
