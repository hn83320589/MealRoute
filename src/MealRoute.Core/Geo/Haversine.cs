namespace MealRoute.Core.Geo;

/// <summary>Great-circle distances (spec: haversine, R = 6,371,008.8 m).</summary>
public static class Haversine
{
    public const double EarthRadiusM = 6_371_008.8;
    private const double DegToRad = Math.PI / 180.0;

    public static double Distance(GeoPoint a, GeoPoint b)
    {
        var dLat = (b.Lat - a.Lat) * DegToRad;
        var dLon = (b.Lon - a.Lon) * DegToRad;
        var sinLat = Math.Sin(dLat * 0.5);
        var sinLon = Math.Sin(dLon * 0.5);
        var h = sinLat * sinLat + Math.Cos(a.Lat * DegToRad) * Math.Cos(b.Lat * DegToRad) * sinLon * sinLon;
        return 2.0 * EarthRadiusM * Math.Asin(Math.Sqrt(Math.Min(1.0, h)));
    }

    public static double PolylineLength(ReadOnlySpan<GeoPoint> points)
    {
        double sum = 0;
        for (var i = 1; i < points.Length; i++) sum += Distance(points[i - 1], points[i]);
        return sum;
    }
}
