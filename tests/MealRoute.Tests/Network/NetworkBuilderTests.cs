using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Tests.Reference;
using static MealRoute.Tests.GeoJson.GeoJsonRoadReaderTests;

namespace MealRoute.Tests.Network;

public class NetworkBuilderTests
{
    private static RoadNetwork Build(params string[] features)
    {
        var r = GeoJsonRoadReader.Read(Fc(features));
        Assert.DoesNotContain(r.Issues, i => i.Severity == IssueSeverity.Error);
        return NetworkBuilder.Build(r.Features);
    }

    private static List<(int From, int To)> EdgePairs(RoadNetwork n) =>
        Enumerable.Range(0, n.EdgeCount).Select(e => (n.EdgeTail(e), n.EdgeHead(e))).ToList();

    [Fact]
    public void Two_way_road_creates_two_opposite_edges()
    {
        var n = Build(Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0]]"));
        Assert.Equal(2, n.NodeCount);
        Assert.Equal(2, n.EdgeCount);
        Assert.Contains((0, 1), EdgePairs(n));
        Assert.Contains((1, 0), EdgePairs(n));
    }

    [Fact]
    public void Forward_oneway_creates_single_edge_along_coordinate_order()
    {
        var n = Build(Line("\"id\":\"a\",\"oneway\":\"yes\"", "[[121.5,25.0],[121.501,25.0]]"));
        var e = Assert.Single(Enumerable.Range(0, n.EdgeCount));
        Assert.Equal(121.5, n.NodePosition(n.EdgeTail(e)).Lon);
        Assert.Equal(121.501, n.NodePosition(n.EdgeHead(e)).Lon);
    }

    [Fact]
    public void Reverse_oneway_creates_single_edge_against_coordinate_order_with_reversed_geometry()
    {
        var n = Build(Line("\"id\":\"a\",\"oneway\":\"-1\"", "[[121.5,25.0],[121.5005,25.0002],[121.501,25.0]]"));
        var e = Assert.Single(Enumerable.Range(0, n.EdgeCount));
        Assert.Equal(121.501, n.NodePosition(n.EdgeTail(e)).Lon);
        Assert.Equal(121.5, n.NodePosition(n.EdgeHead(e)).Lon);
        var g = n.EdgeGeometry(e);
        Assert.Equal(new[] { 121.501, 121.5005, 121.5 }, g.Select(p => p.Lon));
    }

    [Fact]
    public void Identical_endpoints_are_merged_into_one_node()
    {
        var n = Build(
            Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0]]"),
            Line("\"id\":\"b\"", "[[121.501,25.0],[121.502,25.0]]"),
            Line("\"id\":\"c\"", "[[121.501,25.0],[121.501,25.001]]"));
        Assert.Equal(4, n.NodeCount);
        Assert.Equal(6, n.EdgeCount);
    }

    [Fact]
    public void Nearly_identical_endpoints_are_not_merged()
    {
        var n = Build(
            Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0]]"),
            Line("\"id\":\"b\"", "[[121.5010000001,25.0],[121.502,25.0]]"));
        Assert.Equal(4, n.NodeCount);
    }

    [Fact]
    public void Interior_points_do_not_become_nodes_even_if_shared()
    {
        var n = Build(
            Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0],[121.502,25.0]]"),
            Line("\"id\":\"b\"", "[[121.501,25.0],[121.501,25.001]]"));
        Assert.Equal(4, n.NodeCount);
    }

    [Fact]
    public void Multi_point_length_is_sum_of_haversine_segments()
    {
        var coords = new[] { (121.5, 25.0), (121.5004, 25.0003), (121.5011, 25.0001), (121.502, 25.0009) };
        var json = "[" + string.Join(",", coords.Select(c => FormattableString.Invariant($"[{c.Item1},{c.Item2}]"))) + "]";
        var n = Build(Line("\"id\":\"a\"", json));
        var expected = RefHaversine.PolylineLength(coords);
        for (var e = 0; e < n.EdgeCount; e++)
            Assert.Equal(expected, n.EdgeLength(e), 9);
    }

    [Fact]
    public void Out_edges_are_contiguous_per_tail_node()
    {
        var n = Build(
            Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0]]"),
            Line("\"id\":\"b\",\"oneway\":true", "[[121.501,25.0],[121.502,25.0]]"));
        var total = 0;
        for (var v = 0; v < n.NodeCount; v++)
        {
            var (start, end) = n.OutEdgeRange(v);
            for (var e = start; e < end; e++) Assert.Equal(v, n.EdgeTail(e));
            total += end - start;
        }
        Assert.Equal(n.EdgeCount, total);
    }

    [Fact]
    public void Edge_keeps_class_and_feature_reference()
    {
        var n = Build(Line("\"id\":\"R1\",\"class\":\"arterial\",\"oneway\":true", "[[121.5,25.0],[121.501,25.0]]"));
        Assert.Equal(RoadClass.Arterial, n.EdgeClass(0));
        Assert.Equal("R1", n.Features[n.EdgeFeature(0)].Id);
    }

    [Fact]
    public void Build_is_deterministic()
    {
        var f = new[]
        {
            Line("\"id\":\"a\"", "[[121.5,25.0],[121.501,25.0]]"),
            Line("\"id\":\"b\",\"oneway\":\"-1\"", "[[121.501,25.0],[121.502,25.0]]"),
        };
        Assert.Equal(EdgePairs(Build(f)), EdgePairs(Build(f)));
    }

    [Fact]
    public void Negative_zero_and_zero_coordinates_are_the_same_node()
    {
        var a = new RoadFeature(0, "a", new[] { new GeoPoint(0.0, 0.0), new GeoPoint(0.0, 0.001) }, OnewayDirection.Both, RoadClass.Residential);
        var b = new RoadFeature(1, "b", new[] { new GeoPoint(-0.0, -0.0), new GeoPoint(0.001, 0.0) }, OnewayDirection.Both, RoadClass.Residential);
        Assert.Equal(3, NetworkBuilder.Build(new[] { a, b }).NodeCount);
    }
}
