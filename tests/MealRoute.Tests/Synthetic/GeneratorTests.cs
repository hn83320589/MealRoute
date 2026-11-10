using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Core.Synthetic;

namespace MealRoute.Tests.Synthetic;

public class GeneratorTests
{
    [Fact]
    public void City_is_deterministic_for_a_seed()
    {
        var a = CityGenerator.Generate(new CitySpec(20, Seed: 5)).ToGeoJson();
        var b = CityGenerator.Generate(new CitySpec(20, Seed: 5)).ToGeoJson();
        var c = CityGenerator.Generate(new CitySpec(20, Seed: 6)).ToGeoJson();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void City_geojson_reads_back_cleanly_with_one_ways_classes_and_warnings()
    {
        var city = CityGenerator.Generate(new CitySpec(32, Seed: 1, RuralRoadKm: 10));
        var read = GeoJsonRoadReader.Read(city.ToGeoJson());
        Assert.Empty(read.Issues);
        Assert.Equal(city.Roads.Count, read.Features.Count);
        Assert.Contains(read.Features, f => f.Oneway == OnewayDirection.Forward);
        Assert.Contains(read.Features, f => f.Oneway == OnewayDirection.Backward);
        foreach (var cls in Enum.GetValues<RoadClass>()) Assert.Contains(read.Features, f => f.Class == cls);

        var net = NetworkBuilder.Build(read.Features);
        var analysis = NetworkAnalysis.Analyze(net);
        Assert.InRange(net.NodeCount, 1_000, 1_200);
        Assert.True(analysis.Scc.Sizes[analysis.Scc.Largest] >= 0.9 * net.NodeCount);
        Assert.Contains(analysis.SmallComponents, c => c.FeatureIds.Contains("island-1"));
        Assert.NotEmpty(city.Villages);
    }

    [Fact]
    public void Roster_has_requested_counts_optional_stops_far_stops_and_a_long_day_vehicle()
    {
        var city = CityGenerator.Generate(new CitySpec(32, Seed: 2, RuralRoadKm: 20));
        var (stops, vehicles) = RosterGenerator.Generate(new RosterSpec(60, 5, Seed: 2, FarStops: 3, RemoteStops: 4), city);
        Assert.Equal(60, stops.Length);
        Assert.Equal(5, vehicles.Length);
        Assert.Contains(stops, s => !s.Mandatory);
        Assert.All(stops, s => Assert.InRange(s.Priority, 1, 5));
        Assert.All(stops, s => Assert.True(s.WindowEnd > s.WindowStart));
        Assert.Equal(stops.Length, stops.Select(s => s.Id).Distinct().Count());
        Assert.True(vehicles[0].LatestEnd - vehicles[0].EarliestStart > vehicles[1].LatestEnd - vehicles[1].EarliestStart);

        var net = NetworkBuilder.Build(city.Roads);
        var snapped = Snapper.Snap(net, NetworkAnalysis.Analyze(net), stops.Select(s => new SnapRequest(s.Id, s.Location)).ToArray());
        Assert.True(snapped.Warnings.Count >= 3, $"{snapped.Warnings.Count} warnings");
    }
}
