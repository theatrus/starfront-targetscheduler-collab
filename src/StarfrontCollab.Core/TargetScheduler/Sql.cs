using System.Data.SQLite;
using System.Globalization;

namespace StarfrontCollab.TargetScheduler;

/// Small positional helpers: parameters are bound as @p1, @p2, ...
internal static class Sql
{
    internal static SQLiteCommand Command(SQLiteConnection db, string sql, params object?[] args)
    {
        var command = new SQLiteCommand(sql, db);
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + (i + 1), args[i] ?? DBNull.Value);
        return command;
    }

    internal static int Exec(SQLiteConnection db, string sql, params object?[] args)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SQLiteConnection db, string sql, params object?[] args)
    {
        using var command = Command(db, sql, args);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    internal static List<object?[]> Rows(SQLiteConnection db, string sql, params object?[] args)
    {
        using var command = Command(db, sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    internal static long LastId(SQLiteConnection db) => Long(Scalar(db, "SELECT last_insert_rowid()"));

    internal static bool TableExists(SQLiteConnection db, string name) =>
        Scalar(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @p1", name) is not null;

    internal static long Long(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    internal static double Double(object? value) => value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    internal static string Text(object? value) => value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// Target Scheduler writes lowercase hyphenated GUIDs.
    internal static string NewGuid() => Guid.NewGuid().ToString("D");
}
