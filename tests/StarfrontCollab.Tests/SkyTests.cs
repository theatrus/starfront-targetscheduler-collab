using Xunit;

namespace StarfrontCollab.Tests;

public sealed class SkyTests
{
    [Theory]
    [InlineData("Ha", "H")]
    [InlineData("Ha 3nm", "H")]
    [InlineData("H-alpha", "H")]
    [InlineData("OIII (6.5 nm)", "O")]
    [InlineData("O3", "O")]
    [InlineData("SII", "S")]
    [InlineData("Sulfur II", "S")]
    [InlineData("Lum", "L")]
    [InlineData("UV/IR Cut", "L")]
    [InlineData("Red", "R")]
    [InlineData(" g ", "G")]
    [InlineData("Blue", "B")]
    [InlineData("L-eXtreme", "L-eXtreme")]
    [InlineData("Duo Band", "Duo Band")]
    public void FoldsFilterNamesToProtocolLetters(string name, string letter) => Assert.Equal(letter, Filters.Fold(name));

    [Fact]
    public void ReadsBandpassesWrittenIntoNames()
    {
        Assert.Equal(3.0, Filters.Bandpass("Ha 3nm"));
        Assert.Equal(6.5, Filters.Bandpass("OIII (6.5 nm)"));
        Assert.Null(Filters.Bandpass("SII"));
    }

    [Fact]
    public void SunReachesTheSolsticeDeclination()
    {
        var sun = Astro.Sun(Astro.JulianDay(new DateTimeOffset(2024, 6, 20, 20, 51, 0, TimeSpan.Zero)));
        Assert.InRange(sun.Dec, 23.3, 23.5);
    }

    [Fact]
    public void MoonIsFullAndNewOnKnownDates()
    {
        Assert.InRange(Astro.MoonIllumination(Astro.JulianDay(new DateTimeOffset(2024, 1, 25, 17, 54, 0, TimeSpan.Zero))), 0.98, 1.0);
        Assert.InRange(Astro.MoonIllumination(Astro.JulianDay(new DateTimeOffset(2024, 1, 11, 11, 57, 0, TimeSpan.Zero))), 0.0, 0.02);
    }

    [Fact]
    public void SeparationIsAGreatCircleDistance()
    {
        Assert.Equal(90.0, Astro.Separation(0, 0, 90, 0), 6);
        Assert.Equal(10.0, Astro.Separation(359, 40, 359, 50), 6);
        Assert.Equal(1.0, Astro.Separation(359.5, 0, 0.5, 0), 6);
    }

    [Fact]
    public void NightIsNamedByTheDateItStartsAtLocalMeanNoon()
    {
        // Longitude -120: local mean time is UTC minus eight hours.
        Assert.Equal("2026-10-05", NightSky.For(new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero), 37, -120).Name);
        Assert.Equal("2026-10-05", NightSky.For(new DateTimeOffset(2026, 10, 6, 19, 59, 0, TimeSpan.Zero), 37, -120).Name);
        Assert.Equal("2026-10-06", NightSky.For(new DateTimeOffset(2026, 10, 6, 20, 1, 0, TimeSpan.Zero), 37, -120).Name);
        var (start, end) = NightSky.Window(new DateOnly(2026, 10, 5), -120);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 20, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromDays(1), end - start);
    }

    [Fact]
    public void NightCarriesItsMoonOnlyWhenItGetsDark()
    {
        var autumn = NightSky.Of(new DateOnly(2026, 10, 5), 37, -120);
        Assert.NotNull(autumn.MoonIllumination);
        Assert.InRange(autumn.MoonUpFraction!.Value, 0.0, 1.0);
        var arctic = NightSky.Of(new DateOnly(2026, 6, 21), 78, 15);
        Assert.Null(arctic.MoonIllumination);
        Assert.Null(arctic.MoonUpFraction);
    }
}
