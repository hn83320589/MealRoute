using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Network;

public class SnapperTests
{
    // ~111 m per 0.001 deg latitude; at lat 25, 0.001 deg lon ≈ 100.9 m
    private static GeoPoint P(double lat, double lon) => new(lat, lon);

    private static RoadFeature F(int i, string id, OnewayDirection ow, params GeoPoint[] pts) =>
        new(i, id, pts, ow, RoadClass.Residential);

    private static SnappedNetwork Snap(IReadOnlyList<RoadFeature> features, params GeoPoint[] points)
    {
        var net = NetworkBuilder.Build(features);
        var analysis = NetworkAnalysis.Analyze(net);
        var requests = points.Select((p, i) => new SnapRequest($"s{i}", p)).ToArray();
        return Snapper.Snap(net, analysis, requests);
    }

    /// <summary>A square loop of two-way roads around (25.000..25.002, 121.500..121.502).</summary>
    private static List<RoadFeature> Square(OnewayDirection southRoad = OnewayDirection.Both) => new()
    {
        F(0, "south", southRoad, P(25.000, 121.500), P(25.000, 121.502)),
        F(1, "east", OnewayDirection.Both, P(25.000, 121.502), P(25.002, 121.502)),
        F(2, "north", OnewayDirection.Both, P(25.002, 121.502), P(25.002, 121.500)),
        F(3, "west", OnewayDirection.Both, P(25.002, 121.500), P(25.000, 121.500)),
    };

    [Fact]
    public void Point_beside_road_projects_to_the_perpendicular_foot_in_the_middle_of_the_segment()
    {
        var s = Snap(Square(), P(24.9997, 121.5008));
        var sp = Assert.Single(s.Points);
        Assert.Equal("south", s.Graph.Features[sp.FeatureIndex].Id);
        Assert.Equal(25.000, sp.Projected.Lat, 7);
        Assert.Equal(121.5008, sp.Projected.Lon, 7);
        Assert.Equal(RefHaversine.Distance(24.9997, 121.5008, 25.0, 121.5008), sp.DistanceM, 1);
        Assert.True(sp.Node >= 4, "a virtual node is created");
    }

    [Fact]
    public void Split_lengths_sum_to_the_original_length()
    {
        var features = Square();
        var original = Haversine.PolylineLength(features[0].Coordinates);
        var s = Snap(features, P(24.9998, 121.5013));
        var v = s.Points[0].Node;
        var g = s.Graph;
        var into = Enumerable.Range(0, g.EdgeCount).Where(e => g.EdgeHead(e) == v && g.EdgeTail(e) == 0).Single();
        var (start, end) = g.OutEdgeRange(v);
        var outToEast = Enumerable.Range(start, end - start).Single(e => g.EdgeHead(e) == 1);
        Assert.Equal(original, g.EdgeLength(into) + g.EdgeLength(outToEast), 9);
    }

    [Fact]
    public void Two_way_road_virtual_node_is_reachable_and_leavable_in_both_directions()
    {
        var s = Snap(Square(), P(24.9998, 121.5013));
        var g = s.Graph;
        var v = s.Points[0].Node;
        var outHeads = Enumerable.Range(g.OutEdgeRange(v).Start, g.OutEdgeRange(v).End - g.OutEdgeRange(v).Start).Select(g.EdgeHead).OrderBy(x => x);
        var inTails = Enumerable.Range(0, g.EdgeCount).Where(e => g.EdgeHead(e) == v).Select(g.EdgeTail).OrderBy(x => x);
        Assert.Equal(new[] { 0, 1 }, outHeads);
        Assert.Equal(new[] { 0, 1 }, inTails);
    }

    [Fact]
    public void One_way_road_virtual_node_is_only_reachable_along_the_driving_direction()
    {
        var s = Snap(Square(OnewayDirection.Forward), P(24.9998, 121.5013));
        var g = s.Graph;
        var v = s.Points[0].Node;
        var (start, end) = g.OutEdgeRange(v);
        Assert.Equal(new[] { 1 }, Enumerable.Range(start, end - start).Select(g.EdgeHead));
        Assert.Equal(new[] { 0 }, Enumerable.Range(0, g.EdgeCount).Where(e => g.EdgeHead(e) == v).Select(g.EdgeTail));
    }

    [Fact]
    public void Projection_within_one_metre_of_an_endpoint_uses_the_endpoint_and_creates_no_zero_length_edge()
    {
        var features = Square();
        var baseNet = NetworkBuilder.Build(features);
        // 0.000005 deg lon ≈ 0.5 m east of node (25.000, 121.500)
        var s = Snap(features, P(24.9995, 121.500005));
        Assert.Equal(0, s.Points[0].Node);
        Assert.Equal(baseNet.NodeCount, s.Graph.NodeCount);
        Assert.Equal(baseNet.EdgeCount, s.Graph.EdgeCount);
        Assert.All(Enumerable.Range(0, s.Graph.EdgeCount), e => Assert.True(s.Graph.EdgeLength(e) > 0));
    }

    [Fact]
    public void Point_beyond_the_end_of_a_road_clamps_to_the_endpoint_node()
    {
        var features = new List<RoadFeature>
        {
            F(0, "a", OnewayDirection.Both, P(25.0, 121.500), P(25.0, 121.501)),
        };
        var s = Snap(features, P(25.0, 121.5012));
        Assert.Equal(1, s.Points[0].Node);
        Assert.Equal(2, s.Graph.NodeCount);
    }

    [Fact]
    public void Points_farther_than_150_m_are_snapped_but_warned()
    {
        var s = Snap(Square(), P(24.9985, 121.501), P(24.9995, 121.501));
        Assert.True(s.Points[0].IsFar);
        Assert.False(s.Points[1].IsFar);
        var w = Assert.Single(s.Warnings);
        Assert.Equal("s0", w.PointId);
        Assert.Contains("150", w.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Points_never_snap_to_roads_outside_the_largest_component()
    {
        var features = Square();
        // tiny isolated island right next to the point
        features.Add(F(4, "island", OnewayDirection.Both, P(24.9990, 121.5100), P(24.9990, 121.5101)));
        var s = Snap(features, P(24.9990, 121.51005));
        var sp = s.Points[0];
        Assert.NotEqual("island", s.Graph.Features[sp.FeatureIndex].Id);
        Assert.True(sp.IsFar);
    }

    [Fact]
    public void One_way_dead_end_out_of_the_main_component_is_not_used_for_snapping()
    {
        var features = Square();
        features.Add(F(4, "trap", OnewayDirection.Forward, P(25.000, 121.502), P(24.999, 121.503)));
        var s = Snap(features, P(24.9992, 121.5027));
        Assert.NotEqual("trap", s.Graph.Features[s.Points[0].FeatureIndex].Id);
    }

    [Fact]
    public void Multiple_points_on_the_same_road_are_split_in_order()
    {
        var features = Square(OnewayDirection.Forward);
        var original = Haversine.PolylineLength(features[0].Coordinates);
        var s = Snap(features, P(24.9998, 121.5015), P(24.9998, 121.5005));
        var g = s.Graph;
        int Next(int v) { var (a, _) = g.OutEdgeRange(v); return g.EdgeHead(a); }
        int NextEdge(int v) => g.OutEdgeRange(v).Start;

        var west = s.Points[1].Node;
        var east = s.Points[0].Node;
        var e0 = Enumerable.Range(0, g.EdgeCount).Single(e => g.EdgeTail(e) == 0 && g.EdgeHead(e) == west);
        Assert.Equal(east, Next(west));
        Assert.Equal(1, Next(east));
        var total = g.EdgeLength(e0) + g.EdgeLength(NextEdge(west)) + g.EdgeLength(NextEdge(east));
        Assert.Equal(original, total, 9);
    }

    [Fact]
    public void Points_with_identical_coordinates_share_one_virtual_node()
    {
        var s = Snap(Square(), P(24.9998, 121.5013), P(24.9998, 121.5013));
        Assert.Equal(s.Points[0].Node, s.Points[1].Node);
        Assert.Equal(5, s.Graph.NodeCount);
    }

    [Fact]
    public void Split_edge_geometries_concatenate_to_the_original_polyline()
    {
        var features = new List<RoadFeature>
        {
            F(0, "bent", OnewayDirection.Both, P(25.0, 121.500), P(25.001, 121.501), P(25.0, 121.502)),
            F(1, "back", OnewayDirection.Both, P(25.0, 121.502), P(24.999, 121.501), P(25.0, 121.500)),
        };
        var s = Snap(features, P(25.0012, 121.5013));
        var g = s.Graph;
        var v = s.Points[0].Node;
        var inEdge = Enumerable.Range(0, g.EdgeCount).Single(e => g.EdgeHead(e) == v && g.EdgeTail(e) == 0);
        var outEdge = Enumerable.Range(g.OutEdgeRange(v).Start, g.OutEdgeRange(v).End - g.OutEdgeRange(v).Start).Single(e => g.EdgeHead(e) == 1);
        var a = g.EdgeGeometry(inEdge);
        var b = g.EdgeGeometry(outEdge);
        Assert.Equal(P(25.0, 121.500), a[0]);
        Assert.Equal(a[^1], b[0]);
        Assert.Equal(P(25.0, 121.502), b[^1]);
        Assert.Contains(P(25.001, 121.501), a.Concat(b));
        Assert.Equal(Haversine.PolylineLength(features[0].Coordinates), g.EdgeLength(inEdge) + g.EdgeLength(outEdge), 9);
    }

    [Fact]
    public void Network_without_usable_roads_reports_unsnappable_points()
    {
        var features = new List<RoadFeature> { F(0, "a", OnewayDirection.Forward, P(25.0, 121.5), P(25.0, 121.501)) };
        var s = Snap(features, P(25.0, 121.5005));
        Assert.Equal(-1, s.Points[0].Node);
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void Property_snapped_distance_equals_brute_force_minimum_over_main_component_segments()
    {
        Property.ForAll(5_000, 60, (r, _) =>
        {
            var features = RandomGrid(r, 6, out var mainComponentFeatures);
            var net = NetworkBuilder.Build(features);
            var analysis = NetworkAnalysis.Analyze(net);
            var points = Enumerable.Range(0, 15)
                .Select(i => new SnapRequest($"p{i}", P(25.0 + r.NextDouble() * 0.006 - 0.0005, 121.5 + r.NextDouble() * 0.006 - 0.0005)))
                .ToArray();
            var s = Snapper.Snap(net, analysis, points);

            for (var i = 0; i < points.Length; i++)
            {
                var expected = features
                    .Where(f => IsUsable(net, analysis, f.Index))
                    .Min(f => RefProjection.DistanceToPolyline(points[i].Point, f.Coordinates));
                Assert.Equal(expected, s.Points[i].DistanceM, 6);
            }
            for (var e = 0; e < s.Graph.EdgeCount; e++) Assert.True(s.Graph.EdgeLength(e) > 0);
        });
    }

    [Fact]
    public void Property_split_preserves_total_length_per_feature_and_direction()
    {
        Property.ForAll(6_000, 60, (r, _) =>
        {
            var features = RandomGrid(r, 5, out var unusedCount);
            var net = NetworkBuilder.Build(features);
            var analysis = NetworkAnalysis.Analyze(net);
            var points = Enumerable.Range(0, 20)
                .Select(i => new SnapRequest($"p{i}", P(25.0 + r.NextDouble() * 0.004, 121.5 + r.NextDouble() * 0.004)))
                .ToArray();
            var g = Snapper.Snap(net, analysis, points).Graph;

            foreach (var f in features)
            {
                foreach (var reversed in new[] { false, true })
                {
                    var edges = Enumerable.Range(0, g.EdgeCount).Where(e => g.EdgeFeature(e) == f.Index && g.EdgeIsReversed(e) == reversed).ToList();
                    if (edges.Count == 0) continue;
                    Assert.Equal(Haversine.PolylineLength(f.Coordinates), edges.Sum(g.EdgeLength), 6);
                }
            }
        });
    }

    private static bool IsUsable(RoadNetwork net, NetworkAnalysis a, int featureIndex) =>
        Enumerable.Range(0, net.EdgeCount).Any(e => net.EdgeFeature(e) == featureIndex && a.IsEdgeInLargestComponent(net, e));

    /// <summary>Random grid with random one-ways and a few bent (3-point) roads.</summary>
    internal static List<RoadFeature> RandomGrid(Random r, int size, out int count)
    {
        var list = new List<RoadFeature>();
        const double step = 0.001;
        void Add(GeoPoint a, GeoPoint b)
        {
            var ow = r.NextDouble() switch { < 0.2 => OnewayDirection.Forward, < 0.3 => OnewayDirection.Backward, _ => OnewayDirection.Both };
            var pts = r.NextDouble() < 0.3
                ? new[] { a, P((a.Lat + b.Lat) / 2 + (r.NextDouble() - 0.5) * 0.0003, (a.Lon + b.Lon) / 2 + (r.NextDouble() - 0.5) * 0.0003), b }
                : new[] { a, b };
            var cls = (RoadClass)r.Next(3);
            list.Add(new RoadFeature(list.Count, $"f{list.Count}", pts, ow, cls));
        }
        for (var i = 0; i < size; i++)
            for (var j = 0; j < size; j++)
            {
                var here = P(25.0 + i * step, 121.5 + j * step);
                if (j + 1 < size && r.NextDouble() < 0.9) Add(here, P(25.0 + i * step, 121.5 + (j + 1) * step));
                if (i + 1 < size && r.NextDouble() < 0.9) Add(here, P(25.0 + (i + 1) * step, 121.5 + j * step));
            }
        count = list.Count;
        return list;
    }
}
