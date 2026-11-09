namespace MealRoute.Core.Geo;

/// <summary>Result of projecting a point onto one polyline segment.</summary>
/// <param name="T">Fraction along the segment in [0, 1].</param>
/// <param name="DistanceM">Planar distance from the point to the foot of the projection.</param>
public readonly record struct SegmentProjection(double T, double DistanceM, GeoPoint Foot);

public static class PolylineMath
{
    private const double MetresPerDegree = Haversine.EarthRadiusM * Math.PI / 180.0;

    /// <summary>Prefix haversine lengths: cum[0] = 0, cum[^1] = total (same summation order as <see cref="Haversine.PolylineLength"/>).</summary>
    public static double[] Cumulative(ReadOnlySpan<GeoPoint> points)
    {
        var cum = new double[points.Length];
        for (var i = 1; i < points.Length; i++) cum[i] = cum[i - 1] + Haversine.Distance(points[i - 1], points[i]);
        return cum;
    }

    /// <summary>
    /// Projects <paramref name="p"/> onto segment a-b in an equidistant (equirectangular) plane
    /// centred at the segment midpoint, as the spec prescribes.
    /// </summary>
    public static SegmentProjection ProjectOntoSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var midLat = (a.Lat + b.Lat) * 0.5;
        var midLon = (a.Lon + b.Lon) * 0.5;
        var kx = MetresPerDegree * Math.Cos(midLat * Math.PI / 180.0);
        const double ky = MetresPerDegree;

        double ax = (a.Lon - midLon) * kx, ay = (a.Lat - midLat) * ky;
        double bx = (b.Lon - midLon) * kx, by = (b.Lat - midLat) * ky;
        double px = (p.Lon - midLon) * kx, py = (p.Lat - midLat) * ky;
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0.0, 1.0);
        double fx = ax + t * dx - px, fy = ay + t * dy - py;
        var foot = t switch
        {
            0 => a,
            1 => b,
            _ => new GeoPoint(a.Lat + t * (b.Lat - a.Lat), a.Lon + t * (b.Lon - a.Lon)),
        };
        return new SegmentProjection(t, Math.Sqrt(fx * fx + fy * fy), foot);
    }
}
