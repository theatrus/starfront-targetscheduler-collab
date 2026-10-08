using System.Text.Json;

namespace StarfrontCollab.Wire;

/// A rectangle of sky. Degrees throughout; RA is degrees on the wire, never
/// hours. Width and height are angles on the sky.
public sealed record Region(double Ra, double Dec, double Width, double Height, double Rotation)
{
    internal static Region Read(JsonElement value, string key)
    {
        var ra = value.Number("ra");
        var dec = value.Number("dec");
        var width = value.Number("width");
        var height = value.Number("height");
        var rotation = value.OptNumber("rotation") ?? 0.0;
        if (ra is < -360 or > 720 || dec is < -90 or > 90 || width is <= 0 or > 360 || height is <= 0 or > 180)
            throw Json.Bad(key);
        return new(Astro.Normalize(ra), dec, width, height, Astro.Normalize(rotation));
    }
}

/// One of this rig's cells over a project, numbered as the server numbers them.
public sealed record Cell(int Index, int Row, int Column, Region Region);

public sealed record FilterShare(string Filter, double Exposure, double Hours);

public sealed record Requirements(
    double? MinExposure, double? MaxExposure, IReadOnlyDictionary<string, double?> Filters,
    double? MinAltitude, double? MinMoonSeparation, double? MaxMoonIllumination, double? MaxHfr, double? MaxGuideRms,
    int MinFramesPerVisit)
{
    internal static Requirements Read(JsonElement value)
    {
        var filters = new Dictionary<string, double?>(StringComparer.Ordinal);
        if (value.OptObject("filters") is { } listed)
        {
            foreach (var entry in listed.EnumerateObject().Take(64))
                filters[StarfrontCollab.Filters.Fold(entry.Name)] = entry.Value.ValueKind == JsonValueKind.Null ? null
                    : Json.AsNumber(entry.Value) ?? throw Json.Bad("filters");
        }
        return new(value.OptNumber("minExposure"), value.OptNumber("maxExposure"), filters,
            value.OptNumber("minAltitude"), value.OptNumber("minMoonSeparation"), value.OptNumber("maxMoonIllumination"),
            value.OptNumber("maxHfr"), value.OptNumber("maxGuideRms"), value.OptInt("minFramesPerVisit") ?? 10);
    }
}

/// Frames wanted tonight on one panel through one filter.
public sealed record PanelDemand(int PanelIndex, Cell Cell, string Filter, double ExposureSeconds, int Frames);

/// One task the server holds for this rig, and what it asks for tonight.
/// A share with any hold has no demands: it is shown, never scheduled.
public sealed record Share(
    string TaskId, string ProjectId, string ProjectName, int Version, string State, string Kind,
    Region Region, IReadOnlyList<Cell> Cells, IReadOnlyList<int> PanelOrder, string? AssignedNight,
    IReadOnlyList<FilterShare> Filters, IReadOnlyDictionary<string, int> VisitFrames, Requirements? Requirements,
    IReadOnlyList<string> Holds, IReadOnlyList<PanelDemand> Demands)
{
    public bool Mosaic => Kind == "mosaic" && Cells.Count > 1;
}

public sealed record Tonight(string Night, IReadOnlyList<Share> Shares);

/// Reads `GET /api/v1/agent/task` as the AstroCollab spec and Starfront's server
/// define it, and turns each task into panel demands. Tested against the spec's
/// examples and a reply captured from a live Starfront server.
public static class TonightReader
{
    private const int MaxTasks = 32;
    private const int MaxCells = 256;

    public static Tonight Read(JsonElement root, string agentId, string night)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new WireException("The server's task list is not an object.");
        if (root.OptInt("protocol") is { } protocol && protocol != 1)
            throw new WireException($"The server speaks protocol {protocol}; this plugin speaks 1.");

        var tasks = root.OptArray("tasks", MaxTasks);
        if (tasks.Length == 0 && root.OptObject("task") is { } legacy) tasks = [legacy];

        var requirements = new Dictionary<string, Requirements>(StringComparer.Ordinal);
        if (root.OptObject("requirementsByProject") is { } byProject)
            foreach (var entry in byProject.EnumerateObject().Take(MaxTasks))
                if (entry.Value.ValueKind == JsonValueKind.Object) requirements[entry.Name] = Requirements.Read(entry.Value);
        if (tasks.Length > 0 && root.OptObject("requirements") is { } first)
        {
            var project = tasks[0].Text("project", 64);
            if (!requirements.ContainsKey(project)) requirements[project] = Requirements.Read(first);
        }

        var shares = new List<Share>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task.ValueKind != JsonValueKind.Object) throw Json.Bad("tasks");
            var share = ReadShare(task, agentId, night, requirements);
            if (!seen.Add(share.TaskId)) throw new WireException($"The server listed task {share.TaskId} twice.");
            shares.Add(share);
        }
        return new(night, shares);
    }

    private static Share ReadShare(JsonElement task, string agentId, string night, IReadOnlyDictionary<string, Requirements> requirements)
    {
        var id = Identifier(task, "id");
        var project = Identifier(task, "project");
        var name = task.OptText("projectName", 120) is { Length: > 0 } called ? called : project;
        var version = task.OptInt("version") ?? 1;
        var state = task.OptText("state", 32) ?? "offered";
        var kind = task.OptText("kind", 32) ?? "mosaic";
        var region = Region.Read(task.OptObject("region") ?? throw Json.Bad("region"), "region");

        var cells = new List<Cell>();
        var places = new HashSet<(int, int)>();
        foreach (var value in task.OptArray("cells", MaxCells))
        {
            var row = value.OptInt("row") ?? 0;
            var column = value.OptInt("column") ?? 0;
            if (!places.Add((row, column))) throw new WireException($"Task {id} lists cell ({row}, {column}) twice.");
            cells.Add(new(cells.Count, row, column, Region.Read(value, "cells")));
        }
        var order = task.OptArray("share", MaxCells).Select(v => Json.AsNumber(v) is { } n && Json.AsInt(n) is { } i ? i : throw Json.Bad("share")).ToArray();

        var filters = task.OptArray("filters", 32).Select(v => new FilterShare(
            Filters.Fold(v.Text("filter", 64)), v.OptNumber("exposure") ?? 0.0, v.OptNumber("hours") ?? 0.0)).ToArray();

        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        if (task.OptObject("visit") is { } visit && visit.OptObject("frames") is { } wanted)
        {
            foreach (var entry in wanted.EnumerateObject().Take(32))
            {
                var count = Json.AsNumber(entry.Value) is { } n && Json.AsInt(n) is { } i && i >= 0 ? i : throw Json.Bad("visit");
                if (count > 0) frames[Filters.Fold(entry.Name)] = count;
            }
        }
        var assigned = task.OptText("assignedNight", 16);
        requirements.TryGetValue(project, out var wants);

        var holds = new List<string>();
        if (task.OptText("agent", 64) is { Length: > 0 } owner && owner != agentId) holds.Add("belongs to another telescope");
        if (state != "accepted") holds.Add(state == "offered" ? "offered: accept it to schedule it" : state);
        if (kind is not ("single" or "mosaic")) holds.Add($"unknown project kind '{kind}'");
        if (assigned is null) holds.Add("not dealt for a night yet");
        else if (assigned != night) holds.Add($"dealt for {assigned}, not {night}");
        if (order.Length == 0) holds.Add("nothing dealt tonight");
        if (frames.Count == 0) holds.Add("no visit tonight");
        if (wants is null) holds.Add("the project's rules were not sent");
        if (kind == "mosaic" && order.Any(i => i < 0 || i >= cells.Count)) holds.Add("a dealt panel is not one of this rig's cells");
        if (kind == "single" && order.Any(i => i < 0 || i >= Math.Max(1, cells.Count))) holds.Add("a dealt panel is not this rig's frame");

        var exposures = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (filter, count) in frames)
        {
            var exposure = filters.FirstOrDefault(f => f.Filter == filter)?.Exposure ?? 0.0;
            if (exposure <= 0 || exposure > 86_400) { holds.Add($"no sub length for {filter}"); continue; }
            exposures[filter] = exposure;
            if (wants is null) continue;
            if (count < wants.MinFramesPerVisit) holds.Add($"{count} {filter} frames is under the project's {wants.MinFramesPerVisit} per visit");
            if (wants.MinExposure is { } low && exposure < low) holds.Add($"{filter} at {exposure:g}s is under the project's {low:g}s");
            if (wants.MaxExposure is { } high && exposure > high) holds.Add($"{filter} at {exposure:g}s is over the project's {high:g}s");
            if (wants.Filters.Count > 0 && !wants.Filters.ContainsKey(filter)) holds.Add($"{filter} is not a filter this project wants");
        }

        var demands = new List<PanelDemand>();
        if (holds.Count == 0)
        {
            foreach (var index in order)
            {
                // A single-target project is one frame centred on the object:
                // the rig's own cell when it has one, the region when it does not.
                var cell = kind == "single" && cells.Count == 0 ? new Cell(0, 0, 0, region) : cells[index];
                foreach (var (filter, count) in frames)
                    demands.Add(new(index, cell, filter, exposures[filter], count));
            }
        }
        return new(id, project, name, version, state, kind, region, cells, order, assigned, filters, frames, wants, holds, demands);
    }

    private static string Identifier(JsonElement value, string key)
    {
        var text = value.Text(key, 64);
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw Json.Bad(key);
        return text;
    }
}
