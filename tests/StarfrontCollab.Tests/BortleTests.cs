using StarfrontCollab.Wire;
using Xunit;

namespace StarfrontCollab.Tests;

public sealed class BortleTests
{
    private static RigProfile Rig => new("Test rig", 530, 3.76, 6248, 4176, 1, false, null, null, [], new Dictionary<string, double>());
    private static Contribution Report => new("project", "task", "2026-10-05", "L", "0", 1, 300, 300,
        new Region(10, 40, 2, 1, 0), null, null, null, null, null, null, null, false);

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    public void HelloAndContributionCarryTheDeclaredRating(int? rating)
    {
        Assert.Equal(rating, (Rig with { Bortle = rating }).ToJson()["bortle"]?.GetValue<int>());
        Assert.Equal(rating, (Report with { Bortle = rating }).ToJson()["bortle"]?.GetValue<int>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10)]
    public void InvalidRatingsAreNeverSent(int rating)
    {
        Assert.Throws<WireException>(() => (Rig with { Bortle = rating }).ToJson());
        Assert.Throws<WireException>(() => (Report with { Bortle = rating }).ToJson());
    }
}
