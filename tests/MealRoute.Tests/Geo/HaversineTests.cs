using MealRoute.Core.Geo;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Geo;

public class HaversineTests
{
    [Fact]
    public void One_degree_of_longitude_on_equator_matches_earth_radius_formula()
    {
        var d = Haversine.Distance(new GeoPoint(0, 0), new GeoPoint(0, 1));
        Assert.Equal(6_371_008.8 * Math.PI / 180.0, d, 6);
    }

    [Fact]
    public void Distance_is_zero_for_identical_points()
        => Assert.Equal(0.0, Haversine.Distance(new GeoPoint(25.03, 121.56), new GeoPoint(25.03, 121.56)));

    [Fact]
    public void Matches_reference_on_random_points()
    {
        Property.ForAll(1000, 200, (r, _) =>
        {
            double lat1 = r.NextDouble() * 170 - 85, lon1 = r.NextDouble() * 360 - 180;
            double lat2 = lat1 + (r.NextDouble() - 0.5), lon2 = lon1 + (r.NextDouble() - 0.5);
            var expected = RefHaversine.Distance(lat1, lon1, lat2, lon2);
            var actual = Haversine.Distance(new GeoPoint(lat1, lon1), new GeoPoint(lat2, lon2));
            Assert.Equal(expected, actual, 1e-6);
        });
    }

    [Fact]
    public void Polyline_length_is_sum_of_segment_distances()
    {
        var pts = new[] { new GeoPoint(25.0, 121.5), new GeoPoint(25.001, 121.5), new GeoPoint(25.001, 121.502) };
        var expected = RefHaversine.PolylineLength(new[] { (121.5, 25.0), (121.5, 25.001), (121.502, 25.001) });
        Assert.Equal(expected, Haversine.PolylineLength(pts), 9);
    }
}
