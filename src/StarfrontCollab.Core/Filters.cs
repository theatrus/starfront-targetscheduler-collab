using System.Globalization;
using System.Text.RegularExpressions;

namespace StarfrontCollab;

/// Filter names as the protocol spells them. A wheel says "Ha 3nm" or "OIII";
/// the server says "H" or "O". Folding ignores case, spaces, dashes,
/// underscores and slashes, and a trailing bandpass such as "3nm".
public static partial class Filters
{
    private static readonly Dictionary<string, string> Letters = new(StringComparer.Ordinal)
    {
        ["l"] = "L",
        ["lum"] = "L",
        ["luminance"] = "L",
        ["clear"] = "L",
        ["uvircut"] = "L",
        ["uvir"] = "L",
        ["ircut"] = "L",
        ["none"] = "L",
        ["r"] = "R",
        ["red"] = "R",
        ["g"] = "G",
        ["green"] = "G",
        ["b"] = "B",
        ["blue"] = "B",
        ["h"] = "H",
        ["hydrogen"] = "H",
        ["ha"] = "H",
        ["halpha"] = "H",
        ["hα"] = "H",
        ["hydrogenalpha"] = "H",
        ["o"] = "O",
        ["oiii"] = "O",
        ["o3"] = "O",
        ["oxygen"] = "O",
        ["oxygeniii"] = "O",
        ["oxygen3"] = "O",
        ["s"] = "S",
        ["sii"] = "S",
        ["s2"] = "S",
        ["sulphur"] = "S",
        ["sulfur"] = "S",
        ["sulphurii"] = "S",
        ["sulfurii"] = "S",
        ["sulphur2"] = "S",
        ["sulfur2"] = "S",
    };

    [GeneratedRegex(@"\s*\(?\s*(\d+(?:\.\d+)?)\s*nm\s*\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Band();

    /// The protocol letter for a filter name, or the trimmed name when it is
    /// not one of the known broadband or narrowband filters.
    public static string Fold(string name)
    {
        var trimmed = name.Trim();
        var key = new string([.. Band().Replace(trimmed, "").ToLowerInvariant()
            .Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-' or '/'))]);
        return Letters.TryGetValue(key, out var letter) ? letter : trimmed;
    }

    public static bool Same(string left, string right) => string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);

    /// A bandpass written into the name, such as "Ha 3nm" or "OIII (6.5 nm)".
    public static double? Bandpass(string name)
    {
        var match = Band().Match(name.Trim());
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nm)
            && nm > 0 && nm < 1e6 ? nm : null;
    }
}
