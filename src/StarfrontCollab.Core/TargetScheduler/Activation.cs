using System.Data.SQLite;
using System.Globalization;
using StarfrontCollab.Wire;
using static StarfrontCollab.TargetScheduler.Sql;

namespace StarfrontCollab.TargetScheduler;

/// One pass of "make Target Scheduler shoot tonight's deal". The plugin owns
/// the enable flag and goal of the exposure plans it created; it never edits
/// or deletes anything else, and never deletes its own rows either.
internal sealed class Activation(SQLiteConnection db, ActivationRequest request)
{
    // Target Scheduler's default scoring weights for a new project.
    private static readonly (string Name, double Weight)[] RuleWeights =
    [
        ("Meridian Flip Penalty", 0), ("Meridian Window Priority", 75), ("Mosaic Completion", 0), ("Percent Complete", 50),
        ("Project Priority", 50), ("Setting Soonest", 50), ("Smart Exposure Order", 0), ("Target Switch Penalty", 67),
    ];

    private sealed record ProjectRow(long Id, string Guid, string Name, bool Grader);
    private sealed record TargetRow(long Id, string Guid, string Name);

    private readonly List<string> changes = [];
    private readonly List<string> holds = [];
    private readonly HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> missingFilters = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Filter, long ExposureMs), long?> templates = [];
    private int targets;
    private int plans;

    private long Now => request.Now.ToUnixTimeSeconds();
    private string Night => request.Tonight.Night;

    internal void Run()
    {
        Schema.EnsureSideTables(db);
        foreach (var share in request.Tonight.Shares)
        {
            if (share.Holds.Count > 0) holds.Add($"{share.ProjectName}: {string.Join("; ", share.Holds)}");
            else Activate(share);
        }
        PauseUndealt();
    }

    internal ActivationResult Result(bool committed) => new(committed, changes, holds, targets, plans);

    private void Activate(Share share)
    {
        // Settle the filters first: a share none of whose filters this rig can
        // shoot leaves Target Scheduler untouched rather than an empty project.
        var usable = new Dictionary<(string, double), long>();
        foreach (var demand in share.Demands)
        {
            if (Template(demand.Filter, demand.ExposureSeconds) is { } template) usable[(demand.Filter, demand.ExposureSeconds)] = template;
            else if (missingFilters.Add(demand.Filter))
                holds.Add($"{share.ProjectName}: no filter on this wheel and no exposure template at {request.Binning}x{request.Binning} is {demand.Filter}");
        }
        if (usable.Count == 0) return;

        var project = EnsureProject(share);
        foreach (var panel in share.Demands.GroupBy(d => d.PanelIndex))
        {
            var target = EnsureTarget(project, share, panel.First().Cell);
            targets++;
            foreach (var demand in panel)
            {
                if (!usable.TryGetValue((demand.Filter, demand.ExposureSeconds), out var template)) continue;
                EnsurePlan(project, target, share, demand, template);
                plans++;
            }
        }
    }

    private ProjectRow EnsureProject(Share share)
    {
        var floor = share.Requirements?.MinAltitude;
        var found = Rows(db, """
            SELECT p.Id, p.guid, p.name, IFNULL(p.enablegrader, 0), IFNULL(p.minimumaltitude, 0)
            FROM starfront_collab_project c JOIN project p ON p.guid = c.project_guid
            WHERE c.server = @p1 AND c.agent = @p2 AND c.remote_project = @p3 AND p.profileId = @p4
            ORDER BY p.Id DESC LIMIT 1
            """, request.Server, request.AgentId, share.ProjectId, request.ProfileId);
        if (found.Count > 0)
        {
            var row = found[0];
            var project = new ProjectRow(Long(row[0]), Text(row[1]), Text(row[2]), Long(row[3]) != 0);
            // Only ever raised: a floor the coordinator set is the least the frames
            // need, and a higher one somebody chose locally is theirs to keep.
            if (floor is { } minimum && minimum > Double(row[4]))
            {
                Exec(db, "UPDATE project SET minimumaltitude = @p2 WHERE Id = @p1", project.Id, minimum);
                changes.Add($"Raised {project.Name}'s minimum altitude to {minimum:0.#}°, the collaboration's floor");
            }
            return project;
        }

        // The project this rig was linked to has gone (deleted in Target
        // Scheduler, or another profile); start a fresh one.
        Exec(db, "DELETE FROM starfront_collab_project WHERE server = @p1 AND agent = @p2 AND remote_project = @p3",
            request.Server, request.AgentId, share.ProjectId);
        var guid = NewGuid();
        // The grader starts off: Target Scheduler holds grading until most of
        // a plan is shot, which would keep frames from the server for nights.
        // The server judges each report itself; turn the grader on to filter first.
        Exec(db, """
            INSERT INTO project (profileId, name, description, state, priority, createdate, activedate, inactivedate, minimumtime,
                minimumaltitude, maximumAltitude, usecustomhorizon, horizonoffset, meridianwindow, filterswitchfrequency, ditherevery,
                enablegrader, isMosaic, flatsHandling, smartexposureorder, guid)
            VALUES (@p1, @p2, @p3, 1, 1, @p4, @p4, NULL, 30, @p5, 0, 0, 0, 0, 0, 0, 0, @p6, 0, 0, @p7)
            """, request.ProfileId, share.ProjectName, $"Collaboration project {share.ProjectId} on {request.Server}, kept in step by Starfront TargetScheduler Collab.",
            Now, floor ?? 0.0, share.Mosaic ? 1 : 0, guid);
        var id = LastId(db);
        foreach (var (name, weight) in RuleWeights)
            Exec(db, "INSERT INTO ruleweight (name, weight, projectid) VALUES (@p1, @p2, @p3)", name, weight, id);
        Exec(db, "INSERT INTO starfront_collab_project (project_guid, server, agent, remote_project) VALUES (@p1, @p2, @p3, @p4)",
            guid, request.Server, request.AgentId, share.ProjectId);
        changes.Add($"Created Target Scheduler project {share.ProjectName}");
        return new(id, guid, share.ProjectName, false);
    }

    private TargetRow EnsureTarget(ProjectRow project, Share share, Cell cell)
    {
        var sky = cell.Region;
        var name = share.Mosaic ? $"{share.ProjectName} panel {cell.Index}" : share.ProjectName;
        var found = Rows(db, """
            SELECT j.target_guid, t.Id, t.name, j.ra_deg, j.dec_deg, j.width_deg, j.height_deg, j.rotation_deg
            FROM starfront_collab_target j JOIN target t ON t.guid = j.target_guid
            WHERE j.project_guid = @p1 AND j.cell_row = @p2 AND j.cell_column = @p3 AND j.retired = 0
            ORDER BY t.Id DESC LIMIT 1
            """, project.Guid, cell.Row, cell.Column);
        if (found.Count > 0)
        {
            var row = found[0];
            var current = new TargetRow(Long(row[1]), Text(row[0]), Text(row[2]));
            var moved = Astro.Separation(Double(row[3]), Double(row[4]), sky.Ra, sky.Dec);
            var framed = Long(Scalar(db, "SELECT COUNT(*) FROM acquiredimage WHERE targetId = @p1", current.Id)) > 0;
            // A cell that moved a long way is different sky. Frames already on
            // the old target stay with the old target.
            if (moved <= 0.25 * Math.Min(sky.Width, sky.Height) || !framed)
            {
                var same = moved < 1e-7 && Math.Abs(Double(row[5]) - sky.Width) < 1e-9 && Math.Abs(Double(row[6]) - sky.Height) < 1e-9
                    && Math.Abs(Double(row[7]) - sky.Rotation) < 1e-9;
                if (!same)
                {
                    Exec(db, "UPDATE target SET ra = @p2, dec = @p3, rotation = @p4, epochcode = 2 WHERE Id = @p1",
                        current.Id, sky.Ra / 15.0, sky.Dec, sky.Rotation);
                    Exec(db, """
                        UPDATE starfront_collab_target SET ra_deg = @p2, dec_deg = @p3, width_deg = @p4, height_deg = @p5, rotation_deg = @p6
                        WHERE target_guid = @p1
                        """, current.Guid, sky.Ra, sky.Dec, sky.Width, sky.Height, sky.Rotation);
                    if (moved > 1.0 / 60) changes.Add($"Moved {current.Name} {moved * 60:0.#}′ to the server's cell");
                }
                return current;
            }
            Exec(db, "UPDATE target SET active = 0 WHERE Id = @p1", current.Id);
            Exec(db, "UPDATE starfront_collab_target SET retired = 1 WHERE target_guid = @p1", current.Guid);
            changes.Add($"Retired {current.Name}: its cell moved {moved:0.##}° and it already has frames");
        }

        var guid = NewGuid();
        Exec(db, "INSERT INTO target (name, active, ra, dec, epochcode, rotation, roi, projectid, guid) VALUES (@p1, 1, @p2, @p3, 2, @p4, 100, @p5, @p6)",
            name, sky.Ra / 15.0, sky.Dec, sky.Rotation, project.Id, guid);
        var id = LastId(db);
        Exec(db, """
            INSERT INTO starfront_collab_target (target_guid, project_guid, cell_row, cell_column, ra_deg, dec_deg, width_deg, height_deg, rotation_deg)
            VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
            """, guid, project.Guid, cell.Row, cell.Column, sky.Ra, sky.Dec, sky.Width, sky.Height, sky.Rotation);
        changes.Add($"Added target {name} at RA {Hours(sky.Ra / 15.0)}, Dec {Degrees(sky.Dec)}");
        return new(id, guid, name);
    }

    private long? Template(string filter, double exposure)
    {
        var key = (filter, (long)Math.Round(exposure * 1000));
        if (templates.TryGetValue(key, out var cached)) return cached;
        var wheel = request.WheelFilters;
        var best = TemplateChoice.Best(TemplateChoice.Read(db, request.ProfileId, request.Binning), filter, wheel);
        long? id = best?.Id;
        if (id is null && wheel.FirstOrDefault(name => Filters.Fold(name) == filter) is { } filterName)
        {
            var name = $"{filterName} {request.Binning}x{request.Binning}";
            Exec(db, """
                INSERT INTO exposuretemplate (profileId, name, filtername, gain, offset, bin, readoutmode, twilightlevel, moonavoidanceenabled,
                    moonavoidanceseparation, moonavoidancewidth, maximumhumidity, defaultexposure, moonrelaxscale, moonrelaxmaxaltitude,
                    moonrelaxminaltitude, moondownenabled, ditherevery, minutesOffset, guid)
                VALUES (@p1, @p2, @p3, -1, -1, @p4, -1, 0, 0, 60, 7, 0, @p5, 0, 5, -15, 0, -1, 0, @p6)
                """, request.ProfileId, name, filterName, request.Binning, exposure, NewGuid());
            id = LastId(db);
            changes.Add($"Added exposure template {name}");
        }
        templates[key] = id;
        return id;
    }

    private void EnsurePlan(ProjectRow project, TargetRow target, Share share, PanelDemand demand, long template)
    {
        var exposureMs = (long)Math.Round(demand.ExposureSeconds * 1000);
        var label = $"{target.Name} {demand.Filter}";
        var found = Rows(db, """
            SELECT e.guid, IFNULL(e.acquired, 0), IFNULL(e.accepted, 0), IFNULL(e.desired, 0), IFNULL(e.exposure, 0), IFNULL(e.exposureTemplateId, 0),
                IFNULL(e.enabled, 1), IFNULL(e.targetid, 0)
            FROM starfront_collab_plan p JOIN exposureplan e ON e.guid = p.exposureplan_guid
            WHERE p.target_guid = @p1 AND p.filter = @p2
            ORDER BY p.applied_at DESC, p.rowid DESC LIMIT 1
            """, target.Guid, demand.Filter);
        string guid;
        int baseline;
        if (found.Count > 0)
        {
            var row = found[0];
            guid = Text(row[0]);
            // Tonight's goal sits on top of what the plan held when tonight was
            // first dealt, so frames from earlier nights are not shot again and
            // frames already taken tonight are not asked for twice.
            var held = Scalar(db, "SELECT baseline FROM starfront_collab_plan WHERE exposureplan_guid = @p1 AND night = @p2", guid, Night);
            baseline = held is null ? (int)Long(project.Grader ? row[2] : row[1]) : (int)Long(held);
            var desired = baseline + demand.Frames;
            var unchanged = Long(row[3]) == desired && Math.Abs(Double(row[4]) - demand.ExposureSeconds) < 0.0005
                && Long(row[5]) == template && Long(row[6]) != 0 && Long(row[7]) == target.Id;
            if (!unchanged)
            {
                Exec(db, "UPDATE exposureplan SET exposure = @p2, desired = @p3, exposureTemplateId = @p4, enabled = 1, targetid = @p5 WHERE guid = @p1",
                    guid, demand.ExposureSeconds, desired, template, target.Id);
                changes.Add(Long(row[6]) == 0
                    ? $"Resumed {label}: {demand.Frames} × {Seconds(demand.ExposureSeconds)} tonight"
                    : $"Set {label} to {demand.Frames} × {Seconds(demand.ExposureSeconds)} tonight (goal {Long(row[3])} → {desired})");
            }
        }
        else
        {
            guid = NewGuid();
            baseline = 0;
            Exec(db, """
                INSERT INTO exposureplan (profileId, exposure, desired, acquired, accepted, targetid, exposureTemplateId, enabled, guid)
                VALUES (@p1, @p2, @p3, 0, 0, @p4, @p5, 1, @p6)
                """, request.ProfileId, demand.ExposureSeconds, demand.Frames, target.Id, template, guid);
            changes.Add($"Planned {label}: {demand.Frames} × {Seconds(demand.ExposureSeconds)}");
        }

        var recorded = Rows(db, """
            SELECT remote_task, panel_index, exposure_ms, requested FROM starfront_collab_plan WHERE exposureplan_guid = @p1 AND night = @p2
            """, guid, Night);
        var current = recorded.Count > 0 && Text(recorded[0][0]) == share.TaskId && Long(recorded[0][1]) == demand.PanelIndex
            && Long(recorded[0][2]) == exposureMs && Long(recorded[0][3]) == demand.Frames;
        if (!current)
            Exec(db, """
                INSERT INTO starfront_collab_plan (exposureplan_guid, night, target_guid, remote_task, panel_index, filter, exposure_ms, baseline, requested, applied_at)
                VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)
                ON CONFLICT(exposureplan_guid, night) DO UPDATE SET target_guid = excluded.target_guid, remote_task = excluded.remote_task,
                    panel_index = excluded.panel_index, filter = excluded.filter, exposure_ms = excluded.exposure_ms,
                    requested = excluded.requested, applied_at = excluded.applied_at
                """, guid, Night, target.Guid, share.TaskId, demand.PanelIndex, demand.Filter, exposureMs, baseline, demand.Frames, Now);
        touched.Add(guid);
    }

    /// Plans this rig created that tonight's deal does not ask for are paused,
    /// so Target Scheduler does not keep shooting a panel the server has given
    /// to somebody else or a share that is held.
    private void PauseUndealt()
    {
        var enabled = Rows(db, """
            SELECT e.guid, t.name, p.filter
            FROM starfront_collab_plan p
            JOIN starfront_collab_target j ON j.target_guid = p.target_guid
            JOIN starfront_collab_project c ON c.project_guid = j.project_guid
            JOIN exposureplan e ON e.guid = p.exposureplan_guid
            JOIN target t ON t.Id = e.targetid
            WHERE c.server = @p1 AND c.agent = @p2 AND IFNULL(e.enabled, 1) <> 0
            """, request.Server, request.AgentId);
        var paused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in enabled)
        {
            var guid = Text(row[0]);
            if (touched.Contains(guid) || !paused.Add(guid)) continue;
            Exec(db, "UPDATE exposureplan SET enabled = 0 WHERE guid = @p1", guid);
            changes.Add($"Paused {Text(row[1])} {Text(row[2])}: not dealt for {Night}");
        }
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private static string Hours(double hours)
    {
        var total = (int)Math.Round(hours * 3600);
        return $"{total / 3600:00}h{total / 60 % 60:00}m{total % 60:00}s";
    }

    private static string Degrees(double degrees)
    {
        var total = (int)Math.Round(Math.Abs(degrees) * 3600);
        return $"{(degrees < 0 ? '-' : '+')}{total / 3600:00}°{total / 60 % 60:00}′{total % 60:00}″";
    }
}
