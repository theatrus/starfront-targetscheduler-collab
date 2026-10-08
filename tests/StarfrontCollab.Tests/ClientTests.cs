using System.Net;
using System.Text;
using System.Text.Json;
using StarfrontCollab.Wire;
using Xunit;

namespace StarfrontCollab.Tests;

public sealed class ClientTests
{
    private static readonly Uri Server = new("https://collab.example.org/");

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, type) };

    private static RigProfile Profile() => new("Vega 530", 530, 3.76, 6248, 4176, 1, false, null, 6,
        [("Ha 3nm", 3.0), ("OIII", null)], new Dictionary<string, double> { ["Ha 3nm"] = 300 });

    [Fact]
    public async Task HelloSendsTheWholeProfileAndLivePosition()
    {
        var handler = new Handler((_, _) => Reply("""{"agent":"000000000001","name":"Vega","protocol":1,"serverTime":1791171001.0}"""));
        using var client = new CollabClient(Server, handler);
        var presence = new Presence(0.7123, 41.27, "exposing", false, "M31 panel 3", "000000000002", "Vega 530");
        var reply = await client.HelloAsync("agent-token", Profile(), presence, CancellationToken.None);

        Assert.Equal("000000000001", reply.AgentId);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("https://collab.example.org/api/v1/agent/hello", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer agent-token", request.Headers.Authorization!.ToString());
        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;
        Assert.Equal(1, root.GetProperty("protocol").GetInt32());
        var profile = root.GetProperty("profile");
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("rotation").ValueKind);
        Assert.Equal(3.0, profile.GetProperty("filters").GetProperty("Ha 3nm").GetDouble());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("filters").GetProperty("OIII").ValueKind);
        Assert.Equal(300, profile.GetProperty("exposures").GetProperty("Ha 3nm").GetDouble());
        var live = root.GetProperty("presence");
        Assert.Equal(0.7123, live.GetProperty("ra").GetDouble());
        Assert.Equal(41.27, live.GetProperty("dec").GetDouble());
        Assert.Equal("exposing", live.GetProperty("state").GetString());
        Assert.Equal("000000000002", live.GetProperty("project").GetString());
    }

    [Fact]
    public void PresenceWithoutAMountSaysNothingAboutPosition()
    {
        var json = new Presence(null, null, "offline", false, null, null, "Vega 530").ToJson();
        Assert.False(json.ContainsKey("ra"));
        Assert.False(json.ContainsKey("dec"));
        Assert.False(json.ContainsKey("target"));
    }

    [Fact]
    public async Task TonightNamesTheNightAndItsMoon()
    {
        var handler = new Handler((_, _) => Reply(TonightTests.Fixture("starfront-tonight.json")));
        using var client = new CollabClient(Server, handler);
        var night = new NightSky("2026-10-05", default, default, 0.0362, 0.25);
        using var reply = await client.TonightAsync("t", night, CancellationToken.None);
        Assert.Equal("/api/v1/agent/task?night=2026-10-05&moon=0.036&moonUp=0.25", handler.Seen[0].Request.RequestUri!.PathAndQuery);
        Assert.Single(TonightReader.Read(reply.RootElement, "000000000001", "2026-10-05").Shares);
    }

    [Fact]
    public async Task RejectedTokenIsReportedAsUnauthorized()
    {
        using var client = new CollabClient(Server, new Handler((_, _) => Reply("""{"detail":"unknown agent token"}""", HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<CollabException>(() => client.HelloAsync("t", Profile(), null, CancellationToken.None));
        Assert.Equal(CollabFailure.Unauthorized, error.Failure);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var handler = new Handler((_, _) =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://elsewhere.example.org/api/v1/agent/hello");
            return moved;
        });
        using var client = new CollabClient(Server, handler);
        var error = await Assert.ThrowsAsync<CollabException>(() => client.HelloAsync("t", Profile(), null, CancellationToken.None));
        Assert.Equal(CollabFailure.Refused, error.Failure);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task NonJsonRepliesAreRefused()
    {
        using var client = new CollabClient(Server, new Handler((_, _) => Reply("<html></html>", type: "text/html")));
        var error = await Assert.ThrowsAsync<CollabException>(() => client.HealthAsync(CancellationToken.None));
        Assert.Equal(CollabFailure.Malformed, error.Failure);
    }

    [Fact]
    public async Task SignInLinkMustStayOnTheServer()
    {
        using var client = new CollabClient(Server, new Handler((_, _) =>
            Reply("""{"code":"abc","url":"https://phish.example.org/auth/discord/start?code=abc","expiresIn":600}""")));
        var error = await Assert.ThrowsAsync<CollabException>(() => client.StartLoginAsync(CancellationToken.None));
        Assert.Equal(CollabFailure.Malformed, error.Failure);
        // The message names the address the server uses, so a wrong Server setting is easy to fix.
        Assert.Contains("https://phish.example.org", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInWorksOnStarfrontsAddress()
    {
        using var client = new CollabClient(new Uri(KnownServers.Starfront), new Handler((_, _) =>
            Reply("""{"code":"abc","url":"https://collab.starfront.space/auth/discord/start?code=abc","expiresIn":600.0}""")));
        var login = await client.StartLoginAsync(CancellationToken.None);
        Assert.Equal(("abc", TimeSpan.FromMinutes(10)), (login.Code, login.Lifetime));
    }

    [Theory]
    [InlineData("", "https://collab.starfront.space/")]
    [InlineData("https://starfront-bray.duckdns.org", "https://collab.starfront.space/")]
    [InlineData("https://Starfront-Bray.duckdns.org/", "https://collab.starfront.space/")]
    [InlineData("https://collab.starfront.space", "https://collab.starfront.space")]
    [InlineData("https://collab.example.org/", "https://collab.example.org/")]
    public void FormerStarfrontAddressesMoveToTheCurrentOne(string configured, string expected) =>
        Assert.Equal(expected, KnownServers.Canonical(configured));

    [Fact]
    public void TokensMayMoveOnlyBetweenStarfrontsOwnAddresses()
    {
        Assert.Equal(["https://starfront-bray.duckdns.org/"], KnownServers.FormerAddressesOf(new Uri(KnownServers.Starfront)).Select(u => u.AbsoluteUri));
        Assert.Empty(KnownServers.FormerAddressesOf(new Uri("https://collab.example.org/")));
    }

    [Fact]
    public async Task EnrollmentUsesThePersonTokenOnce()
    {
        var handler = new Handler((_, _) => Reply("""{"agent":{"id":"8e6710917f15","name":"Vega"},"token":"agent-secret"}"""));
        using var client = new CollabClient(Server, handler);
        var enrollment = await client.EnrollAsync("person-secret", "Vega 530", CancellationToken.None);
        Assert.Equal(("8e6710917f15", "agent-secret"), (enrollment.AgentId, enrollment.Token));
        Assert.Equal("Bearer person-secret", handler.Seen[0].Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("agent-secret", enrollment.ToString());
    }

    [Fact]
    public async Task AShortReportReplyIsRefused()
    {
        using var client = new CollabClient(Server, new Handler((_, _) =>
            Reply("""{"recorded":[{"id":"aaaaaaaaaaaa","accepted":true,"duplicate":false,"verdict":{"accepted":true,"reasons":[],"unverified":[],"summary":"accepted"}}]}""")));
        var row = Contribution("0");
        var error = await Assert.ThrowsAsync<CollabException>(() => client.ReportAsync("t", [row, row with { Panel = "1" }], CancellationToken.None));
        Assert.Equal(CollabFailure.Malformed, error.Failure);
    }

    [Fact]
    public async Task ReportsCarryLettersPanelStringsAndExplicitNulls()
    {
        var handler = new Handler((_, _) =>
            Reply("""{"recorded":[{"id":"aaaaaaaaaaaa","accepted":true,"duplicate":false,"verdict":{"accepted":true,"summary":"accepted, but guiding was not measured"}}]}"""));
        using var client = new CollabClient(Server, handler);
        var recorded = await client.ReportAsync("t", [Contribution("4")], CancellationToken.None);
        Assert.Equal("accepted, but guiding was not measured", Assert.Single(recorded).Summary);
        using var sent = JsonDocument.Parse(handler.Seen[0].Body!);
        var row = sent.RootElement.GetProperty("contributions")[0];
        Assert.Equal("O", row.GetProperty("filterName").GetString());
        Assert.Equal("4", row.GetProperty("panel").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("guideRms").ValueKind);
        Assert.Equal(10.6847, row.GetProperty("footprint").GetProperty("ra").GetDouble());
    }

    [Theory]
    [InlineData("https://collab.example.org", false, "https://collab.example.org/")]
    [InlineData("https://collab.example.org/base", false, "https://collab.example.org/base/")]
    [InlineData("http://127.0.0.1:8765", true, "http://127.0.0.1:8765/")]
    public void NormalizesServerAddresses(string text, bool loopback, string expected) =>
        Assert.Equal(expected, CollabClient.Normalize(text, loopback).AbsoluteUri);

    [Theory]
    [InlineData("http://collab.example.org", true)]
    [InlineData("http://127.0.0.1:8765", false)]
    [InlineData("https://user@collab.example.org", false)]
    [InlineData("https://collab.example.org/?x=1", false)]
    public void RefusesUnsafeServerAddresses(string text, bool loopback) =>
        Assert.Throws<ArgumentException>(() => CollabClient.Normalize(text, loopback));

    private static Contribution Contribution(string panel) => new("000000000002", "000000000004", "2026-10-05", "O", panel, 11, 3300, 300,
        new Region(10.6847, 41.269, 2.54, 1.70, 35), 1.46, 530, 2.9, null, 0.04, 95.2, 7, false);
}
