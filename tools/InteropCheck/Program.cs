using System.Data.SQLite;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StarfrontCollab;
using StarfrontCollab.TargetScheduler;
using StarfrontCollab.Wire;

// End-to-end check of the core library against a real collaboration server.
// Run it against a throwaway server on this machine, never a shared one: it
// enrols a telescope, starts a project and reports frames.
//
//   dotnet run --project tools/InteropCheck -- http://127.0.0.1:8800 <owner-token-file>

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: InteropCheck <server-url> <owner-token-file>");
    return 2;
}
var server = CollabClient.Normalize(args[0], allowLoopbackHttp: true);
if (!server.IsLoopback)
{
    Console.Error.WriteLine("Refusing to run against a server that is not on this computer.");
    return 2;
}
var owner = File.ReadAllText(args[1]).Trim();
const string Profile = "11111111-2222-3333-4444-555555555555";
const double Latitude = 37.0, Longitude = -122.0;
string[] wheel = ["L", "R", "G", "B", "Ha 3nm", "OIII 3nm", "SII 3nm"];
var exposures = new Dictionary<string, double> { ["Ha 3nm"] = 300, ["OIII 3nm"] = 300, ["SII 3nm"] = 300, ["L"] = 120 };
var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
    if (!ok) failures++;
}

using var client = new CollabClient(server);
using var raw = new HttpClient { BaseAddress = new Uri(server, "api/v1/") };

Console.WriteLine($"Server {server}");
var health = await client.HealthAsync(CancellationToken.None);
Check(health.Protocol == 1, $"health: protocol {health.Protocol}, version {health.Version}, sign-in {(health.SignIn ? "on" : "off")}");

// The owner token may enrol telescopes, like a signed-in person.
var enrolled = await client.EnrollAsync(owner, "Interop rig", CancellationToken.None);
Check(enrolled.AgentId.Length > 0, $"enrolled telescope {enrolled.AgentId}");
var agent = enrolled.Token;

var rig = new RigProfile("Interop rig", 530, 3.76, 6248, 4176, 1, false, null, 6,
    [.. wheel.Select(name => (name, Filters.Bandpass(name)))], exposures);
var presence = new Presence(0.7123, 41.27, "exposing", false, "M31 halo panel 0", null, "Interop rig 530");
var hello = await client.HelloAsync(agent, rig, presence, CancellationToken.None);
Check(hello.AgentId == enrolled.AgentId, $"hello answered for {hello.AgentId}; server clock {hello.ServerTime:0}");

using (var seen = await Get("presence", agent))
{
    var row = seen.RootElement.GetProperty("telescopes").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == enrolled.AgentId);
    Check(row.ValueKind == JsonValueKind.Object, "presence lists this telescope");
    if (row.ValueKind == JsonValueKind.Object)
    {
        Check(row.GetProperty("ra").GetDouble() == 0.7123 && row.GetProperty("dec").GetDouble() == 41.27, "presence carries RA in hours and Dec");
        Check(row.GetProperty("state").GetString() == "exposing" && row.GetProperty("online").GetBoolean(), "presence: exposing, online");
        Check(row.GetProperty("name").GetString() == "Interop rig 530", "presence uses the telescope name sent with it");
    }
}

var created = await Post("projects", owner, new JsonObject
{
    ["name"] = "Interop M31",
    ["kind"] = "mosaic",
    ["region"] = new JsonObject { ["ra"] = 10.6847, ["dec"] = 41.269, ["width"] = 5.0, ["height"] = 3.0, ["rotation"] = 0.0 },
    ["requirements"] = new JsonObject
    {
        ["filters"] = new JsonObject { ["H"] = 7.0, ["O"] = 7.0 },
        ["minExposure"] = 120.0,
        ["maxExposure"] = 600.0,
        ["minAltitude"] = 30.0,
        ["maxHfr"] = 4.0,
    },
    ["goals"] = new JsonObject { ["H"] = 10.0, ["O"] = 10.0 },
});
var projectId = created.RootElement.GetProperty("project").GetProperty("id").GetString()!;
Check(projectId.Length > 0, $"owner started project {projectId}");

var open = await client.ProjectsAsync(agent, CancellationToken.None);
var listed = open.FirstOrDefault(p => p.Id == projectId);
Check(listed is { Compatible: true }, $"project listed; compatible: {listed?.Compatible} {listed?.Compatibility}");

var now = DateTimeOffset.UtcNow;
var night = NightSky.For(now, Latitude, Longitude);
await client.JoinAsync(agent, projectId, night, exposures, CancellationToken.None);
Check(true, $"joined for night {night.Name} (Moon {night.MoonIllumination?.ToString("P0") ?? "n/a"}, up {night.MoonUpFraction?.ToString("P0") ?? "n/a"})");

Tonight tonight;
using (var reply = await client.TonightAsync(agent, night, CancellationToken.None))
    tonight = TonightReader.Read(reply.RootElement, enrolled.AgentId, night.Name);
var share = tonight.Shares.Single(s => s.ProjectId == projectId);
Check(share.Holds.Count == 0, "tonight's share is schedulable" + (share.Holds.Count > 0 ? ": " + string.Join("; ", share.Holds) : ""));
Console.WriteLine($"        {share.Cells.Count} cells; tonight {string.Join(", ", share.Demands.GroupBy(d => d.Filter).Select(g => $"{g.Count()} × {g.First().Frames} {g.Key} at {g.First().ExposureSeconds}s"))}");

var folder = Path.Combine(Path.GetTempPath(), "starfront-collab-interop", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var database = Path.Combine(folder, "schedulerdb.sqlite");
BuildTargetScheduler(database);
var store = new TsStore(database);
var request = new ActivationRequest(server.AbsoluteUri, enrolled.AgentId, Profile, tonight, wheel, 1, now);

var preview = store.Activate(request, commit: false);
Check(preview.Changes.Count > 0 && Count(database, "SELECT COUNT(*) FROM target") == 0, $"preview lists {preview.Changes.Count} changes and writes nothing");
var applied = store.Activate(request, commit: true);
Check(applied.Targets == share.Demands.Select(d => d.PanelIndex).Distinct().Count() && applied.Plans == share.Demands.Count,
    $"applied: {applied.Targets} targets, {applied.Plans} plans");
foreach (var line in applied.Changes.Take(6)) Console.WriteLine("        " + line);
Check(store.Activate(request, commit: true).Changes.Count == 0, "applying the same deal again changes nothing");

// Two accepted frames on the first dealt panel, as Target Scheduler would record them.
var first = share.Demands[0];
var targetId = Count(database, "SELECT t.Id FROM target t JOIN starfront_collab_target j ON j.target_guid = t.guid WHERE j.cell_row = @p1 AND j.cell_column = @p2",
    first.Cell.Row, first.Cell.Column);
var wheelName = wheel.First(name => Filters.Fold(name) == first.Filter);
for (var i = 0; i < 2; i++)
{
    var saved = now.AddMinutes(-10 + 6 * i);
    Exec(database, """
        INSERT INTO acquiredimage (projectId, targetId, acquireddate, filtername, gradingStatus, metadata, profileId, guid)
        SELECT projectid, Id, @p2, @p3, 1, @p4, @p5, @p6 FROM target WHERE Id = @p1
        """, targetId, saved.ToUnixTimeSeconds(), wheelName,
        new JsonObject { ["ExposureDuration"] = first.ExposureSeconds, ["HFR"] = 1.8 + 0.2 * i, ["GuidingRMSArcSec"] = 0.55 }.ToJsonString(),
        Profile, Guid.NewGuid().ToString("D"));
}

var scope = new ReportScope(server.AbsoluteUri, enrolled.AgentId, Profile, Latitude, Longitude, now.AddMinutes(1));
var pending = store.PendingReports(scope);
var report = pending.SingleOrDefault();
Check(report is { Frames: 2 }, $"one pending report of {report?.Frames} frames on panel {report?.Key.Panel} {report?.Key.Filter}");
if (report is not null)
{
    var contribution = new Contribution(report.Project, report.Key.Task, report.Key.Night, report.Key.Filter, report.Key.Panel, report.Frames,
        report.Seconds, report.Exposure, report.Footprint, rig.Scale, rig.FocalLength, report.HfrPixels * rig.Scale, report.GuideRms,
        report.MoonIllumination, report.MoonSeparation, Filters.Bandpass(report.FilterName), rig.Colour);
    var verdicts = await client.ReportAsync(agent, [contribution], CancellationToken.None);
    var verdict = verdicts.Single();
    Check(verdict.Accepted, $"server recorded {verdict.Id}: {verdict.Summary}");
    store.RecordReports(scope, [(report, verdict)]);
    Check(store.PendingReports(scope).Count == 0, "nothing left to report");
    var again = await client.ReportAsync(agent, [contribution], CancellationToken.None);
    Check(again.Single().Duplicate, "sending the same report again is a duplicate on the server");
}

using (var project = await Get($"projects/{projectId}", null))
{
    var contributions = project.RootElement.GetProperty("contributions").GetArrayLength();
    Check(contributions == 1, $"the project holds {contributions} contribution");
}

// The deal for another night does not run tonight.
var tomorrow = NightSky.Of(DateOnly.ParseExact(night.Name, "yyyy-MM-dd").AddDays(1), Latitude, Longitude);
using (var reply = await client.TonightAsync(agent, tomorrow, CancellationToken.None))
{
    var later = TonightReader.Read(reply.RootElement, enrolled.AgentId, night.Name);
    Check(later.Shares.All(s => s.Demands.Count == 0), "a share dealt for the next night is held tonight");
}

var goodbye = await client.HelloAsync(agent, rig, new Presence(null, null, "offline", false, null, null, "Interop rig 530"), CancellationToken.None);
using (var seen = await Get("presence", agent))
{
    var row = seen.RootElement.GetProperty("telescopes").EnumerateArray().First(t => t.GetProperty("id").GetString() == goodbye.AgentId);
    Check(row.GetProperty("state").GetString() == "offline" && row.GetProperty("ra").ValueKind == JsonValueKind.Null, "goodbye clears the position");
}

SQLiteConnection.ClearAllPools();
Directory.Delete(folder, recursive: true);
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

async Task<JsonDocument> Get(string route, string? bearer)
{
    using var message = new HttpRequestMessage(HttpMethod.Get, route);
    if (bearer is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    using var response = await raw.SendAsync(message);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

async Task<JsonDocument> Post(string route, string bearer, JsonNode body)
{
    using var message = new HttpRequestMessage(HttpMethod.Post, route)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    using var response = await raw.SendAsync(message);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{route}: {(int)response.StatusCode} {text}");
    return JsonDocument.Parse(text);
}

static void BuildTargetScheduler(string path)
{
    var schema = Path.Combine(AppContext.BaseDirectory, "ts_schema");
    using var db = new SQLiteConnection($"Data Source={path};Pooling=False");
    db.Open();
    foreach (var file in new[] { "initial_schema.sql" }.Concat(Enumerable.Range(1, 23).Select(i => Path.Combine("migrate", i + ".sql"))))
    {
        using var command = new SQLiteCommand(File.ReadAllText(Path.Combine(schema, file)), db);
        command.ExecuteNonQuery();
    }
}

static long Count(string path, string sql, params object[] args)
{
    using var db = new SQLiteConnection($"Data Source={path};Pooling=False");
    db.Open();
    using var command = new SQLiteCommand(sql, db);
    for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + (i + 1), args[i]);
    return Convert.ToInt64(command.ExecuteScalar());
}

static void Exec(string path, string sql, params object[] args)
{
    using var db = new SQLiteConnection($"Data Source={path};Pooling=False");
    db.Open();
    using var command = new SQLiteCommand(sql, db);
    for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + (i + 1), args[i]);
    command.ExecuteNonQuery();
}
