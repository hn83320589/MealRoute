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
        var raw = new List<RoadNetwork.RawEdge>(features.Count * 2);
        for (var f = 0; f < features.Count; f++)
        {
            var feature = features[f];
            var a = NodeOf(feature.Coordinates[0]);
            var b = NodeOf(feature.Coordinates[^1]);
            var len = Haversine.PolylineLength(feature.Coordinates);
            if (feature.Oneway != OnewayDirection.Backward) raw.Add(new(a, b, len, feature.Class, f, false, 0, len));
            if (feature.Oneway != OnewayDirection.Forward) raw.Add(new(b, a, len, feature.Class, f, true, 0, len));
        }

        return RoadNetwork.Create(nodes.ToArray(), raw, features);
    }
}
