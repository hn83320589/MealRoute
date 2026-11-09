using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;

namespace MealRoute.Core.Network;

/// <summary>
/// Builds a <see cref="RoadNetwork"/> from road features. Only feature endpoints become nodes;
/// endpoints with bit-identical coordinates are merged. Interior points are geometry only.
/// </summary>
public static class NetworkBuilder
{
    public static RoadNetwork Build(IReadOnlyList<RoadFeature> features)
    {
        var nodeIndex = new Dictionary<GeoPoint, int>();
        var nodes = new List<GeoPoint>();

        int NodeOf(GeoPoint p)
        {
            p = new GeoPoint(p.Lat == 0 ? 0.0 : p.Lat, p.Lon == 0 ? 0.0 : p.Lon);
            if (!nodeIndex.TryGetValue(p, out var id))
            {
                id = nodes.Count;
                nodeIndex.Add(p, id);
                nodes.Add(p);
            }
            return id;
        }

        // Edges in creation order: feature order, forward before backward.
        var raw = new List<(int Tail, int Head, double Length, RoadClass Class, int Feature, bool Reversed)>();
        for (var f = 0; f < features.Count; f++)
        {
            var feature = features[f];
            var a = NodeOf(feature.Coordinates[0]);
            var b = NodeOf(feature.Coordinates[^1]);
            var len = Haversine.PolylineLength(feature.Coordinates);
            if (feature.Oneway != OnewayDirection.Backward) raw.Add((a, b, len, feature.Class, f, false));
            if (feature.Oneway != OnewayDirection.Forward) raw.Add((b, a, len, feature.Class, f, true));
        }

        // Stable counting sort by tail.
        var n = nodes.Count;
        var m = raw.Count;
        var firstOut = new int[n + 1];
        foreach (var e in raw) firstOut[e.Tail + 1]++;
        for (var v = 0; v < n; v++) firstOut[v + 1] += firstOut[v];

        var next = (int[])firstOut.Clone();
        var tail = new int[m];
        var head = new int[m];
        var length = new double[m];
        var cls = new RoadClass[m];
        var feat = new int[m];
        var rev = new bool[m];
        foreach (var e in raw)
        {
            var id = next[e.Tail]++;
            tail[id] = e.Tail;
            head[id] = e.Head;
            length[id] = e.Length;
            cls[id] = e.Class;
            feat[id] = e.Feature;
            rev[id] = e.Reversed;
        }

        return new RoadNetwork(nodes.ToArray(), firstOut, tail, head, length, cls, feat, rev, features);
    }
}
