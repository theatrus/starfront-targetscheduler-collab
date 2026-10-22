using System.ComponentModel.Composition.Hosting;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using StarfrontCollab.TargetScheduler;
using StarfrontCollab.Tests;
using StarfrontCollab.Wire;
using Xunit;

namespace StarfrontCollab.Plugin.Tests;

public sealed class CollabControllerTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal readonly TestDatabase Database;
        internal readonly CollabController Controller;
        internal readonly RotatorInfo Rotator = new() { Connected = true };
        internal readonly TaskCompletionSource TaskRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<JsonValueKind> AdvertisedRotations = [];
        internal string Mode = CollabSettings.Off;
        internal string? FailAt;
        internal int TaskRequests;
        private readonly NinaRig nina;

        internal Rig(int schema = 23)
        {
            Database = new TestDatabase(schema);
            if (schema >= TsStore.MinimumSchema)
            {
                Database.AddTemplate("Ha 3nm", 300);
                Database.AddTemplate("OIII 3nm", 300);
            }
            var profiles = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profiles.SetupGet(p => p.ActiveProfile.Id).Returns(Guid.Parse(TestDatabase.Profile));
            profiles.SetupGet(p => p.ActiveProfile.Name).Returns("Test rig");
            profiles.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(35);
            profiles.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(-105);
            profiles.SetupGet(p => p.ActiveProfile.FilterWheelSettings.FilterWheelFilters)
                .Returns(new ObserveAllCollection<FilterInfo>([new() { Name = "Ha 3nm" }, new() { Name = "OIII 3nm" }]));
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo());
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo());
            var rotator = new Mock<IRotatorMediator>();
            rotator.Setup(r => r.GetInfo()).Returns(Rotator);
            nina = new NinaRig(profiles.Object, mount.Object, camera.Object, rotator.Object, Mock.Of<IImageSaveMediator>());

            var options = new Mock<IPluginOptionsAccessor>();
            options.Setup(o => o.GetValueString(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string name, string fallback) => name switch
                {
                    nameof(CollabSettings.Mode) => Mode,
                    nameof(CollabSettings.ServerUrl) => "https://collab.example.org/",
                    nameof(CollabSettings.DatabasePath) => Database.Path,
                    _ => fallback,
                });
            options.Setup(o => o.GetValueBoolean(It.IsAny<string>(), It.IsAny<bool>())).Returns((string _, bool fallback) => fallback);
            options.Setup(o => o.GetValueInt32(It.IsAny<string>(), It.IsAny<int>())).Returns((string _, int fallback) => fallback);
            options.Setup(o => o.GetValueDouble(It.IsAny<string>(), It.IsAny<double>())).Returns((string _, double fallback) => fallback);
            Controller = new CollabController(nina, new CollabSettings(options.Object),
                server => new CollabClient(server, new Handler(Send)), (_, _) => new Credential("000000000001", "synthetic-token"));
            Controller.Start();
            // Hello mode has no background assignment pulls. The sequence owns
            // the first fetch, after the test's equipment is connected.
            Mode = CollabSettings.Hello;
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (endpoint == FailAt) return Reply("""{"detail":"temporarily unavailable"}""", HttpStatusCode.ServiceUnavailable);
            if (endpoint == "hello")
            {
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                AdvertisedRotations.Add(sent.RootElement.GetProperty("profile").GetProperty("rotation").ValueKind);
                return Reply("""{"agent":"000000000001","protocol":1}""");
            }
            if (endpoint == "task")
            {
                Interlocked.Increment(ref TaskRequests);
                TaskRequested.TrySetResult();
                await ReleaseTask.Task.WaitAsync(token);
                var night = request.RequestUri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("night=", StringComparison.Ordinal))[6..];
                var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "starfront-tonight.json")))!;
                root.AsObject().Remove("task");
                root["tasks"]![0]!["assignedNight"] = night;
                return Reply(root.ToJsonString());
            }
            if (endpoint == "projects") return Reply("""{"projects":[]}""");
            throw new InvalidOperationException("Unexpected test endpoint: " + endpoint);
        }

        private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public async ValueTask DisposeAsync()
        {
            await Controller.StopAsync(sayGoodbye: false);
            nina.Dispose();
            Database.Dispose();
        }
    }

    [Fact]
    public async Task SeparatelyComposedInstructionsUseTheRunningPluginController()
    {
        await using var rig = new Rig();
        using var catalog = new TypeCatalog(typeof(AssignmentFetcher), typeof(FetchTonight));
        using var firstContainer = new CompositionContainer(catalog);
        using var secondContainer = new CompositionContainer(catalog);
        var first = Assert.IsType<FetchTonight>(firstContainer.GetExportedValue<ISequenceItem>());
        var second = Assert.IsType<FetchTonight>(secondContainer.GetExportedValue<ISequenceItem>());
        Assert.False(first.Validate());
        try
        {
            AssignmentFetcher.Controller = rig.Controller;
            Assert.True(first.Validate());
            Assert.True(second.Validate());
            rig.ReleaseTask.SetResult();
            await ((FetchTonight)first.Clone()).Execute(null!, CancellationToken.None);
            await second.Execute(null!, CancellationToken.None);
            Assert.Equal(2, rig.TaskRequests);
            Assert.Equal(6, rig.Database.Count("SELECT COUNT(*) FROM target"));
        }
        finally { AssignmentFetcher.Controller = null; }
        Assert.False(second.Validate());
    }

    [Fact]
    public async Task SequenceAppliesBeforeReturningEvenWithBackgroundAutoApplyOff()
    {
        await using var rig = new Rig();
        var fetch = rig.Controller.FetchTonightAsync(CancellationToken.None);
        await rig.TaskRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fetch.IsCompleted);
        Assert.Equal(0, rig.Database.Count("SELECT COUNT(*) FROM target"));
        rig.ReleaseTask.SetResult();
        await fetch;
        Assert.Equal(6, rig.Database.Count("SELECT COUNT(*) FROM target"));
        Assert.Equal(35.0, Convert.ToDouble(rig.Database.Scalar("SELECT rotation FROM target ORDER BY Id LIMIT 1")));
        Assert.All(rig.AdvertisedRotations, kind => Assert.Equal(JsonValueKind.Null, kind));
        Assert.False(rig.Controller.AutoApply);

        await rig.Controller.FetchTonightAsync(CancellationToken.None);
        Assert.Equal(6, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("task")]
    public async Task FailedProfileOrAssignmentRequestFailsTheSequencePull(string endpoint)
    {
        await using var rig = new Rig { FailAt = endpoint };
        await Assert.ThrowsAsync<CollabException>(() => rig.Controller.FetchTonightAsync(CancellationToken.None));
        Assert.Equal(0, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public async Task FailedDatabaseApplyFailsTheSequencePull()
    {
        await using var rig = new Rig(schema: 21);
        rig.ReleaseTask.SetResult();
        await Assert.ThrowsAsync<TsException>(() => rig.Controller.FetchTonightAsync(CancellationToken.None));
        Assert.Equal(0, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public async Task CancellationBeforeTheReplyDoesNotApplyAssignments()
    {
        await using var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        var fetch = rig.Controller.FetchTonightAsync(cancel.Token);
        await rig.TaskRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(0, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public async Task PluginShutdownCancelsAnInFlightSequencePull()
    {
        await using var rig = new Rig();
        var fetch = rig.Controller.FetchTonightAsync(CancellationToken.None);
        await rig.TaskRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Controller.StopAsync(sayGoodbye: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(0, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public async Task ConcurrentPullsWaitForTheSameConversationLock()
    {
        await using var rig = new Rig();
        var first = rig.Controller.FetchTonightAsync(CancellationToken.None);
        await rig.TaskRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = rig.Controller.FetchTonightAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, rig.TaskRequests);
        rig.ReleaseTask.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, rig.TaskRequests);
        Assert.Equal(6, rig.Database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public async Task OffModeRefusesExplicitPulls()
    {
        await using var rig = new Rig { Mode = CollabSettings.Off };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Controller.FetchTonightAsync(CancellationToken.None));
        Assert.Equal(0, rig.TaskRequests);
    }
}
