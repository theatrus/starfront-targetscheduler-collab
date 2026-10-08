using System.Data.SQLite;

namespace StarfrontCollab.Tests;

/// A fresh Target Scheduler database built the way Target Scheduler builds
/// one: the initial schema, then every migration in order.
internal sealed class TestDatabase : IDisposable
{
    public const string Profile = "3f1d8c62-2b0e-4c55-9f0e-6a1f6f0c2a11";

    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "starfront-collab-tests", Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public TestDatabase(int version = 23)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "schedulerdb.sqlite");
        using var db = Open();
        var schema = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "ts_schema");
        Run(db, File.ReadAllText(System.IO.Path.Combine(schema, "initial_schema.sql")));
        for (var step = 1; step <= version; step++)
            Run(db, File.ReadAllText(System.IO.Path.Combine(schema, "migrate", step + ".sql")));
    }

    public SQLiteConnection Open()
    {
        var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = Path, Pooling = false }.ConnectionString);
        db.Open();
        return db;
    }

    public long Count(string sql, params object[] args) => Convert.ToInt64(Scalar(sql, args));

    public object? Scalar(string sql, params object[] args)
    {
        using var db = Open();
        using var command = new SQLiteCommand(sql, db);
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + (i + 1), args[i]);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public void Exec(string sql, params object[] args)
    {
        using var db = Open();
        using var command = new SQLiteCommand(sql, db);
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + (i + 1), args[i]);
        command.ExecuteNonQuery();
    }

    public void AddTemplate(string filter, double exposure, int bin = 1) => Exec("""
        INSERT INTO exposuretemplate (profileId, name, filtername, gain, offset, bin, readoutmode, twilightlevel, moonavoidanceenabled,
            moonavoidanceseparation, moonavoidancewidth, maximumhumidity, defaultexposure, guid)
        VALUES (@p1, @p2, @p2, 100, 10, @p3, -1, 0, 0, 60, 7, 0, @p4, @p5)
        """, Profile, filter, bin, exposure, Guid.NewGuid().ToString("D"));

    private static void Run(SQLiteConnection db, string sql)
    {
        using var transaction = db.BeginTransaction();
        using var command = new SQLiteCommand(sql, db);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }
}
