using System.Data.SQLite;
using System.Text.Json;
using StarfrontCollab.Wire;
using static StarfrontCollab.TargetScheduler.Sql;

namespace StarfrontCollab.TargetScheduler;

/// Whose reports, and where the site is (night windows and Moon geometry).
public sealed record ReportScope(string Server, string AgentId, string ProfileId, double Latitude, double Longitude, DateTimeOffset Now, int Days = 14);

public sealed record ReportKey(string Task, string Night, string Panel, string Filter);

/// One night's accepted frames on one panel through one filter, measured from
/// Target Scheduler's own records. The footprint is the planned cell.
public sealed record PendingReport(
    ReportKey Key, string Project, int Frames, double Seconds, Region Footprint, string FilterName,
    double? HfrPixels, double? GuideRms, double? MoonIllumination, double? MoonSeparation)
{
    public double Exposure => Frames == 0 ? 0 : Seconds / Frames;
}

internal static class Reports
{
    private sealed record Plan(string Project, long TargetId, long ExposureMs, bool Grader, Region Footprint);

    internal static IReadOnlyList<PendingReport> Pending(SQLiteConnection db, ReportScope scope)
    {
        if (!TableExists(db, "starfront_collab_plan")) return [];
        var since = NightSky.NameOf(DateOnly.FromDateTime(scope.Now.UtcDateTime.AddDays(-scope.Days)));
        var rows = Rows(db, """
            SELECT p.remote_task, p.night, p.panel_index, p.filter, p.exposure_ms, t.Id, c.remote_project, IFNULL(pr.enablegrader, 0),
                j.ra_deg, j.dec_deg, j.width_deg, j.height_deg, j.rotation_deg
            FROM starfront_collab_plan p
            JOIN starfront_collab_target j ON j.target_guid = p.target_guid
            JOIN starfront_collab_project c ON c.project_guid = j.project_guid
            JOIN target t ON t.guid = p.target_guid
            JOIN project pr ON pr.Id = t.projectid
            WHERE c.server = @p1 AND c.agent = @p2 AND p.night >= @p3
            """, scope.Server, scope.AgentId, since);
        var plans = rows.GroupBy(
            row => new ReportKey(Text(row[0]), Text(row[1]), Long(row[2]).ToString(System.Globalization.CultureInfo.InvariantCulture), Text(row[3])),
            row => new Plan(Text(row[6]), Long(row[5]), Long(row[4]), Long(row[7]) != 0,
                new Region(Double(row[8]), Double(row[9]), Double(row[10]), Double(row[11]), Double(row[12]))));

        var ledger = TableExists(db, "starfront_collab_report");
        var pending = new List<PendingReport>();
        foreach (var group in plans)
        {
            if (!NightSky.TryParse(group.Key.Night, out var date)) continue;
            var report = Measure(db, scope, group.Key, [.. group], date);
            if (report is null) continue;
            var sent = ledger ? Scalar(db, """
                SELECT frames FROM starfront_collab_report
                WHERE server = @p1 AND agent = @p2 AND remote_task = @p3 AND night = @p4 AND panel = @p5 AND filter = @p6
                """, scope.Server, scope.AgentId, group.Key.Task, group.Key.Night, group.Key.Panel, group.Key.Filter) : null;
            // The server keeps the larger of two reports for the same key, so
            // only a night that has grown is worth sending again.
            if (sent is null || Long(sent) < report.Frames) pending.Add(report);
        }
        return pending;
    }

    private static PendingReport? Measure(SQLiteConnection db, ReportScope scope, ReportKey key, IReadOnlyList<Plan> plans, DateOnly date)
    {
        var (start, end) = NightSky.Window(date, scope.Longitude);
        var targets = plans.Select(p => p.TargetId).Distinct().ToArray();
        var marks = string.Join(", ", targets.Select((_, i) => "@p" + (i + 4)));
        var frames = Rows(db, $"""
            SELECT acquireddate, filtername, gradingStatus, metadata FROM acquiredimage
            WHERE acquireddate >= @p1 AND acquireddate < @p2 AND (profileId IS NULL OR profileId = @p3) AND targetId IN ({marks})
            ORDER BY acquireddate, Id
            """, [start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), scope.ProfileId, .. targets.Cast<object?>()]);

        var grader = plans.Any(p => p.Grader);
        var footprint = plans[0].Footprint;
        int count = 0;
        double seconds = 0, hfr = 0, rms = 0, lit = 0, apart = 0;
        bool everyHfr = true, everyRms = true;
        string? filterName = null;
        foreach (var frame in frames)
        {
            var name = Text(frame[1]);
            if (Filters.Fold(name) != key.Filter) continue;
            // With the grader on only accepted frames count; with it off, anything
            // nobody has rejected.
            var status = Long(frame[2]);
            if (grader ? status != 1 : status == 2) continue;
            var metadata = Metadata(Text(frame[3]));
            var duration = metadata.Duration ?? plans[0].ExposureMs / 1000.0;
            if (!plans.Any(p => SameExposure(duration, p.ExposureMs))) continue;

            count++;
            seconds += duration;
            filterName ??= name;
            if (metadata.Hfr is { } h) hfr += h; else everyHfr = false;
            if (metadata.GuideRms is { } g) rms += g; else everyRms = false;
            var middle = DateTimeOffset.FromUnixTimeSeconds(Long(frame[0])).AddSeconds(-duration / 2);
            var jd = Astro.JulianDay(middle);
            var moon = Astro.Moon(jd);
            lit += Astro.MoonIllumination(jd);
            apart += Astro.Separation(moon.Ra, moon.Dec, footprint.Ra, footprint.Dec);
        }
        if (count == 0) return null;
        return new(key, plans[0].Project, count, seconds, footprint, filterName ?? key.Filter,
            everyHfr ? hfr / count : null, everyRms ? rms / count : null, Math.Round(lit / count, 3), Math.Round(apart / count, 1));
    }

    internal static void Record(SQLiteConnection db, ReportScope scope, IReadOnlyList<(PendingReport Report, Recorded Verdict)> recorded)
    {
        foreach (var (report, verdict) in recorded)
            Exec(db, """
                INSERT INTO starfront_collab_report (server, agent, remote_task, night, panel, filter, frames, seconds, remote_id, accepted, summary, reported_at)
                VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
                ON CONFLICT(server, agent, remote_task, night, panel, filter) DO UPDATE SET frames = excluded.frames, seconds = excluded.seconds,
                    remote_id = excluded.remote_id, accepted = excluded.accepted, summary = excluded.summary, reported_at = excluded.reported_at
                """, scope.Server, scope.AgentId, report.Key.Task, report.Key.Night, report.Key.Panel, report.Key.Filter, report.Frames,
                report.Seconds, verdict.Id, verdict.Accepted ? 1 : 0, verdict.Summary, scope.Now.ToUnixTimeSeconds());
    }

    /// 0.1% of the sub length, at least 10 ms and at most a second: two
    /// writers rounding the same exposure differently are the same exposure.
    internal static bool SameExposure(double seconds, long expectedMs)
    {
        var tolerance = Math.Clamp(expectedMs * 0.001, 10, 1000);
        return Math.Abs(seconds * 1000 - expectedMs) <= tolerance;
    }

    private sealed record FrameMetadata(double? Duration, double? Hfr, double? GuideRms);

    /// Target Scheduler's `acquiredimage.metadata`: a flat JSON object.
    /// Unmeasured values arrive as null, zero or NaN and are treated alike.
    private static FrameMetadata Metadata(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            return new(Positive(root, "ExposureDuration"), Positive(root, "HFR"), Positive(root, "GuidingRMSArcSec"));
        }
        catch (JsonException) { return new(null, null, null); }
    }

    private static double? Positive(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && Json.AsNumber(value) is > 0 and var number ? number : null;
}
