using Xunit.Sdk;

namespace MealRoute.Tests.Infrastructure;

/// <summary>
/// Minimal seeded property-test runner. Each case gets its own seed derived from
/// <c>baseSeed + i</c>; a failure is rethrown with the exact seed so it can be
/// reproduced via <see cref="Replay"/>.
/// </summary>
public static class Property
{
    public static void ForAll(int baseSeed, int cases, Action<Random, int> check)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cases);
        for (var i = 0; i < cases; i++)
        {
            var seed = unchecked(baseSeed + i);
            try
            {
                check(new Random(seed), seed);
            }
            catch (Exception ex)
            {
                throw new XunitException(
                    $"Property failed for seed {seed} (case {i + 1}/{cases}). " +
                    $"Reproduce with Property.Replay({seed}, ...).{Environment.NewLine}{ex}");
            }
        }
    }

    public static void Replay(int seed, Action<Random, int> check) => check(new Random(seed), seed);
}
