namespace StarfrontCollab;

/// Low-precision Sun and Moon positions (Astronomical Almanac formulae).
/// Good to a few tenths of a degree, which is all that "is it dark", "is the
/// Moon up" and "how far was the Moon" need. Angles are degrees; RA is degrees.
public static class Astro
{
    private const double Rad = Math.PI / 180.0;

    public static double JulianDay(DateTimeOffset time) => time.ToUnixTimeMilliseconds() / 86_400_000.0 + 2_440_587.5;

    public static (double Ra, double Dec) Sun(double jd)
    {
        var n = jd - 2_451_545.0;
        var mean = 280.460 + 0.9856474 * n;
        var anomaly = (357.528 + 0.9856003 * n) * Rad;
        var longitude = (mean + 1.915 * Math.Sin(anomaly) + 0.020 * Math.Sin(2 * anomaly)) * Rad;
        var obliquity = (23.439 - 0.0000004 * n) * Rad;
        var ra = Math.Atan2(Math.Cos(obliquity) * Math.Sin(longitude), Math.Cos(longitude));
        var dec = Math.Asin(Math.Sin(obliquity) * Math.Sin(longitude));
        return (Normalize(ra / Rad), dec / Rad);
    }

    public static (double Ra, double Dec) Moon(double jd)
    {
        var t = (jd - 2_451_545.0) / 36_525.0;
        double S(double a, double b) => Math.Sin((a + b * t) * Rad);
        var longitude = (218.32 + 481_267.881 * t
            + 6.29 * S(134.9, 477_198.85) - 1.27 * S(259.2, -413_335.38) + 0.66 * S(235.7, 890_534.23)
            + 0.21 * S(269.9, 954_397.70) - 0.19 * S(357.5, 35_999.05) - 0.11 * S(186.6, 966_404.05)) * Rad;
        var latitude = (5.13 * S(93.3, 483_202.03) + 0.28 * S(228.2, 960_400.87)
            - 0.28 * S(318.3, 6_003.18) - 0.17 * S(217.6, -407_332.20)) * Rad;
        var obliquity = (23.4393 - 0.0130 * t) * Rad;
        var ra = Math.Atan2(Math.Sin(longitude) * Math.Cos(obliquity) - Math.Tan(latitude) * Math.Sin(obliquity), Math.Cos(longitude));
        var dec = Math.Asin(Math.Sin(latitude) * Math.Cos(obliquity) + Math.Cos(latitude) * Math.Sin(obliquity) * Math.Sin(longitude));
        return (Normalize(ra / Rad), dec / Rad);
    }

    /// The lit fraction of the Moon's disc, 0 (new) to 1 (full).
    public static double MoonIllumination(double jd)
    {
        var sun = Sun(jd);
        var moon = Moon(jd);
        return (1.0 - Math.Cos(Separation(sun.Ra, sun.Dec, moon.Ra, moon.Dec) * Rad)) / 2.0;
    }

    public static double Altitude(double ra, double dec, double latitude, double longitude, double jd)
    {
        var sidereal = 280.46061837 + 360.98564736629 * (jd - 2_451_545.0) + longitude;
        var hour = (sidereal - ra) * Rad;
        var phi = latitude * Rad;
        var delta = dec * Rad;
        return Math.Asin(Math.Sin(phi) * Math.Sin(delta) + Math.Cos(phi) * Math.Cos(delta) * Math.Cos(hour)) / Rad;
    }

    /// Great-circle distance between two positions, by the haversine.
    public static double Separation(double ra1, double dec1, double ra2, double dec2)
    {
        var dRa = (ra2 - ra1) * Rad;
        var dDec = (dec2 - dec1) * Rad;
        var h = Math.Pow(Math.Sin(dDec / 2), 2) + Math.Cos(dec1 * Rad) * Math.Cos(dec2 * Rad) * Math.Pow(Math.Sin(dRa / 2), 2);
        return 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(h))) / Rad;
    }

    public static double Normalize(double degrees)
    {
        var value = degrees % 360.0;
        return value < 0 ? value + 360.0 : value;
    }
}
