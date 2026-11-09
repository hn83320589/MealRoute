using Xunit.Sdk;

namespace MealRoute.Tests.Infrastructure;

public class PropertyTests
{
    [Fact]
    public void ForAll_runs_every_case_with_consecutive_seeds()
    {
        var seen = new List<int>();
        Property.ForAll(100, 5, (_, seed) => seen.Add(seed));
        Assert.Equal(new[] { 100, 101, 102, 103, 104 }, seen);
    }

    [Fact]
    public void ForAll_failure_message_contains_reproducible_seed()
    {
        var ex = Assert.Throws<XunitException>(() =>
            Property.ForAll(7, 10, (_, seed) => Assert.NotEqual(12, seed)));
        Assert.Contains("seed 12", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_seed_yields_same_random_sequence()
    {
        int a = 0, b = 0;
        Property.Replay(42, (r, _) => a = r.Next());
        Property.Replay(42, (r, _) => b = r.Next());
        Assert.Equal(a, b);
    }
}
