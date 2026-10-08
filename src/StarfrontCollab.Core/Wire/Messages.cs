using System.Text.Json.Nodes;

namespace StarfrontCollab.Wire;

/// What the server needs to know to decide whether this rig can help. The
/// server keeps the last profile it was sent, so every hello sends all of it.
public sealed record RigProfile(
    string Name, double? FocalLength, double? PixelSize, int? SensorWidth, int? SensorHeight, int Binning, bool Colour,
    double? Rotation, double? HoursPerNight, IReadOnlyList<(string Name, double? Bandpass)> Filters,
    IReadOnlyDictionary<string, double> Exposures)
{
    /// Arcseconds per binned pixel.
    public double? Scale => FocalLength is > 0 && PixelSize is > 0 ? 206.265 * PixelSize.Value * Math.Max(1, Binning) / FocalLength.Value : null;

    public JsonObject ToJson()
    {
        var filters = new JsonObject();
        foreach (var (name, bandpass) in Filters) filters[name] = bandpass;
        var exposures = new JsonObject();
        foreach (var (name, seconds) in Exposures) exposures[name] = seconds;
        return new JsonObject
        {
            ["name"] = Name,
            ["focalLength"] = FocalLength,
            ["pixelSize"] = PixelSize,
            ["sensorWidth"] = SensorWidth,
            ["sensorHeight"] = SensorHeight,
            ["binning"] = Binning,
            ["colour"] = Colour,
            // Null means a rotator can turn the camera wherever a project asks.
            ["rotation"] = Rotation,
            ["hoursPerNight"] = HoursPerNight,
            ["filters"] = filters,
            ["exposures"] = exposures,
        };
    }
}

/// Where the telescope points and what it is doing, for the other rigs'
/// charts. RA is hours here, unlike regions. Sent fresh; never replayed.
public sealed record Presence(double? RaHours, double? Dec, string State, bool Slewing, string? Target, string? Project, string? Telescope)
{
    public JsonObject ToJson()
    {
        var body = new JsonObject { ["state"] = State, ["slewing"] = Slewing };
        if (RaHours is { } ra && Dec is { } dec)
        {
            body["ra"] = Math.Round(((ra % 24) + 24) % 24, 5);
            body["dec"] = Math.Round(Math.Clamp(dec, -90, 90), 4);
        }
        if (!string.IsNullOrWhiteSpace(Target)) body["target"] = Truncate(Target, 80);
        if (!string.IsNullOrWhiteSpace(Project)) body["project"] = Project;
        if (!string.IsNullOrWhiteSpace(Telescope)) body["telescope"] = Truncate(Telescope, 60);
        return body;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}

/// One night's frames on one panel through one filter. Unknown measurements
/// are sent as null, never guessed.
public sealed record Contribution(
    string Project, string Task, string Night, string Filter, string Panel, int Frames, double Seconds, double Exposure,
    Region Footprint, double? Scale, double? FocalLength, double? Hfr, double? GuideRms,
    double? MoonIllumination, double? MoonSeparation, double? Bandpass, bool Colour)
{
    public JsonObject ToJson() => new()
    {
        ["project"] = Project,
        ["task"] = Task,
        ["night"] = Night,
        ["filterName"] = Filter,
        ["panel"] = Panel,
        ["frames"] = Frames,
        ["seconds"] = Math.Round(Seconds, 3),
        ["exposure"] = Math.Round(Exposure, 3),
        ["footprint"] = new JsonObject
        {
            ["ra"] = Footprint.Ra,
            ["dec"] = Footprint.Dec,
            ["width"] = Footprint.Width,
            ["height"] = Footprint.Height,
            ["rotation"] = Footprint.Rotation,
        },
        ["scale"] = Scale,
        ["focalLength"] = FocalLength,
        ["hfr"] = Hfr,
        ["guideRms"] = GuideRms,
        ["moonIllumination"] = MoonIllumination,
        ["moonSeparation"] = MoonSeparation,
        ["calibrated"] = false,
        ["bandpass"] = Bandpass,
        ["colour"] = Colour,
    };
}

public sealed record Health(int Protocol, string Version, bool SignIn);
public sealed record LoginStart(string Code, Uri Url, TimeSpan Lifetime);
public sealed record LoginPoll(string State, string? Token)
{
    public override string ToString() => "Sign-in state: " + State;
}

public sealed record Enrollment(string AgentId, string Token)
{
    public override string ToString() => "Enrollment for " + AgentId + " (token redacted)";
}

public sealed record HelloReply(string AgentId, string Name, double? ServerTime);

public sealed record OpenProject(string Id, string Name, string Kind, bool Joined, bool? Compatible, string Compatibility,
    string Coordinator, int Participants, int Online, IReadOnlyDictionary<string, double> Goals, IReadOnlyDictionary<string, double> Collected);

public sealed record Recorded(string Id, bool Accepted, bool Duplicate, string Summary);
