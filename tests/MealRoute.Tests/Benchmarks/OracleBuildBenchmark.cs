using System.Diagnostics;
using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Core.Travel;
using Xunit.Abstractions;

namespace MealRoute.Tests.Benchmarks;

/// <summary>Excluded from CI (Category=Benchmark). Run manually to measure L1 build time at target scale.</summary>
[Trait("Category", "Benchmark")]
public class OracleBuildBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void Build_l1_on_20k_node_grid_with_215_locations()
    {
        const int size = 142; // 142 x 142 ≈ 20k nodes
        var features = new List<RoadFeature>();
        var r = new Random(1);
        static GeoPoint At(int i, int j) => new(25 + i * 0.0009, 121.4 + j * 0.001);
        for (var i = 0; i < size; i++)
            for (var j = 0; j < size; j++)
            {
                var here = At(i, j);
                var cls = (RoadClass)(i % 10 == 0 || j % 10 == 0 ? 0 : i % 5 == 0 || j % 5 == 0 ? 1 : 2);
                if (j + 1 < size) features.Add(new(features.Count, $"h{i}_{j}", new[] { here, At(i, j + 1) }, r.NextDouble() < 0.1 ? OnewayDirection.Forward : OnewayDirection.Both, cls));
                if (i + 1 < size) features.Add(new(features.Count, $"v{i}_{j}", new[] { here, At(i + 1, j) }, OnewayDirection.Both, cls));
            }
        var net = NetworkBuilder.Build(features);
        var analysis = NetworkAnalysis.Analyze(net);
        var largest = Enumerable.Range(0, net.NodeCount).Where(analysis.IsInLargestComponent).ToArray();
        var locations = Enumerable.Range(0, 215).Select(_ => largest[r.Next(largest.Length)]).ToArray();

        var sw = Stopwatch.StartNew();
        var l1 = PathSampledOracle.Build(net, SpeedProfile.Default, locations, 10 * 3600, 16 * 3600, new OracleBuildOptions());
        sw.Stop();
        output.WriteLine($"largestScc={largest.Length} nodes={net.NodeCount} edges={net.EdgeCount} samples={l1.SampleTimes.Count} cores={Environment.ProcessorCount} build={sw.Elapsed.TotalSeconds:F2}s");

        var sw4 = Stopwatch.StartNew();
        PathSampledOracle.Build(net, SpeedProfile.Default, locations, 10 * 3600, 16 * 3600, new OracleBuildOptions { MaxDegreeOfParallelism = 4 });
        output.WriteLine($"build with 4 threads={sw4.Elapsed.TotalSeconds:F2}s");

        var q = Stopwatch.StartNew();
        double sink = 0;
        for (var k = 0; k < 200_000; k++) sink += l1.ArrivalTime(k % 215, (k * 7) % 215, 40_000 + k % 3600);
        q.Stop();
        output.WriteLine($"L1 query: {q.Elapsed.TotalMilliseconds * 1000 / 200_000:F2} µs (sink {sink:F0})");

        var exact = new ExactOracle(net, SpeedProfile.Default, locations);
        var e = Stopwatch.StartNew();
        for (var k = 0; k < 300; k++) sink += exact.ArrivalTime(k % 215, (k * 7 + 1) % 215, 40_000 + k);
        e.Stop();
        output.WriteLine($"L2 query (random pairs): {e.Elapsed.TotalMilliseconds / 300:F2} ms");
    }
}
