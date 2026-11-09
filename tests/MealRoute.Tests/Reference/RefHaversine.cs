namespace MealRoute.Tests.Reference;

/// <summary>Independent haversine implementation (does not use MealRoute.Core).</summary>
internal static class RefHaversine
{
    public const double EarthRadiusM = 6_371_008.8;

    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>Polyline length; coordinates are (lon, lat) pairs like GeoJSON.</summary>
    public static double PolylineLength(IReadOnlyList<(double Lon, double Lat)> pts)
    {
        double sum = 0;
        for (var i = 1; i < pts.Count; i++)
            sum += Distance(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
        return sum;
    }
}
