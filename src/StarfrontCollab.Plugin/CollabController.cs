using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NINA.Core.Utility;
using StarfrontCollab.TargetScheduler;
using StarfrontCollab.Wire;

namespace StarfrontCollab.Plugin;

public sealed record ModeChoice(string Key, string Label);
public sealed record ShareRow(string Title, string Detail, bool Offered, ICommand Accept, ICommand Decline);
public sealed record ProjectRow(string Title, string Detail, bool CanJoin, ICommand Join);

/// Everything the options page shows, and the two background loops behind
/// it: hello (live position) and, in Collab mode, the nightly pull into
/// Target Scheduler. Network work never runs on the UI thread, and only one
/// server conversation runs at a time.
internal sealed class CollabController : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, double> NoExposures = new Dictionary<string, double>();

    private readonly NinaRig rig;
    private readonly CollabSettings settings;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Command signIn, cancelSignIn, saveToken, forget, sendHello, poll, apply;

    private CancellationTokenSource? running;
    private Task? loop;
    private CancellationTokenSource? signingIn;
    private (Uri Server, Guid Profile, Credential? Value)? credential;
    private bool rejected;
    private DateTimeOffset nextHello, nextPoll, lastHello;
    private int helloFailures, pollFailures;
    private ActivationRequest? pending;
    private IReadOnlyDictionary<string, string> targetProjects = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, double> exposures = NoExposures;

    private string accountStatus = "Not signed in.";
    private string helloStatus = "Nothing sent yet.";
    private string collabStatus = "Collab mode is off.";
    private string previewText = "";
    private string reportStatus = "";
    private string agentTokenInput = "";
    private IReadOnlyList<ShareRow> shares = [];
    private IReadOnlyList<ProjectRow> projects = [];

    internal CollabController(NinaRig rig, CollabSettings settings)
    {
        this.rig = rig;
        this.settings = settings;
        signIn = new(SignInAsync, () => signingIn is null, Report);
        cancelSignIn = new(() => { signingIn?.Cancel(); return Task.CompletedTask; }, () => signingIn is not null, Report);
        saveToken = new(SaveTokenAsync, () => signingIn is null && AgentTokenInput.Trim().Length > 0, Report);
        forget = new(ForgetAsync, () => signingIn is null, Report);
        sendHello = new(() => Exclusive(token => HelloAsync(Mode == CollabSettings.Collab, token)), () => Mode != CollabSettings.Off, Report);
        poll = new(() => Exclusive(PollAsync), () => Mode == CollabSettings.Collab, Report);
        apply = new(() => Exclusive(_ => ApplyAsync()), () => pending is not null, Report);
        rig.Slewed += (_, _) =>
        {
            // Share a new position soon after a slew, but not more than every 15 s.
            var soon = lastHello + TimeSpan.FromSeconds(15);
            nextHello = soon > DateTimeOffset.UtcNow ? soon : DateTimeOffset.UtcNow;
            Wake();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ModeChoice> Modes { get; } =
    [
        new(CollabSettings.Off, "Off"),
        new(CollabSettings.Hello, "Hello: share where the telescope points"),
        new(CollabSettings.Collab, "Collab: also pull assignments into Target Scheduler"),
    ];

    public string Mode
    {
        get => settings.Mode;
        set
        {
            if (value == settings.Mode) return;
            settings.Mode = value;
            nextHello = nextPoll = DateTimeOffset.UtcNow;
            if (value != CollabSettings.Collab)
            {
                // Collab results from before would read as current; clear them.
                pending = null;
                CollabStatus = "Collab mode is off.";
                PreviewText = "";
                ReportStatus = "";
            }
            Changed(string.Empty);
            Wake();
        }
    }

    public bool IsCollab => Mode == CollabSettings.Collab;
    public bool IsNotCollab => !IsCollab;

    /// Whether this telescope already has a token: signing in saved one, or one was pasted.
    public bool HasToken => Account() is not null;
    public bool NeedsToken => !HasToken;

    public string TokenNote => HasToken
        ? "This telescope is registered. Its token is saved in Windows Credential Manager, so there is nothing to paste."
        : "Sign in with Discord registers this telescope and saves its token for you. Paste a token only if the server's owner gave you one.";

    public string SharesHint => !IsCollab ? "Shown in Collab mode. Set Mode to Collab to see this telescope's shares."
        : Shares.Count == 0 ? "No shares yet. Join a collaboration below, then click Fetch tonight." : "";
    public bool ShowSharesHint => SharesHint.Length > 0;

    public string ProjectsHint => !IsCollab ? "Shown in Collab mode. Set Mode to Collab to browse and join collaborations."
        : Projects.Count == 0 ? "Not fetched yet. Click Fetch tonight, or wait for the next check-in." : "";
    public bool ShowProjectsHint => ProjectsHint.Length > 0;

    public string ServerUrl
    {
        get => settings.ServerUrl;
        set { settings.ServerUrl = value; ServerChanged(); }
    }

    public bool AllowLoopbackHttp
    {
        get => settings.AllowLoopbackHttp;
        set { settings.AllowLoopbackHttp = value; ServerChanged(); }
    }

    public string TelescopeName
    {
        get => settings.TelescopeName;
        set { settings.TelescopeName = value; Changed(); }
    }

    public string TelescopeNameHint => "Blank uses the profile name: " + rig.Profile.Name;

    public bool SharePosition
    {
        get => settings.SharePosition;
        set { settings.SharePosition = value; Changed(); }
    }

    public bool ShareTarget
    {
        get => settings.ShareTarget;
        set { settings.ShareTarget = value; Changed(); }
    }

    public int CheckInMinutes
    {
        get => settings.CheckInMinutes;
        set { settings.CheckInMinutes = value; nextHello = nextPoll = DateTimeOffset.UtcNow + CheckIn; Changed(); }
    }

    public bool AutoApply
    {
        get => settings.AutoApply;
        set { settings.AutoApply = value; Changed(); }
    }

    public string DatabasePath
    {
        get => settings.DatabasePath;
        set { settings.DatabasePath = value; pending = null; Changed(); }
    }

    public int Binning
    {
        get => settings.Binning;
        set { settings.Binning = value; Changed(); }
    }

    public double HoursPerNight
    {
        get => settings.HoursPerNight;
        set { settings.HoursPerNight = value; Changed(); }
    }

    public double CameraAngle
    {
        get => settings.CameraAngle;
        set { settings.CameraAngle = value; Changed(); }
    }

    public string AgentTokenInput
    {
        get => agentTokenInput;
        set { agentTokenInput = value; Changed(); saveToken.Refresh(); }
    }

    public string AccountStatus { get => accountStatus; private set => Set(ref accountStatus, value); }
    public string HelloStatus { get => helloStatus; private set => Set(ref helloStatus, value); }
    public string CollabStatus { get => collabStatus; private set => Set(ref collabStatus, value); }
    public string PreviewText { get => previewText; private set => Set(ref previewText, value); }
    public string ReportStatus { get => reportStatus; private set => Set(ref reportStatus, value); }
    public IReadOnlyList<ShareRow> Shares
    {
        get => shares;
        private set { Set(ref shares, value); Changed(nameof(SharesHint)); Changed(nameof(ShowSharesHint)); }
    }

    public IReadOnlyList<ProjectRow> Projects
    {
        get => projects;
        private set { Set(ref projects, value); Changed(nameof(ProjectsHint)); Changed(nameof(ShowProjectsHint)); }
    }

    public ICommand SignInCommand => signIn;
    public ICommand CancelSignInCommand => cancelSignIn;
    public ICommand SaveTokenCommand => saveToken;
    public ICommand ForgetCommand => forget;
    public ICommand SendHelloCommand => sendHello;
    public ICommand PollCommand => poll;
    public ICommand ApplyCommand => apply;

    private TimeSpan CheckIn => TimeSpan.FromMinutes(settings.CheckInMinutes);
    private Guid ProfileId => rig.Profile.Id;

    // ----------------------------------------------------------------- lifecycle

    internal void Start()
    {
        running = new CancellationTokenSource();
        rejected = false;
        credential = null;
        nextHello = nextPoll = DateTimeOffset.UtcNow;
        AccountStatus = DescribeAccount();
        loop = Task.Run(() => RunAsync(running.Token));
        Logger.Info($"Starfront TargetScheduler Collab started: mode {Mode}, server {settings.ServerUrl}, Target Scheduler database {settings.DatabasePath}");
        Changed(string.Empty);
    }

    /// Stop the loops and, when the rig has been sharing, say it is offline
    /// rather than leave its last position on everybody's chart.
    internal async Task StopAsync(bool sayGoodbye)
    {
        signingIn?.Cancel();
        running?.Cancel();
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        loop = null;
        pending = null;
        if (!sayGoodbye || Mode == CollabSettings.Off || rejected || Account() is not { } account || Server() is not { } server) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var client = new CollabClient(server);
            await client.HelloAsync(account.Token, rig.ReadProfile(settings, NoExposures),
                new Presence(null, null, "offline", false, null, null, rig.TelescopeName(settings)), deadline.Token);
        }
        catch (Exception error) { Logger.Info("Starfront TargetScheduler Collab could not say goodbye to the server: " + error.Message); }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await TickAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { Logger.Error(error); }
            try { await wake.WaitAsync(TimeSpan.FromSeconds(1), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var mode = Mode;
        if (mode == CollabSettings.Off || rejected || Account() is null) return;
        var now = DateTimeOffset.UtcNow;
        if (mode == CollabSettings.Collab && now >= nextPoll) await Exclusive(PollAsync, token);
        else if (now >= nextHello) await Exclusive(t => HelloAsync(mode == CollabSettings.Collab, t), token);
    }

    private Task Exclusive(Func<CancellationToken, Task> work) =>
        running is { } source ? Exclusive(work, source.Token) : Task.CompletedTask;

    private async Task Exclusive(Func<CancellationToken, Task> work, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await work(token); }
        finally { gate.Release(); }
    }

    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    // ----------------------------------------------------------------- hello

    private async Task<bool> HelloAsync(bool collab, CancellationToken token)
    {
        if (Account() is not { } account || Server() is not { } server) return false;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var profile = rig.ReadProfile(settings, collab ? exposures : NoExposures);
            var presence = rig.ReadPresence(settings, targetProjects);
            using var client = new CollabClient(server);
            var reply = await client.HelloAsync(account.Token, profile, presence, token);
            if (reply.AgentId != account.AgentId) Remember(server, account with { AgentId = reply.AgentId });
            helloFailures = 0;
            lastHello = now;
            nextHello = now + CheckIn;
            var skew = reply.ServerTime is { } time ? now.ToUnixTimeSeconds() - time : 0;
            HelloStatus = $"{Clock(now)}: {Describe(presence)}"
                + (Math.Abs(skew) > 120 ? $" This computer's clock is {Math.Abs(skew):0} s {(skew > 0 ? "ahead of" : "behind")} the server's." : "");
            AccountStatus = $"Connected to {server.Host} as telescope {reply.AgentId}.";
            return true;
        }
        catch (CollabException error) when (error.Failure == CollabFailure.Unauthorized) { Reject(); return false; }
        catch (CollabException error)
        {
            nextHello = now + Backoff(++helloFailures, CheckIn);
            HelloStatus = $"{Clock(now)}: {error.Message} Trying again at {Clock(nextHello)}.";
            return false;
        }
    }

    private static string Describe(Presence presence)
    {
        var where = presence.RaHours is { } ra && presence.Dec is { } dec
            ? $"RA {(int)ra:00}h{(int)(ra * 60 % 60):00}m, Dec {(dec < 0 ? "-" : "+")}{(int)Math.Abs(dec):00}°{(int)(Math.Abs(dec) * 60 % 60):00}′"
            : "no position shared";
        return $"sent {presence.State}, {where}" + (presence.Target is { } target ? $", on {target}" : "") + ".";
    }

    // ----------------------------------------------------------------- collab

    private async Task PollAsync(CancellationToken token)
    {
        if (Account() is not { } account || Server() is not { } server) return;
        var now = DateTimeOffset.UtcNow;
        try
        {
            exposures = Exposures();
            if (!await HelloAsync(collab: true, token))
            {
                if (!rejected) nextPoll = now + Backoff(++pollFailures, CheckIn);
                return;
            }
            account = Account() ?? account;
            var site = rig.Profile.AstrometrySettings;
            var night = await Task.Run(() => NightSky.For(now, site.Latitude, site.Longitude), token);
            Tonight tonight;
            IReadOnlyList<OpenProject>? open = null;
            using (var client = new CollabClient(server))
            {
                using (var reply = await client.TonightAsync(account.Token, night, token))
                    tonight = TonightReader.Read(reply.RootElement, account.AgentId, night.Name);
                try { open = await client.ProjectsAsync(account.Token, token); }
                catch (CollabException error) when (error.Failure != CollabFailure.Unauthorized)
                { Logger.Info("Starfront TargetScheduler Collab could not list open projects: " + error.Message); }
            }
            ShowShares(tonight);
            if (open is not null) ShowProjects(open, night);

            var store = new TsStore(settings.DatabasePath);
            var request = new ActivationRequest(server.AbsoluteUri, account.AgentId, ProfileId.ToString("D"), tonight, rig.WheelFilters(), settings.Binning, now);
            var commit = settings.AutoApply;
            var result = await Task.Run(() => store.Activate(request, commit), token);
            pending = !commit && result.Changes.Count > 0 ? request : null;
            apply.Refresh();
            ShowActivation(result, night, now);
            targetProjects = await Task.Run(() => store.TargetProjects(server.AbsoluteUri, account.AgentId), token);

            await ReportAsync(store, server, account, now, token);
            pollFailures = 0;
            nextPoll = now + CheckIn;
        }
        catch (CollabException error) when (error.Failure == CollabFailure.Unauthorized) { Reject(); }
        catch (Exception error) when (error is CollabException or WireException or TsException or SQLiteException or IOException
            or UnauthorizedAccessException)
        {
            nextPoll = now + Backoff(++pollFailures, CheckIn);
            CollabStatus = $"{Clock(now)}: {error.Message} Trying again at {Clock(nextPoll)}.";
        }
    }

    private async Task ApplyAsync()
    {
        if (pending is not { } request) return;
        var now = DateTimeOffset.UtcNow;
        var store = new TsStore(settings.DatabasePath);
        try
        {
            var result = await Task.Run(() => store.Activate(request with { Now = now }, commit: true));
            pending = null;
            var site = rig.Profile.AstrometrySettings;
            ShowActivation(result, NightSky.For(now, site.Latitude, site.Longitude), now);
            targetProjects = await Task.Run(() => store.TargetProjects(request.Server, request.AgentId));
        }
        catch (Exception error) when (error is TsException or SQLiteException or IOException or UnauthorizedAccessException)
        {
            CollabStatus = $"{Clock(now)}: could not apply: {error.Message}";
        }
        finally { apply.Refresh(); }
    }

    private void ShowActivation(ActivationResult result, NightSky night, DateTimeOffset now)
    {
        var moon = night.MoonIllumination is { } lit ? $", Moon {lit * 100:0}% lit" : ", no astronomical darkness";
        CollabStatus = $"{Clock(now)}: night {night.Name}{moon}. {Count(result.Targets, "panel")}, {Count(result.Plans, "exposure plan")} "
            + (result.Committed ? "in Target Scheduler." : "ready to apply.");
        var lines = new List<string>();
        if (result.Changes.Count == 0) lines.Add("Target Scheduler already matches tonight's assignments.");
        else
        {
            lines.Add(result.Committed ? "Applied to Target Scheduler:" : "Apply will make these changes to Target Scheduler:");
            lines.AddRange(result.Changes.Select(change => "  " + change));
        }
        if (result.Holds.Count > 0)
        {
            lines.Add("Held, not scheduled:");
            lines.AddRange(result.Holds.Select(hold => "  " + hold));
        }
        PreviewText = string.Join(Environment.NewLine, lines);
    }

    private void ShowShares(Tonight tonight) => Shares =
    [
        .. tonight.Shares.Select(share =>
        {
            var panels = share.Demands.Select(d => d.PanelIndex).Distinct().Count();
            var tonightWork = share.Demands.Count == 0 ? "nothing tonight"
                : $"{Count(panels, "panel")}: " + string.Join(", ", share.Demands.GroupBy(d => d.Filter)
                    .Select(g => $"{g.Key} {g.First().Frames} × {g.First().ExposureSeconds:0.#} s"));
            var detail = share.Holds.Count > 0 ? $"{share.State}; held: {string.Join("; ", share.Holds)}" : $"{share.State}; tonight {tonightWork}";
            var offered = share.State == "offered";
            return new ShareRow($"{share.ProjectName} ({share.Kind}, {Count(share.Cells.Count, "cell")})", detail, offered,
                new Command(() => SetTaskStateAsync(share.TaskId, "accepted"), null, Report),
                new Command(() => SetTaskStateAsync(share.TaskId, "declined"), null, Report));
        }),
    ];

    private void ShowProjects(IReadOnlyList<OpenProject> open, NightSky night) => Projects =
    [
        .. open.Select(project =>
        {
            var goals = project.Goals.Count == 0 ? "no depth goal"
                : string.Join(", ", project.Goals.Select(g => $"{g.Key} {(project.Collected.TryGetValue(g.Key, out var done) ? done : 0):0.#}/{g.Value:0.#} h"));
            var fit = project.Compatible switch
            {
                true => "this rig qualifies",
                false => "this rig does not qualify: " + project.Compatibility,
                null => "qualification unknown",
            };
            var detail = $"{project.Kind}; {goals}; {project.Participants} telescopes, {project.Online} online; {fit}"
                + (project.Coordinator.Length > 0 ? $"; run by {project.Coordinator}" : "");
            var title = project.Name + (project.Joined ? " (joined)" : "");
            return new ProjectRow(title, detail, !project.Joined && project.Compatible != false,
                new Command(() => JoinAsync(project.Id, night), () => !project.Joined, Report));
        }),
    ];

    private Task JoinAsync(string projectId, NightSky night) => Exclusive(async token =>
    {
        if (Account() is not { } account || Server() is not { } server) return;
        using var client = new CollabClient(server);
        try
        {
            await client.JoinAsync(account.Token, projectId, night, Exposures(), token);
            CollabStatus = $"{Clock(DateTimeOffset.UtcNow)}: joined. Fetching tonight's share.";
        }
        catch (CollabException error) when (error.Failure == CollabFailure.Unauthorized) { Reject(); return; }
        catch (CollabException error) { CollabStatus = "Could not join: " + error.Message; return; }
        nextPoll = DateTimeOffset.UtcNow;
        Wake();
    });

    private Task SetTaskStateAsync(string taskId, string state) => Exclusive(async token =>
    {
        if (Account() is not { } account || Server() is not { } server) return;
        using var client = new CollabClient(server);
        try { await client.SetTaskStateAsync(account.Token, taskId, state, token); }
        catch (CollabException error) when (error.Failure == CollabFailure.Unauthorized) { Reject(); return; }
        catch (CollabException error) { CollabStatus = $"Could not mark the task {state}: {error.Message}"; return; }
        nextPoll = DateTimeOffset.UtcNow;
        Wake();
    });

    private async Task ReportAsync(TsStore store, Uri server, Credential account, DateTimeOffset now, CancellationToken token)
    {
        var site = rig.Profile.AstrometrySettings;
        var scope = new ReportScope(server.AbsoluteUri, account.AgentId, ProfileId.ToString("D"), site.Latitude, site.Longitude, now);
        var reports = await Task.Run(() => store.PendingReports(scope), token);
        if (reports.Count == 0)
        {
            ReportStatus = $"{Clock(now)}: no new accepted frames to report.";
            return;
        }
        var optics = rig.ReadProfile(settings, NoExposures);
        int sent = 0, accepted = 0;
        var refused = new List<string>();
        using var client = new CollabClient(server);
        foreach (var batch in reports.Chunk(200))
        {
            Contribution[] rows = [.. batch.Select(r => new Contribution(r.Project, r.Key.Task, r.Key.Night, r.Key.Filter, r.Key.Panel, r.Frames,
                r.Seconds, r.Exposure, r.Footprint, optics.Scale, optics.FocalLength, r.HfrPixels * optics.Scale, r.GuideRms,
                r.MoonIllumination, r.MoonSeparation, Filters.Bandpass(r.FilterName), optics.Colour))];
            var verdicts = await client.ReportAsync(account.Token, rows, token);
            await Task.Run(() => store.RecordReports(scope, [.. batch.Zip(verdicts)]), token);
            sent += rows.Length;
            accepted += verdicts.Count(v => v.Accepted);
            refused.AddRange(batch.Zip(verdicts).Where(p => !p.Second.Accepted).Select(p => $"{p.First.Key.Filter} panel {p.First.Key.Panel}: {p.Second.Summary}"));
        }
        ReportStatus = $"{Clock(now)}: reported {sent} panel-night{(sent == 1 ? "" : "s")}, {accepted} accepted."
            + (refused.Count > 0 ? " Not accepted: " + string.Join("; ", refused.Take(5)) : "");
    }

    private IReadOnlyDictionary<string, double> Exposures()
    {
        try { return new TsStore(settings.DatabasePath).DefaultExposures(ProfileId.ToString("D"), settings.Binning, rig.WheelFilters()); }
        catch (Exception error) when (error is TsException or SQLiteException or IOException or UnauthorizedAccessException)
        {
            Logger.Info("Starfront TargetScheduler Collab could not read Target Scheduler templates: " + error.Message);
            return NoExposures;
        }
    }

    // ----------------------------------------------------------------- account

    private async Task SignInAsync()
    {
        if (Server() is not { } server) return;
        using var cancel = running is { } source ? CancellationTokenSource.CreateLinkedTokenSource(source.Token) : new CancellationTokenSource();
        signingIn = cancel;
        RefreshAccountCommands();
        string? person = null;
        try
        {
            using var client = new CollabClient(server);
            var health = await client.HealthAsync(cancel.Token);
            if (!health.SignIn)
            {
                AccountStatus = "This server does not offer Discord sign-in. Paste a telescope token from its owner instead.";
                return;
            }
            var login = await client.StartLoginAsync(cancel.Token);
            Process.Start(new ProcessStartInfo(login.Url.AbsoluteUri) { UseShellExecute = true });
            AccountStatus = "Finish signing in with Discord in your browser. Waiting…";
            var deadline = DateTimeOffset.UtcNow + login.Lifetime;
            while (person is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
                LoginPoll state;
                try { state = await client.PollLoginAsync(login.Code, cancel.Token); }
                catch (CollabException error) when (error.Failure is CollabFailure.RateLimited or CollabFailure.Timeout or CollabFailure.Unavailable)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancel.Token);
                    continue;
                }
                if (state.State == "pending") continue;
                if (state.State != "done")
                {
                    AccountStatus = state.State == "expired" ? "The sign-in expired. Press Sign in to try again." : "That sign-in was already used. Press Sign in to try again.";
                    return;
                }
                person = state.Token;
            }
            if (person is null)
            {
                AccountStatus = "The sign-in was not finished in time. Press Sign in to try again.";
                return;
            }
            Enrollment enrollment;
            try { enrollment = await client.EnrollAsync(person, rig.TelescopeName(settings), cancel.Token); }
            catch (CollabException error) when (error.Failure is CollabFailure.Timeout or CollabFailure.Unavailable or CollabFailure.Malformed)
            {
                // The server may have enrolled the telescope and the answer was lost.
                // Enrolling again would register a second one, so stop here.
                AccountStatus = "Signed in, but the server's answer to registering this telescope was lost. It may exist on the server without a token here. Sign in again to register another, and ask the server's owner to remove the spare.";
                return;
            }
            Remember(server, new Credential(enrollment.AgentId, enrollment.Token));
            rejected = false;
            AccountStatus = $"Signed in. This telescope is {enrollment.AgentId} on {server.Host}.";
            nextHello = nextPoll = DateTimeOffset.UtcNow;
            Wake();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { AccountStatus = "Sign-in cancelled."; }
        catch (CollabException error) { AccountStatus = "Sign-in failed: " + error.Message; }
        finally
        {
            person = null;
            signingIn = null;
            RefreshAccountCommands();
        }
    }

    private Task SaveTokenAsync() => Exclusive(async token =>
    {
        if (Server() is not { } server) return;
        var secret = AgentTokenInput.Trim();
        if (!CollabClient.ValidSecret(secret))
        {
            AccountStatus = "That does not look like a telescope token.";
            return;
        }
        try
        {
            // A hello both proves the token and says which telescope it is.
            using var client = new CollabClient(server);
            var presence = Mode == CollabSettings.Off ? null : rig.ReadPresence(settings, targetProjects);
            var reply = await client.HelloAsync(secret, rig.ReadProfile(settings, NoExposures), presence, token);
            Remember(server, new Credential(reply.AgentId, secret));
            rejected = false;
            AgentTokenInput = "";
            AccountStatus = $"Token saved. This telescope is {reply.AgentId} on {server.Host}.";
            nextHello = nextPoll = DateTimeOffset.UtcNow;
            Wake();
        }
        catch (CollabException error)
        {
            AccountStatus = error.Failure == CollabFailure.Unauthorized ? "The server does not know that token." : "Could not check the token: " + error.Message;
        }
    });

    private Task ForgetAsync()
    {
        if (Server() is { } server)
        {
            CredentialStore.Forget(server, ProfileId);
            credential = (server, ProfileId, null);
        }
        rejected = false;
        pending = null;
        AccountStatus = "Not signed in. The telescope stays registered on the server; signing in again registers a new one.";
        TokenChanged();
        return Task.CompletedTask;
    }

    private Credential? Account()
    {
        if (Server() is not { } server) return null;
        if (credential is { } cached && cached.Server == server && cached.Profile == ProfileId) return cached.Value;
        Credential? value = null;
        try { value = CredentialStore.Read(server, ProfileId) ?? MoveFromFormerAddress(server); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { AccountStatus = error.Message; }
        credential = (server, ProfileId, value);
        return value;
    }

    /// A token saved while Starfront's server had its earlier name is the same
    /// telescope on the same server: file it under the current address.
    private Credential? MoveFromFormerAddress(Uri server)
    {
        foreach (var former in KnownServers.FormerAddressesOf(server))
        {
            if (CredentialStore.Read(former, ProfileId) is not { } moved) continue;
            CredentialStore.Store(server, ProfileId, moved);
            CredentialStore.Forget(former, ProfileId);
            Logger.Info($"Starfront TargetScheduler Collab moved telescope {moved.AgentId}'s token from {former.Host} to {server.Host}.");
            return moved;
        }
        return null;
    }

    private void Remember(Uri server, Credential value)
    {
        CredentialStore.Store(server, ProfileId, value);
        credential = (server, ProfileId, value);
        TokenChanged();
    }

    private void TokenChanged()
    {
        Changed(nameof(HasToken));
        Changed(nameof(NeedsToken));
        Changed(nameof(TokenNote));
    }

    private void Reject()
    {
        rejected = true;
        pending = null;
        AccountStatus = "The server rejected this telescope's token. Sign in again or paste a new token.";
    }

    private string DescribeAccount() => Server() is not { } server ? AccountStatus
        : Account() is { } account ? $"Signed in to {server.Host} as telescope {(account.AgentId.Length > 0 ? account.AgentId : "(not yet confirmed)")}."
        : "Not signed in.";

    private Uri? Server()
    {
        try { return CollabClient.Normalize(settings.ServerUrl, settings.AllowLoopbackHttp); }
        catch (ArgumentException error)
        {
            AccountStatus = error.Message;
            return null;
        }
    }

    private void ServerChanged()
    {
        credential = null;
        rejected = false;
        pending = null;
        targetProjects = new Dictionary<string, string>();
        exposures = NoExposures;
        Shares = [];
        Projects = [];
        AccountStatus = DescribeAccount();
        nextHello = nextPoll = DateTimeOffset.UtcNow;
        Changed(string.Empty);
    }

    internal void ProfileChanged() => ServerChanged();

    private void RefreshAccountCommands()
    {
        signIn.Refresh();
        cancelSignIn.Refresh();
        saveToken.Refresh();
        forget.Refresh();
    }

    // ----------------------------------------------------------------- plumbing

    private static TimeSpan Backoff(int failures, TimeSpan interval) =>
        TimeSpan.FromSeconds(Math.Min(1800, interval.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6))));

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private void Report(Exception error)
    {
        Logger.Error(error);
        CollabStatus = "Something went wrong; see the N.I.N.A. log. " + error.Message;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }

    private void Changed([CallerMemberName] string name = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        sendHello?.Refresh();
        poll?.Refresh();
        apply?.Refresh();
    }
}
