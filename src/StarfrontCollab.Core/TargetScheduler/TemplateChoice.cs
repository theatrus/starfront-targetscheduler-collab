using System.Data.SQLite;
using static StarfrontCollab.TargetScheduler.Sql;

namespace StarfrontCollab.TargetScheduler;

internal sealed record Template(long Id, string FilterName, double DefaultExposure);

/// Which of a profile's exposure templates shoots a protocol filter. A
/// template matches when its filter folds to the same letter: "Ha", "HA" and
/// "H-alpha" are all H. Among matches, N.I.N.A. switches filters by exact
/// name, so a template naming a wheel filter exactly comes first, then one
/// naming it in another case, then the one with the longest default exposure.
internal static class TemplateChoice
{
    internal static IReadOnlyList<Template> Read(SQLiteConnection db, string profileId, int binning) =>
        [.. Rows(db, "SELECT Id, filtername, IFNULL(defaultexposure, 60) FROM exposuretemplate WHERE profileId = @p1 AND IFNULL(bin, 1) = @p2 ORDER BY Id",
                profileId, binning)
            .Select(row => new Template(Long(row[0]), Text(row[1]), Double(row[2])))];

    internal static Template? Best(IEnumerable<Template> templates, string filter, IReadOnlyList<string> wheel, string? preferred = null) =>
        templates.Where(t => Filters.Fold(t.FilterName) == filter)
            .OrderByDescending(t => preferred is not null && t.FilterName == preferred)
            .ThenByDescending(t => wheel.Contains(t.FilterName, StringComparer.Ordinal))
            .ThenByDescending(t => wheel.Contains(t.FilterName, StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(t => t.DefaultExposure)
            .ThenBy(t => t.Id)
            .FirstOrDefault();
}
