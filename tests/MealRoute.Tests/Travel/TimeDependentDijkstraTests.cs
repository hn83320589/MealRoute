using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Core.Travel;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Network;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Travel;

public class TimeDependentDijkstraTests
{
    internal static RefGraph ToRef(RoadNetwork n) => new(n.NodeCount,
        Enumerable.Range(0, n.EdgeCount).Select(e => (n.EdgeTail(e), n.EdgeHead(e), (int)n.EdgeClass(e), n.EdgeLength(e))).ToList());

    private static RoadNetwork RandomNetwork(Random r) => NetworkBuilder.Build(SnapperTests.RandomGrid(r, r.Next(3, 8), out _));

    [Fact]
    public void Property_matches_reference_td_dijkstra_on_random_networks()
    {
        Property.ForAll(20_000, 80, (r, _) =>
        {
            var net = RandomNetwork(r);
            var profile = SpeedModelTests.RandomProfile(r, out var raw);
            var source = r.Next(net.NodeCount);
            var departure = r.NextDouble() * 86_400;

            var td = new TimeDependentDijkstra(net, profile);
            td.Run(source, departure);
            var (expected, _) = RefDijkstra.TimeDependent(ToRef(net), raw, source, departure);

            for (var v = 0; v < net.NodeCount; v++)
            {
                if (double.IsPositiveInfinity(expected[v])) Assert.True(double.IsPositiveInfinity(td.ArrivalAt(v)));
                else Assert.Equal(expected[v], td.ArrivalAt(v), 1e-6);
            }
        });
    }

    [Fact]
    public void Property_returned_path_integrated_by_reference_equals_reported_arrival()
    {
        Property.ForAll(21_000, 80, (r, _) =>
        {
            var net = RandomNetwork(r);
            var profile = SpeedModelTests.RandomProfile(r, out var raw);
            var source = r.Next(net.NodeCount);
            var target = r.Next(net.NodeCount);
            var departure = r.NextDouble() * 86_400;

            var td = new TimeDependentDijkstra(net, profile);
            var arrival = td.EarliestArrival(source, target, departure);
            if (double.IsPositiveInfinity(arrival)) return;
            var path = td.PathTo(target);

            var tail = source;
            foreach (var e in path) { Assert.Equal(tail, net.EdgeTail(e)); tail = net.EdgeHead(e); }
            Assert.Equal(target, tail);
            var integrated = RefTdIntegrator.PathArrival(raw, path.Select(e => ((int)net.EdgeClass(e), net.EdgeLength(e))), departure);
            Assert.Equal(arrival, integrated, 1e-6);
        });
    }

    [Fact]
    public void Property_point_to_point_astar_equals_one_to_all()
    {
        Property.ForAll(22_000, 80, (r, _) =>
        {
            var net = RandomNetwork(r);
            var profile = SpeedModelTests.RandomProfile(r, out var unusedRaw);
            var departure = r.NextDouble() * 86_400;
            var source = r.Next(net.NodeCount);
            var all = new TimeDependentDijkstra(net, profile);
            all.Run(source, departure);
            var p2p = new TimeDependentDijkstra(net, profile);
            for (var k = 0; k < 5; k++)
            {
                var target = r.Next(net.NodeCount);
                var a = p2p.EarliestArrival(source, target, departure);
                if (double.IsPositiveInfinity(all.ArrivalAt(target))) Assert.True(double.IsPositiveInfinity(a));
                else Assert.Equal(all.ArrivalAt(target), a, 1e-6);
            }
        });
    }

    [Fact]
    public void Property_constant_speed_equals_static_dijkstra()
    {
        Property.ForAll(23_000, 80, (r, _) =>
        {
            var net = RandomNetwork(r);
            var kmh = 10 + r.NextDouble() * 50;
            var flat = SpeedModelTests.Flat(kmh);
            var td = new TimeDependentDijkstra(net, new SpeedProfile(flat, flat, flat));
            var source = r.Next(net.NodeCount);
            var departure = r.NextDouble() * 86_400;
            td.Run(source, departure);
            var dist = RefDijkstra.StaticDistances(ToRef(net), source);
            for (var v = 0; v < net.NodeCount; v++)
            {
                if (double.IsPositiveInfinity(dist[v])) continue;
                Assert.Equal(dist[v] / (kmh / 3.6), td.ArrivalAt(v) - departure, 1e-6);
            }
        });
    }

    [Fact]
    public void Property_td_shortest_path_arrival_is_fifo_in_departure_time()
    {
        Property.ForAll(24_000, 60, (r, _) =>
        {
            var net = RandomNetwork(r);
            var profile = SpeedModelTests.RandomProfile(r, out var unusedRaw);
            var td = new TimeDependentDijkstra(net, profile);
            var s = r.Next(net.NodeCount);
            var d = r.Next(net.NodeCount);
            var t1 = r.NextDouble() * 86_400;
            var t2 = t1 + r.NextDouble() * 1_800;
            var a1 = td.EarliestArrival(s, d, t1);
            var a2 = td.EarliestArrival(s, d, t2);
            if (!double.IsPositiveInfinity(a1)) Assert.True(a1 <= a2 + 1e-9);
        });
    }

    [Fact]
    public void Unreachable_target_has_infinite_arrival_and_empty_path()
    {
        var features = new[]
        {
            new RoadFeature(0, "a", new[] { new GeoPoint(25, 121.5), new GeoPoint(25, 121.501) }, OnewayDirection.Forward, RoadClass.Residential),
        };
        var td = new TimeDependentDijkstra(NetworkBuilder.Build(features), SpeedProfile.Default);
        Assert.True(double.IsPositiveInfinity(td.EarliestArrival(1, 0, 1000)));
        Assert.Empty(td.PathTo(0));
    }

    [Fact]
    public void Source_equal_to_target_arrives_immediately()
    {
        var net = NetworkBuilder.Build(SnapperTests.RandomGrid(new Random(1), 3, out _));
        var td = new TimeDependentDijkstra(net, SpeedProfile.Default);
        Assert.Equal(500.0, td.EarliestArrival(2, 2, 500));
        Assert.Empty(td.PathTo(2));
    }

    [Fact]
    public void Run_with_multiple_initial_labels_takes_the_best_start()
    {
        var net = NetworkBuilder.Build(SnapperTests.RandomGrid(new Random(3), 4, out _));
        var td = new TimeDependentDijkstra(net, SpeedProfile.Default);
        td.Run(0, 1000);
        var viaZero = td.ArrivalAt(net.NodeCount - 1);
        td.Run(new[] { (0, 1000.0), (net.NodeCount - 1, 5000.0) });
        Assert.Equal(Math.Min(viaZero, 5000.0), td.ArrivalAt(net.NodeCount - 1), 9);
    }

    [Fact]
    public void Early_stop_on_targets_gives_exact_values_for_those_targets()
    {
        var r = new Random(9);
        var net = NetworkBuilder.Build(SnapperTests.RandomGrid(r, 7, out _));
        var full = new TimeDependentDijkstra(net, SpeedProfile.Default);
        full.Run(0, 40_000);
        var partial = new TimeDependentDijkstra(net, SpeedProfile.Default);
        var targets = new[] { 3, 10, 20 }.Where(t => t < net.NodeCount).ToArray();
        partial.Run(new[] { (0, 40_000.0) }, targets);
        foreach (var t in targets) Assert.Equal(full.ArrivalAt(t), partial.ArrivalAt(t));
    }
}
