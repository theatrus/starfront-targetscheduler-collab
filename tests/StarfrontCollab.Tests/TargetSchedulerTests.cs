using System.Text.Json.Nodes;
using StarfrontCollab.TargetScheduler;
using StarfrontCollab.Wire;
using Xunit;

namespace StarfrontCollab.Tests;

public sealed class TargetSchedulerTests : IDisposable
{
    private const string Server = "https://collab.example.org/";
    private const string Agent = "000000000001";
    private static readonly DateTimeOffset Evening = new(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
    private static readonly string[] Wheel = ["L", "Ha 3nm", "OIII 3nm", "SII 3nm"];

    private readonly TestDatabase database = new();
    private TsStore Store => new(database.Path);

    private static ActivationRequest Request(Tonight tonight, DateTimeOffset? now = null, IReadOnlyList<string>? wheel = null) =>
        new(Server, Agent, TestDatabase.Profile, tonight, wheel ?? Wheel, 1, now ?? Evening);

    private static Tonight Tonight(string night = "2026-10-05", Action<JsonObject>? change = null)
    {
        var root = JsonNode.Parse(TonightTests.Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("task");
        var task = root["tasks"]![0]!.AsObject();
        task["assignedNight"] = night;
        change?.Invoke(task);
        return TonightTests.Read(root.ToJsonString(), night: night);
    }

    [Fact]
    public void PreviewListsTheWorkAndWritesNothing()
    {
        var result = Store.Activate(Request(Tonight()), commit: false);

        Assert.False(result.Committed);
        Assert.Equal((6, 6), (result.Targets, result.Plans));
        Assert.Contains("Created Target Scheduler project M31 halo in narrowband", result.Changes);
        Assert.Contains("Added exposure template OIII 3nm 1x1", result.Changes);
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM project"));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'starfront_collab%'"));
    }

    [Fact]
    public void ApplyCreatesOneProjectSixPanelsAndTheirPlans()
    {
        Store.Activate(Request(Tonight()), commit: true);

        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM project WHERE profileId = @p1 AND isMosaic = 1 AND state = 1 AND minimumaltitude = 30 AND guid IS NOT NULL", TestDatabase.Profile));
        Assert.Equal(8, database.Count("SELECT COUNT(*) FROM ruleweight"));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM target WHERE active = 1 AND epochcode = 2 AND guid IS NOT NULL"));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE desired = 11 AND exposure = 300 AND enabled = 1 AND guid IS NOT NULL"));
        // Target Scheduler stores right ascension in hours.
        Assert.Equal(13.720405230187254 / 15.0, Convert.ToDouble(database.Scalar("SELECT ra FROM target WHERE name = 'M31 halo in narrowband panel 2'")), 9);
        Assert.Equal(35.0, Convert.ToDouble(database.Scalar("SELECT rotation FROM target WHERE name = 'M31 halo in narrowband panel 2'")), 9);
        Assert.Equal("OIII 3nm", database.Scalar("SELECT filtername FROM exposuretemplate"));
    }

    [Fact]
    public void ApplyingTheSameDealAgainChangesNothing()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var again = Store.Activate(Request(Tonight()), commit: true);
        Assert.Empty(again.Changes);
        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM project"));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan"));
    }

    [Fact]
    public void CheckInsAllNightForSeveralNightsReuseTheSameRows()
    {
        // Twenty 5-minute check-ins a night for three nights, with frames arriving between them.
        string[] nights = ["2026-10-05", "2026-10-06", "2026-10-07"];
        for (var night = 0; night < nights.Length; night++)
        {
            for (var checkIn = 0; checkIn < 20; checkIn++)
            {
                var result = Store.Activate(Request(Tonight(nights[night]), Evening.AddDays(night).AddMinutes(5 * checkIn)), commit: true);
                if (checkIn > 0) Assert.Empty(result.Changes);
                database.Exec("UPDATE exposureplan SET acquired = acquired + 1, accepted = accepted + 1");
            }
        }

        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM project"));
        Assert.Equal(8, database.Count("SELECT COUNT(*) FROM ruleweight"));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM target"));
        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM exposuretemplate"));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan"));
        // One goal record per plan per night.
        Assert.Equal(18, database.Count("SELECT COUNT(*) FROM starfront_collab_plan"));
        // The last night's goal: 40 frames from the first two nights, plus tonight's 11.
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE desired = 51"));
    }

    [Fact]
    public void AnExistingTemplateOnTheWheelIsReused()
    {
        database.AddTemplate("OIII 3nm", 300);
        var result = Store.Activate(Request(Tonight()), commit: true);
        Assert.DoesNotContain(result.Changes, c => c.StartsWith("Added exposure template", StringComparison.Ordinal));
        Assert.Equal(1, database.Count("SELECT COUNT(*) FROM exposuretemplate"));
    }

    [Fact]
    public void TheLongestMatchingTemplateIsUsed()
    {
        database.AddTemplate("OIII 3nm", 300);
        database.AddTemplate("OIII 3nm", 600);
        database.AddTemplate("OIII 3nm", 180);
        Store.Activate(Request(Tonight()), commit: true);

        var longest = database.Scalar("SELECT Id FROM exposuretemplate WHERE defaultexposure = 600")!;
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = @p1", longest));
        // The sub length is the one the server dealt; the template supplies filter and camera settings.
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE exposure = 300"));
        Assert.Equal(3, database.Count("SELECT COUNT(*) FROM exposuretemplate"));
    }

    [Fact]
    public void ATemplateNamingTheWheelFilterBeatsALongerOneThatDoesNot()
    {
        database.AddTemplate("OIII 3nm", 300);
        database.AddTemplate("O-III", 900);
        Store.Activate(Request(Tonight()), commit: true);
        var onWheel = database.Scalar("SELECT Id FROM exposuretemplate WHERE filtername = 'OIII 3nm'")!;
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE exposureTemplateId = @p1", onWheel));
    }

    [Theory]
    [InlineData("HA")]
    [InlineData("H-alpha")]
    [InlineData("Ha 7nm")]
    public void HydrogenAlphaTemplatesMatchH(string name)
    {
        database.AddTemplate(name, 600);
        var h = Tonight(change: t =>
        {
            t["visit"]!["frames"] = new JsonObject { ["H"] = 11 };
            t["filters"] = new JsonArray(new JsonObject { ["filter"] = "H", ["exposure"] = 300 });
        });
        var result = Store.Activate(Request(h, wheel: ["L", name]), commit: true);
        Assert.Empty(result.Holds);
        Assert.DoesNotContain(result.Changes, c => c.StartsWith("Added exposure template", StringComparison.Ordinal));
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan"));
    }

    [Fact]
    public void TheServerIsToldTheLongestTemplateExposure()
    {
        database.AddTemplate("Ha 3nm", 300);
        database.AddTemplate("Ha 3nm", 600);
        database.AddTemplate("Red", 120);
        database.AddTemplate("R", 180);
        var exposures = Store.DefaultExposures(TestDatabase.Profile, 1, ["Red", "Ha 3nm", "SII 3nm"]);
        Assert.Equal(600, exposures["Ha 3nm"]);
        // The template naming the wheel filter wins over a longer one that only folds to it.
        Assert.Equal(120, exposures["Red"]);
        Assert.False(exposures.ContainsKey("SII 3nm"));
    }

    [Fact]
    public void ANightsGoalSitsOnTopOfFramesAlreadyTaken()
    {
        Store.Activate(Request(Tonight()), commit: true);
        database.Exec("UPDATE exposureplan SET acquired = 9, accepted = 7");

        // Later the same night: the goal does not move with tonight's frames.
        Store.Activate(Request(Tonight()), commit: true);
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE desired = 11"));

        // The next night starts from the acquired count while the grader is off...
        Store.Activate(Request(Tonight("2026-10-06"), Evening.AddDays(1)), commit: true);
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE desired = 20"));

        // ...and from the accepted count once somebody turns it on.
        database.Exec("UPDATE project SET enablegrader = 1");
        Store.Activate(Request(Tonight("2026-10-07"), Evening.AddDays(2)), commit: true);
        Assert.Equal(6, database.Count("SELECT COUNT(*) FROM exposureplan WHERE desired = 18"));
    }

    [Fact]
    public void NewProjectsStartWithTheGraderOff()
    {
        Store.Activate(Request(Tonight()), commit: true);
        Assert.Equal(0, database.Count("SELECT enablegrader FROM project"));
    }

    [Fact]
    public void WithTheGraderOnOnlyAcceptedFramesAreReported()
    {
        Store.Activate(Request(Tonight()), commit: true);
        database.Exec("UPDATE project SET enablegrader = 1");
        var panel = database.Scalar("SELECT Id FROM target WHERE name = 'M31 halo in narrowband panel 4'")!;
        AddFrame(panel, Evening, "OIII 3nm", 1);
        AddFrame(panel, Evening.AddMinutes(6), "OIII 3nm", 0); // not graded yet
        var scope = new ReportScope(Server, Agent, TestDatabase.Profile, 37, -120, Evening);
        Assert.Equal(1, Assert.Single(Store.PendingReports(scope)).Frames);
        database.Exec("UPDATE project SET enablegrader = 0");
        Assert.Equal(2, Assert.Single(Store.PendingReports(scope)).Frames);
    }

    [Fact]
    public void PanelsNotDealtTonightArePaused()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var smaller = Tonight("2026-10-06", t => t["share"] = new JsonArray(0, 1));
        var result = Store.Activate(Request(smaller, Evening.AddDays(1)), commit: true);

        Assert.Equal(2, database.Count("SELECT COUNT(*) FROM exposureplan WHERE enabled = 1"));
        Assert.Equal(4, database.Count("SELECT COUNT(*) FROM exposureplan WHERE enabled = 0"));
        Assert.Contains("Paused M31 halo in narrowband panel 5 O: not dealt for 2026-10-06", result.Changes);
    }

    [Fact]
    public void AHeldShareShootsNothing()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var held = Tonight("2026-10-06", t => t["state"] = "offered");
        var result = Store.Activate(Request(held, Evening.AddDays(1)), commit: true);

        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM exposureplan WHERE enabled = 1"));
        Assert.Contains("M31 halo in narrowband: offered: accept it to schedule it", result.Holds);
    }

    [Fact]
    public void AFilterTheWheelLacksIsHeldNotGuessed()
    {
        var result = Store.Activate(Request(Tonight(), wheel: ["L", "R", "G", "B"]), commit: true);
        Assert.Equal(0, result.Plans);
        Assert.Single(result.Holds, h => h.Contains("no filter on this wheel", StringComparison.Ordinal));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM exposureplan"));
        // Nothing to shoot means nothing written, not an empty project.
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM project"));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public void FiltersTheWheelHasArePlannedAndTheRestHeld()
    {
        var both = Tonight(change: t =>
        {
            t["visit"]!["frames"] = new JsonObject { ["O"] = 11, ["H"] = 11 };
            t["filters"] = new JsonArray(new JsonObject { ["filter"] = "O", ["exposure"] = 300 }, new JsonObject { ["filter"] = "H", ["exposure"] = 300 });
        });
        var result = Store.Activate(Request(both, wheel: ["L", "OIII 3nm"]), commit: true);
        Assert.Equal((6, 6), (result.Targets, result.Plans));
        Assert.Single(result.Holds, h => h.EndsWith("is H", StringComparison.Ordinal));
    }

    [Fact]
    public void ACellThatMovesAwayFromItsFramesGetsANewTarget()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var panel = database.Scalar("SELECT Id FROM target WHERE name = 'M31 halo in narrowband panel 0'")!;
        AddFrame(panel, Evening, "OIII 3nm", 1);
        var moved = Tonight("2026-10-05", t => t["cells"]![0]!["dec"] = 38.5);

        var result = Store.Activate(Request(moved), commit: true);

        Assert.Contains(result.Changes, c => c.StartsWith("Retired M31 halo in narrowband panel 0", StringComparison.Ordinal));
        Assert.Equal(0, database.Count("SELECT active FROM target WHERE Id = @p1", panel));
        Assert.Equal(7, database.Count("SELECT COUNT(*) FROM target"));
    }

    [Fact]
    public void AcceptedFramesBecomeOneReportPerPanelFilterAndNight()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var panel = database.Scalar("SELECT Id FROM target WHERE name = 'M31 halo in narrowband panel 3'")!;
        AddFrame(panel, Evening, "OIII 3nm", 1, hfr: 2.0, rms: 0.6);
        AddFrame(panel, Evening.AddMinutes(6), "OIII 3nm", 1, hfr: 2.4, rms: 0.8);
        AddFrame(panel, Evening.AddMinutes(12), "OIII 3nm", 2);              // rejected
        AddFrame(panel, Evening.AddMinutes(18), "Ha 3nm", 1);               // another filter
        AddFrame(panel, Evening.AddMinutes(24), "OIII 3nm", 1, seconds: 60); // another sub length
        var scope = new ReportScope(Server, Agent, TestDatabase.Profile, 37, -120, Evening.AddHours(2));

        var report = Assert.Single(Store.PendingReports(scope));
        Assert.Equal(new ReportKey("000000000004", "2026-10-05", "3", "O"), report.Key);
        Assert.Equal(("000000000002", 2, 600.0, 300.0), (report.Project, report.Frames, report.Seconds, report.Exposure));
        Assert.Equal(2.2, report.HfrPixels!.Value, 9);
        Assert.Equal(0.7, report.GuideRms!.Value, 9);
        Assert.Equal(7.580299025898128, report.Footprint.Ra, 9);
        Assert.Equal(41.269, report.Footprint.Dec, 9);
        Assert.NotNull(report.MoonSeparation);

        Store.RecordReports(scope, [(report, new Recorded("aaaaaaaaaaaa", true, false, "accepted"))]);
        Assert.Empty(Store.PendingReports(scope));

        AddFrame(panel, Evening.AddMinutes(30), "OIII 3nm", 1);
        Assert.Equal(3, Assert.Single(Store.PendingReports(scope)).Frames);
    }

    [Fact]
    public void AnUnmeasuredFrameMakesTheNightsMeasurementUnknown()
    {
        Store.Activate(Request(Tonight()), commit: true);
        var panel = database.Scalar("SELECT Id FROM target WHERE name = 'M31 halo in narrowband panel 1'")!;
        AddFrame(panel, Evening, "OIII 3nm", 1, hfr: 2.0, rms: 0.6);
        AddFrame(panel, Evening.AddMinutes(6), "OIII 3nm", 1, hfr: 2.4);
        var report = Assert.Single(Store.PendingReports(new ReportScope(Server, Agent, TestDatabase.Profile, 37, -120, Evening)));
        Assert.Equal(2.2, report.HfrPixels!.Value, 9);
        Assert.Null(report.GuideRms);
    }

    [Fact]
    public void OldSchemasAreRefused()
    {
        using var old = new TestDatabase(version: 21);
        var error = Assert.Throws<TsException>(() => new TsStore(old.Path).Activate(Request(Tonight()), commit: false));
        Assert.Contains("schema 21", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDatabaseIsNeverCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "schedulerdb.sqlite");
        Assert.Throws<TsException>(() => new TsStore(path).Activate(Request(Tonight()), commit: true));
        Assert.False(File.Exists(path));
    }

    private void AddFrame(object targetId, DateTimeOffset saved, string filter, int grading, double seconds = 300, double? hfr = null, double? rms = null)
    {
        var metadata = new JsonObject { ["FileName"] = "frame.fits", ["FilterName"] = filter, ["ExposureDuration"] = seconds };
        if (hfr is not null) metadata["HFR"] = hfr;
        metadata["GuidingRMSArcSec"] = rms ?? 0.0;
        database.Exec("""
            INSERT INTO acquiredimage (projectId, targetId, acquireddate, filtername, gradingStatus, metadata, profileId, guid)
            SELECT projectid, Id, @p2, @p3, @p4, @p5, @p6, @p7 FROM target WHERE Id = @p1
            """, targetId, saved.ToUnixTimeSeconds(), filter, grading, metadata.ToJsonString(), TestDatabase.Profile, Guid.NewGuid().ToString("D"));
    }

    public void Dispose() => database.Dispose();
}
