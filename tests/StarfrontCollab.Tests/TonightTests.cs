using System.Text.Json;
using System.Text.Json.Nodes;
using StarfrontCollab.Wire;
using Xunit;

namespace StarfrontCollab.Tests;

public sealed class TonightTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static Tonight Read(string json, string agent = "000000000001", string night = "2026-10-05")
    {
        using var document = JsonDocument.Parse(json);
        return TonightReader.Read(document.RootElement, agent, night);
    }

    [Fact]
    public void SpecExampleYieldsSixOiiiPanelsOfElevenFiveMinuteFrames()
    {
        var share = Assert.Single(Read(Fixture("starfront-tonight.json")).Shares);
        Assert.Empty(share.Holds);
        Assert.Equal("M31 halo in narrowband", share.ProjectName);
        Assert.True(share.Mosaic);
        Assert.Equal(6, share.Demands.Count);
        Assert.All(share.Demands, d => Assert.Equal(("O", 11, 300.0), (d.Filter, d.Frames, d.ExposureSeconds)));
        Assert.Equal([0, 1, 2, 3, 4, 5], share.Demands.Select(d => d.PanelIndex));
        Assert.Equal(30.0, share.Requirements!.MinAltitude);
    }

    [Fact]
    public void LiveStarfrontReplyYieldsOneFrameInFiveFilters()
    {
        // Captured from a Starfront 0.2.29 server: a single-target test project.
        var share = Assert.Single(Read(Fixture("starfront-live-tonight.json"), "3e74cd845054", "2026-10-07").Shares);
        Assert.Empty(share.Holds);
        Assert.Equal(("M31 NINA TEST COLLAB", "single"), (share.ProjectName, share.Kind));
        Assert.False(share.Mosaic);
        Assert.Equal(["H", "O", "R", "G", "B"], share.Demands.Select(d => d.Filter));
        Assert.All(share.Demands, d => Assert.Equal((0, 2151, 2.0), (d.PanelIndex, d.Frames, d.ExposureSeconds)));
        Assert.Equal(275.846, share.Demands[0].Cell.Region.Rotation, 6);
    }

    [Fact]
    public void CellsKeepTheServersNumberingAndDegrees()
    {
        var share = Read(Fixture("starfront-tonight.json")).Shares[0];
        var third = share.Demands[2].Cell;
        Assert.Equal((2, 0, 2), (third.Index, third.Row, third.Column));
        Assert.Equal(13.720405230187254, third.Region.Ra, 9);
        Assert.Equal(39.769, third.Region.Dec, 9);
        Assert.Equal(35.0, third.Region.Rotation, 9);
    }

    [Fact]
    public void AnotherNightsDealIsHeld()
    {
        var share = Read(Fixture("starfront-tonight.json"), night: "2026-10-06").Shares[0];
        Assert.Contains("dealt for 2026-10-05, not 2026-10-06", share.Holds);
        Assert.Empty(share.Demands);
    }

    [Fact]
    public void AnOfferedTaskWaitsToBeAccepted()
    {
        var share = Read(Edit(t => t["state"] = "offered")).Shares[0];
        Assert.Contains("offered: accept it to schedule it", share.Holds);
        Assert.Empty(share.Demands);
    }

    [Fact]
    public void AnotherTelescopesTaskIsHeld() =>
        Assert.Contains("belongs to another telescope", Read(Fixture("starfront-tonight.json"), agent: "000000000009").Shares[0].Holds);

    [Fact]
    public void ProjectRulesHoldAVisitTheyWouldReject()
    {
        var share = Read(Edit(t => t["visit"]!["frames"]!["O"] = 4)).Shares[0];
        Assert.Contains("4 O frames is under the project's 10 per visit", share.Holds);
        var exposure = Read(Edit(t => t["filters"]![1]!["exposure"] = 900)).Shares[0];
        Assert.Contains("O at 900s is over the project's 600s", exposure.Holds);
    }

    [Fact]
    public void StarfrontColourCameraAndSkyRulesAreRetained()
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("requirementsByProject");
        root["requirements"]!["acceptOscRgb"] = false;
        root["requirements"]!["acceptOscNarrowband"] = true;
        root["requirements"]!["maxBortle"] = 5;
        var rules = Read(root.ToJsonString()).Shares[0].Requirements!;
        Assert.False(rules.AcceptOscRgb);
        Assert.True(rules.AcceptOscNarrowband);
        Assert.Equal(5, rules.MaxBortle);
    }

    [Theory]
    [InlineData("osc-rgb", "OSC-RGB", "acceptOscRgb")]
    [InlineData("osc-ha+oiii", "OSC-Ha+OIII", "acceptOscNarrowband")]
    [InlineData("OSC-Ha", "OSC-Ha", "acceptColour")]
    [InlineData("OSC-Ha", "OSC-Ha", "acceptOscRgb")]
    public void StarfrontDisabledColourBandsAreHeld(string spelling, string band, string flag)
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("requirementsByProject");
        root.Remove("task");
        var task = root["tasks"]![0]!;
        task["filters"] = new JsonArray(new JsonObject { ["filter"] = spelling, ["exposure"] = 300 });
        task["visit"] = new JsonObject { ["frames"] = new JsonObject { [spelling] = 11 } };
        root["requirements"]!["filters"] = new JsonObject { [spelling] = 7 };
        Assert.Empty(Read(root.ToJsonString()).Shares[0].Holds);
        root["requirements"]![flag] = false;
        var share = Read(root.ToJsonString()).Shares[0];
        Assert.Contains($"{band} is disabled by this project's colour-camera rules", share.Holds);
        Assert.Empty(share.Demands);
    }

    [Theory]
    [InlineData("L", "OSC-RGB")]
    [InlineData("L-eXtreme", "OSC-Ha+OIII")]
    [InlineData("Dual", "Ha+OIII")]
    public void PhysicalTaskFiltersKeepTheirDeliveredDataBand(string physical, string band)
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("task");
        root.Remove("requirementsByProject");
        var task = root["tasks"]![0]!;
        task["filters"] = new JsonArray(new JsonObject
        {
            ["filter"] = physical,
            ["exposure"] = 300,
            ["channels"] = new JsonArray(band)
        });
        task["visit"] = new JsonObject { ["frames"] = new JsonObject { [physical] = 11 } };
        root["requirements"]!["filters"] = new JsonObject { [band] = 7 };
        var share = Read(root.ToJsonString()).Shares[0];
        Assert.Empty(share.Holds);
        Assert.All(share.Demands, d => Assert.Equal(physical, d.Filter));
        Assert.Equal([band], Assert.Single(share.Filters).Channels);
        if (band.StartsWith("OSC-", StringComparison.Ordinal))
        {
            root["requirements"]!["acceptColour"] = false;
            Assert.Empty(Read(root.ToJsonString()).Shares[0].Demands);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void InvalidBortleLimitsAreRefused(int value)
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("requirementsByProject");
        root["requirements"]!["maxBortle"] = value;
        Assert.Throws<WireException>(() => Read(root.ToJsonString()));
    }

    [Fact]
    public void LegacySingleTaskRepliesAreRead()
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("tasks");
        Assert.Equal(6, Read(root.ToJsonString()).Shares[0].Demands.Count);
    }

    [Fact]
    public void ASinglesTargetIsOneFrameOnTheObject()
    {
        var share = Read(Edit(t =>
        {
            t["kind"] = "single";
            t["cells"] = new JsonArray();
            t["share"] = new JsonArray(0);
        })).Shares[0];
        var demand = Assert.Single(share.Demands);
        Assert.False(share.Mosaic);
        Assert.Equal(10.6847, demand.Cell.Region.Ra, 9);
    }

    [Fact]
    public void RepeatedFieldsAreRefused()
    {
        var json = Fixture("starfront-tonight.json").Replace("\"version\": 2,", "\"version\": 2, \"version\": 3,");
        Assert.Throws<WireException>(() => Json.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Dispose());
    }

    private static string Edit(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(Fixture("starfront-tonight.json"))!.AsObject();
        root.Remove("task");
        change(root["tasks"]![0]!.AsObject());
        return root.ToJsonString();
    }
}
