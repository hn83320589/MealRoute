using MealRoute.Core.Network;
using MealRoute.Core.Travel;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Network;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Travel;

public class TravelTimeOracleTests
{
    private sealed record Setup(RoadNetwork Net, SpeedProfile Profile, double[][] Raw, int[] Locations);

    private static Setup RandomSetup(Random r, bool realisticProfile = false)
    {
        var net = NetworkBuilder.Build(SnapperTests.RandomGrid(r, r.Next(4, 8), out var unusedCount));
        double[][] raw;
        SpeedProfile profile;
        if (realisticProfile)
        {
            profile = SpeedProfile.Default;
            raw = new[] { profile.SpeedsKmh(RoadClass.Arterial), profile.SpeedsKmh(RoadClass.Collector), profile.SpeedsKmh(RoadClass.Residential) };
        }
        else profile = SpeedModelTests.RandomProfile(r, out raw);

        // only locations inside the largest SCC are meaningful (as after snapping)
        var analysis = NetworkAnalysis.Analyze(net);
        var candidates = Enumerable.Range(0, net.NodeCount).Where(analysis.IsInLargestComponent).ToArray();
        var locations = Enumerable.Range(0, Math.Min(8, candidates.Length)).Select(_ => candidates[r.Next(candidates.Length)]).ToArray();
        return new Setup(net, profile, raw, locations);
    }

    private static PathSampledOracle BuildL1(Setup s, double start = 10 * 3600, double end = 14 * 3600, int parallelism = 2) =>
        PathSampledOracle.Build(s.Net, s.Profile, s.Locations, start, end, new OracleBuildOptions { MaxDegreeOfParallelism = parallelism });

    [Fact]
    public void Property_exact_oracle_matches_reference_td_dijkstra()
    {
        Property.ForAll(30_000, 60, (r, _) =>
        {
            var s = RandomSetup(r);
            var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
            var refGraph = TimeDependentDijkstraTests.ToRef(s.Net);
            for (var q = 0; q < 5; q++)
            {
                int a = r.Next(s.Locations.Length), b = r.Next(s.Locations.Length);
                var t = r.NextDouble() * 86_400;
                var (expected, _) = RefDijkstra.TimeDependent(refGraph, s.Raw, s.Locations[a], t);
                Assert.Equal(expected[s.Locations[b]], exact.ArrivalTime(a, b, t), 1e-6);
            }
        });
    }

    [Fact]
    public void Property_sampled_oracle_is_exact_at_sample_times()
    {
        Property.ForAll(31_000, 40, (r, _) =>
        {
            var s = RandomSetup(r);
            var l1 = BuildL1(s);
            var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
            for (var k = 0; k < l1.SampleTimes.Count; k++)
                for (var a = 0; a < s.Locations.Length; a++)
                    for (var b = 0; b < s.Locations.Length; b++)
                    {
                        var t = l1.SampleTimes[k];
                        Assert.Equal(exact.ArrivalTime(a, b, t), l1.ArrivalTime(a, b, t), 1e-9);
                    }
        });
    }

    [Fact]
    public void Property_sampled_oracle_is_an_upper_bound_of_exact_and_fifo()
    {
        Property.ForAll(32_000, 60, (r, _) =>
        {
            var s = RandomSetup(r);
            var l1 = BuildL1(s);
            var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
            for (var q = 0; q < 20; q++)
            {
                int a = r.Next(s.Locations.Length), b = r.Next(s.Locations.Length);
                var t1 = 9 * 3600 + r.NextDouble() * 6 * 3600; // includes times outside the horizon
                var t2 = t1 + (r.NextDouble() < 0.3 ? r.NextDouble() * 1e-3 : r.NextDouble() * 1800);
                var c1 = l1.ArrivalTime(a, b, t1);
                Assert.True(c1 >= exact.ArrivalTime(a, b, t1) - 1e-9, "L1 must be an upper bound (a real path's time)");
                Assert.True(c1 <= l1.ArrivalTime(a, b, t2) + 1e-9, $"L1 FIFO violated between {t1} and {t2}");
            }
        });
    }

    [Fact]
    public void Property_sampled_oracle_is_exact_when_speeds_are_constant()
    {
        Property.ForAll(33_000, 40, (r, _) =>
        {
            var s = RandomSetup(r);
            var flat = SpeedModelTests.Flat(20 + r.NextDouble() * 30);
            s = s with { Profile = new SpeedProfile(flat, SpeedModelTests.Flat(25), SpeedModelTests.Flat(15)) };
            var l1 = BuildL1(s);
            var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
            for (var q = 0; q < 20; q++)
            {
                int a = r.Next(s.Locations.Length), b = r.Next(s.Locations.Length);
                var t = 10 * 3600 + r.NextDouble() * 4 * 3600;
                Assert.Equal(exact.ArrivalTime(a, b, t), l1.ArrivalTime(a, b, t), 1e-6);
            }
        });
    }

    [Fact]
    public void Property_healing_makes_the_queried_leg_exact_and_keeps_fifo()
    {
        Property.ForAll(34_000, 40, (r, _) =>
        {
            var s = RandomSetup(r);
            var l1 = BuildL1(s);
            var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
            int a = r.Next(s.Locations.Length), b = r.Next(s.Locations.Length);
            var t = 10 * 3600 + r.NextDouble() * 4 * 3600;

            l1.Heal(a, b, t, exact);

            Assert.Equal(exact.ArrivalTime(a, b, t), l1.ArrivalTime(a, b, t), 1e-9);
            var prev = double.NegativeInfinity;
            for (var x = 10 * 3600.0; x <= 14 * 3600; x += 37)
            {
                var v = l1.ArrivalTime(a, b, x);
                Assert.True(v >= prev - 1e-9);
                prev = v;
            }
        });
    }

    [Fact]
    public void Build_result_does_not_depend_on_parallelism()
    {
        var s = RandomSetup(new Random(77), realisticProfile: true);
        var one = BuildL1(s, parallelism: 1);
        var four = BuildL1(s, parallelism: 4);
        var r = new Random(5);
        for (var q = 0; q < 500; q++)
        {
            int a = r.Next(s.Locations.Length), b = r.Next(s.Locations.Length);
            var t = 9 * 3600 + r.NextDouble() * 6 * 3600;
            Assert.Equal(one.ArrivalTime(a, b, t), four.ArrivalTime(a, b, t));
            Assert.Equal(one.Leg(a, b, t).DistanceM, four.Leg(a, b, t).DistanceM);
        }
    }

    [Fact]
    public void Same_location_takes_zero_time_and_distance()
    {
        var s = RandomSetup(new Random(8));
        var l1 = BuildL1(s);
        var leg = l1.Leg(0, 0, 40_000);
        Assert.Equal(40_000, leg.Arrival);
        Assert.Equal(0, leg.DistanceM);
    }

    [Fact]
    public void Unsnapped_location_is_unreachable()
    {
        var s = RandomSetup(new Random(9));
        var locations = s.Locations.Append(-1).ToArray();
        var exact = new ExactOracle(s.Net, s.Profile, locations);
        Assert.True(double.IsPositiveInfinity(exact.ArrivalTime(0, locations.Length - 1, 40_000)));
        var l1 = PathSampledOracle.Build(s.Net, s.Profile, locations, 36_000, 50_000, new OracleBuildOptions());
        Assert.True(double.IsPositiveInfinity(l1.ArrivalTime(0, locations.Length - 1, 40_000)));
    }

    [Fact]
    public void Leg_distance_is_the_length_of_the_path_used()
    {
        var s = RandomSetup(new Random(10), realisticProfile: true);
        var exact = new ExactOracle(s.Net, s.Profile, s.Locations);
        var path = exact.PathEdges(0, 1, 40_000);
        Assert.Equal(path.Sum(s.Net.EdgeLength), exact.Leg(0, 1, 40_000).DistanceM, 9);
    }

    [Fact]
    public void Build_reports_monotone_progress_and_honours_cancellation()
    {
        var s = RandomSetup(new Random(11), realisticProfile: true);
        var reported = new List<double>();
        var progress = new SynchronousProgress<double>(reported.Add);
        PathSampledOracle.Build(s.Net, s.Profile, s.Locations, 36_000, 50_000, new OracleBuildOptions { Progress = progress, MaxDegreeOfParallelism = 4 });
        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1]);
        for (var i = 1; i < reported.Count; i++) Assert.True(reported[i] >= reported[i - 1]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PathSampledOracle.Build(s.Net, s.Profile, s.Locations, 36_000, 50_000, new OracleBuildOptions { CancellationToken = cts.Token }));
    }
}

/// <summary>IProgress that invokes the handler synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly object _gate = new();
    public void Report(T value) { lock (_gate) handler(value); }
}
