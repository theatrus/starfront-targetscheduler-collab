using System.Globalization;

namespace StarfrontCollab;

/// One observing night at a site. A night runs from local mean noon to the
/// next local mean noon and is named by the date it starts on. The server compares the name it dealt against the name the
/// rig asks with, so the same rig must always name the same night the same way.
public sealed record NightSky(string Name, DateTimeOffset Start, DateTimeOffset End, double? MoonIllumination, double? MoonUpFraction)
{
    private const double DarkSunAltitude = -18.0;

    public static NightSky For(DateTimeOffset now, double latitude, double longitude)
    {
        var local = now.UtcDateTime + Offset(longitude);
        return Of(DateOnly.FromDateTime(local.AddHours(-12)), latitude, longitude);
    }

    /// The night, with the Moon over its astronomical darkness: the mean lit
    /// fraction and how much of the dark it is up for. Both are null when the
    /// Sun never gets 18 degrees down.
    public static NightSky Of(DateOnly date, double latitude, double longitude)
    {
        var (start, end) = Window(date, longitude);
        int dark = 0, up = 0;
        var lit = 0.0;
        for (var time = start; time < end; time = time.AddMinutes(10))
        {
            var jd = Astro.JulianDay(time);
            var sun = Astro.Sun(jd);
            if (Astro.Altitude(sun.Ra, sun.Dec, latitude, longitude, jd) > DarkSunAltitude) continue;
            dark++;
            var moon = Astro.Moon(jd);
            if (Astro.Altitude(moon.Ra, moon.Dec, latitude, longitude, jd) > 0) up++;
            lit += Astro.MoonIllumination(jd);
        }
        return new(NameOf(date), start, end,
            dark == 0 ? null : Math.Round(lit / dark, 3),
            dark == 0 ? null : Math.Round((double)up / dark, 3));
    }

    public static (DateTimeOffset Start, DateTimeOffset End) Window(DateOnly date, double longitude)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) - Offset(longitude);
        return (start, start.AddDays(1));
    }

    public static string NameOf(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParse(string name, out DateOnly date) =>
        DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static TimeSpan Offset(double longitude) => TimeSpan.FromHours(longitude / 15.0);
}
