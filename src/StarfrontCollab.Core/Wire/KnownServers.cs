namespace StarfrontCollab.Wire;

/// Starfront's collaboration server, and the addresses it used to answer on.
public static class KnownServers
{
    public const string Starfront = "https://collab.starfront.space/";

    // The same server under its earlier name. A setting or a saved token that
    // still names it moves to the current address.
    private static readonly string[] Former = ["https://starfront-bray.duckdns.org/"];

    /// The address to use for a configured one: a former Starfront address
    /// becomes the current one, a blank one becomes Starfront's, and anything
    /// else is left as typed.
    public static string Canonical(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return Starfront;
        var slashed = trimmed.EndsWith('/') ? trimmed : trimmed + "/";
        return Former.Any(f => string.Equals(f, slashed, StringComparison.OrdinalIgnoreCase)) ? Starfront : trimmed;
    }

    /// Earlier addresses of this server, where a saved token may still be filed.
    public static IReadOnlyList<Uri> FormerAddressesOf(Uri server) =>
        server.AbsoluteUri == Starfront ? [.. Former.Select(f => new Uri(f))] : [];
}
