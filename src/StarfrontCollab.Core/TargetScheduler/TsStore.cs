using System.Data.SQLite;
using StarfrontCollab.Wire;
using static StarfrontCollab.TargetScheduler.Sql;

namespace StarfrontCollab.TargetScheduler;

public sealed class TsException(string message) : Exception(message);

public sealed record ActivationRequest(
    string Server, string AgentId, string ProfileId, Tonight Tonight, IReadOnlyList<string> WheelFilters, int Binning, DateTimeOffset Now);

public sealed record ActivationResult(bool Committed, IReadOnlyList<string> Changes, IReadOnlyList<string> Holds, int Targets, int Plans);

/// The Target Scheduler database, as this plugin uses it. Rows it creates are
/// ordinary Target Scheduler rows; which of them it owns is recorded in its own
/// `starfront_collab_*` tables beside them, so ownership survives a copy or a
/// restore of the database and Target Scheduler never needs to know.
public sealed class TsStore(string path)
{
    /// The lowest schema with GUID columns and exposure-plan enable flags.
    public const int MinimumSchema = 22;

    public string Path { get; } = path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "SchedulerPlugin", "schedulerdb.sqlite");

    /// Bring Target Scheduler in step with tonight's deal. Always runs in one
    /// transaction; a preview is the same run rolled back, so what a preview
    /// lists is exactly what Apply would do.
    public ActivationResult Activate(ActivationRequest request, bool commit)
    {
        using var db = Open(readOnly: false);
        using var transaction = db.BeginTransaction();
        var activation = new Activation(db, request);
        activation.Run();
        if (commit) transaction.Commit();
        else transaction.Rollback();
        return activation.Result(commit);
    }

    /// The sub length the server should deal each wheel filter at: the default
    /// exposure of the template Target Scheduler would use for it, at the rig's
    /// binning. Read only.
    public IReadOnlyDictionary<string, double> DefaultExposures(string profileId, int binning, IReadOnlyList<string> wheelFilters)
    {
        using var db = Open(readOnly: true);
        var templates = TemplateChoice.Read(db, profileId, binning);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var filter in wheelFilters)
        {
            var match = TemplateChoice.Best(templates, Filters.Fold(filter), wheelFilters, preferred: filter);
            if (match is not null && match.DefaultExposure is > 0 and <= 86_400) result[filter] = match.DefaultExposure;
        }
        return result;
    }

    /// Target Scheduler target names this plugin owns, and the remote project
    /// each belongs to, for naming the project in live presence.
    public IReadOnlyDictionary<string, string> TargetProjects(string server, string agent)
    {
        using var db = Open(readOnly: true);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(db, "starfront_collab_target")) return result;
        foreach (var row in Rows(db, """
            SELECT t.name, c.remote_project FROM starfront_collab_target j
            JOIN starfront_collab_project c ON c.project_guid = j.project_guid
            JOIN target t ON t.guid = j.target_guid
            WHERE c.server = @p1 AND c.agent = @p2 AND j.retired = 0
            """, server, agent))
            result[Text(row[0])] = Text(row[1]);
        return result;
    }

    public IReadOnlyList<PendingReport> PendingReports(ReportScope scope)
    {
        using var db = Open(readOnly: true);
        return Reports.Pending(db, scope);
    }

    public void RecordReports(ReportScope scope, IReadOnlyList<(PendingReport Report, Recorded Verdict)> recorded)
    {
        using var db = Open(readOnly: false);
        using var transaction = db.BeginTransaction();
        Schema.EnsureSideTables(db);
        Reports.Record(db, scope, recorded);
        transaction.Commit();
    }

    internal SQLiteConnection Open(bool readOnly)
    {
        if (!File.Exists(Path))
            throw new TsException($"There is no Target Scheduler database at {Path}. Open Target Scheduler in N.I.N.A. once to create it.");
        var builder = new SQLiteConnectionStringBuilder
        {
            DataSource = Path,
            FailIfMissing = true,
            Pooling = false,
            ReadOnly = readOnly,
            DefaultTimeout = 15,
            Version = 3,
        };
        var db = new SQLiteConnection(builder.ConnectionString);
        try
        {
            db.Open();
            Exec(db, "PRAGMA busy_timeout = 15000");
            var version = Long(Scalar(db, "PRAGMA user_version"));
            if (version < MinimumSchema)
                throw new TsException($"The Target Scheduler database is at schema {version}; this plugin needs {MinimumSchema} or later. Update Target Scheduler and open it once.");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}

internal static class Schema
{
    internal static void EnsureSideTables(SQLiteConnection db) => Exec(db, """
        CREATE TABLE IF NOT EXISTS starfront_collab_project (
            project_guid TEXT NOT NULL PRIMARY KEY,
            server TEXT NOT NULL,
            agent TEXT NOT NULL,
            remote_project TEXT NOT NULL,
            UNIQUE(server, agent, remote_project));
        CREATE TABLE IF NOT EXISTS starfront_collab_target (
            target_guid TEXT NOT NULL PRIMARY KEY,
            project_guid TEXT NOT NULL,
            cell_row INTEGER NOT NULL,
            cell_column INTEGER NOT NULL,
            ra_deg REAL NOT NULL,
            dec_deg REAL NOT NULL,
            width_deg REAL NOT NULL,
            height_deg REAL NOT NULL,
            rotation_deg REAL NOT NULL,
            retired INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS starfront_collab_target_cell ON starfront_collab_target(project_guid, cell_row, cell_column);
        CREATE TABLE IF NOT EXISTS starfront_collab_plan (
            exposureplan_guid TEXT NOT NULL,
            night TEXT NOT NULL,
            target_guid TEXT NOT NULL,
            remote_task TEXT NOT NULL,
            panel_index INTEGER NOT NULL,
            filter TEXT NOT NULL,
            exposure_ms INTEGER NOT NULL,
            baseline INTEGER NOT NULL,
            requested INTEGER NOT NULL,
            applied_at INTEGER NOT NULL,
            PRIMARY KEY(exposureplan_guid, night));
        CREATE INDEX IF NOT EXISTS starfront_collab_plan_target ON starfront_collab_plan(target_guid, filter);
        CREATE TABLE IF NOT EXISTS starfront_collab_report (
            server TEXT NOT NULL,
            agent TEXT NOT NULL,
            remote_task TEXT NOT NULL,
            night TEXT NOT NULL,
            panel TEXT NOT NULL,
            filter TEXT NOT NULL,
            frames INTEGER NOT NULL,
            seconds REAL NOT NULL,
            remote_id TEXT,
            accepted INTEGER,
            summary TEXT,
            reported_at INTEGER NOT NULL,
            PRIMARY KEY(server, agent, remote_task, night, panel, filter));
        """);
}
