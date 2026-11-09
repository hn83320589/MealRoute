using MealRoute.Core.Geo;

namespace MealRoute.Tests.Reference;

/// <summary>
/// Independent point-to-polyline distance per the spec: each polyline segment is projected to an
/// equidistant plane centred at the segment midpoint; distance is measured in that plane.
/// </summary>
internal static class RefProjection
{
    public static double DistanceToPolyline(GeoPoint p, IReadOnlyList<GeoPoint> line)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < line.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
        return best;
    }

    public static double DistanceToSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var midLat = (a.Lat + b.Lat) / 2;
        var midLon = (a.Lon + b.Lon) / 2;
        (double X, double Y) ToPlane(GeoPoint q)
        {
            var k = RefHaversine.EarthRadiusM * Math.PI / 180;
            return ((q.Lon - midLon) * k * Math.Cos(midLat * Math.PI / 180), (q.Lat - midLat) * k);
        }
        var (ax, ay) = ToPlane(a);
        var (bx, by) = ToPlane(b);
        var (px, py) = ToPlane(p);
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double fx = ax + t * dx - px, fy = ay + t * dy - py;
        return Math.Sqrt(fx * fx + fy * fy);
    }
}
