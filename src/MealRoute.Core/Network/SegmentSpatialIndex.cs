using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;

namespace MealRoute.Core.Network;

/// <summary>Nearest-segment hit: feature (list index), segment index within the feature, projection.</summary>
public readonly record struct NearestSegment(int Feature, int Segment, SegmentProjection Projection);

/// <summary>
/// Uniform grid over polyline segments of selected features. The grid uses one global
/// equirectangular plane only for bucketing; distances use the per-segment projection of
/// <see cref="PolylineMath.ProjectOntoSegment"/>.
/// </summary>
public sealed class SegmentSpatialIndex
{
    private const double MetresPerDegree = Haversine.EarthRadiusM * Math.PI / 180.0;
    private readonly IReadOnlyList<RoadFeature> _features;
    private readonly Dictionary<long, List<(int Feature, int Segment)>> _cells = new();
    private readonly double _originLat, _originLon, _kx, _cellSize;
    private readonly int _minCx, _maxCx, _minCy, _maxCy;
    private readonly bool _empty;

    public SegmentSpatialIndex(IReadOnlyList<RoadFeature> features, Func<int, bool> include, double cellSizeM = 100)
    {
        _features = features;
        _cellSize = cellSizeM;
        var included = Enumerable.Range(0, features.Count).Where(include).ToArray();
        _empty = included.Length == 0;
        if (_empty) return;

        double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var f in included)
            foreach (var p in features[f].Coordinates)
            {
                minLat = Math.Min(minLat, p.Lat); maxLat = Math.Max(maxLat, p.Lat);
                minLon = Math.Min(minLon, p.Lon); maxLon = Math.Max(maxLon, p.Lon);
            }
        _originLat = (minLat + maxLat) / 2;
        _originLon = (minLon + maxLon) / 2;
        _kx = MetresPerDegree * Math.Cos(_originLat * Math.PI / 180.0);

        _minCx = _minCy = int.MaxValue;
        _maxCx = _maxCy = int.MinValue;
        foreach (var f in included)
        {
            var c = features[f].Coordinates;
            for (var s = 0; s + 1 < c.Length; s++)
            {
                var (x0, y0) = Cell(c[s]);
                var (x1, y1) = Cell(c[s + 1]);
                for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                    for (var y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
                    {
                        var key = Key(x, y);
                        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
                        list.Add((f, s));
                        _minCx = Math.Min(_minCx, x); _maxCx = Math.Max(_maxCx, x);
                        _minCy = Math.Min(_minCy, y); _maxCy = Math.Max(_maxCy, y);
                    }
            }
        }
    }

    private (int X, int Y) Cell(GeoPoint p) =>
        ((int)Math.Floor((p.Lon - _originLon) * _kx / _cellSize), (int)Math.Floor((p.Lat - _originLat) * MetresPerDegree / _cellSize));

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// <summary>Nearest segment by planar distance; ties go to the smaller (feature, segment). Null if the index is empty.</summary>
    public NearestSegment? FindNearest(GeoPoint p)
    {
        if (_empty) return null;
        var (cx, cy) = Cell(p);
        NearestSegment? best = null;
        var seen = new HashSet<(int, int)>();
        var maxRing = Math.Max(Math.Max(Math.Abs(cx - _minCx), Math.Abs(cx - _maxCx)), Math.Max(Math.Abs(cy - _minCy), Math.Abs(cy - _maxCy)));

        for (var ring = 0; ring <= maxRing; ring++)
        {
            for (var x = cx - ring; x <= cx + ring; x++)
                for (var y = cy - ring; y <= cy + ring; y++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != ring) continue;
                    if (!_cells.TryGetValue(Key(x, y), out var list)) continue;
                    foreach (var (f, s) in list)
                    {
                        if (!seen.Add((f, s))) continue;
                        var c = _features[f].Coordinates;
                        var proj = PolylineMath.ProjectOntoSegment(p, c[s], c[s + 1]);
                        if (best is null || IsBetter(proj.DistanceM, f, s, best.Value))
                            best = new NearestSegment(f, s, proj);
                    }
                }

            // Any segment not yet seen lies entirely outside rings 0..ring, i.e. at least
            // ring * cellSize away (minus a margin for the projection difference).
            if (best is not null && best.Value.Projection.DistanceM < ring * _cellSize * 0.98) break;
        }
        return best;
    }

    private static bool IsBetter(double d, int f, int s, NearestSegment cur)
    {
        if (d != cur.Projection.DistanceM) return d < cur.Projection.DistanceM;
        return f != cur.Feature ? f < cur.Feature : s < cur.Segment;
    }
}
