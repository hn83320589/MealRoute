using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Network;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Planning;

/// <summary>
/// The road-network travel model for one set of locations: snapping (largest SCC only), the
/// L1 search oracle and the L2 exact oracle, plus path geometry for drawing and export.
/// </summary>
public sealed class TravelModel
{
    private TravelModel(SnappedNetwork snapped, PathSampledOracle search, ExactOracle exact, int[] nodes)
    {
        Snapped = snapped;
        Search = search;
        Exact = exact;
        LocationNodes = nodes;
    }

    public SnappedNetwork Snapped { get; }
    public PathSampledOracle Search { get; }
    public ExactOracle Exact { get; }
    public IReadOnlyList<int> LocationNodes { get; }

    /// <summary>Departure horizon covering every vehicle's working window (plus a margin).</summary>
    public static (double Start, double End) Horizon(ProblemInstance instance) =>
        instance.Vehicles.Count == 0
            ? (36_000, 50_400)
            : (instance.Vehicles.Min(v => v.EarliestStart), Math.Min(instance.Vehicles.Max(v => v.LatestEnd) + 1_800, 2 * 86_400));

    public static TravelModel Build(RoadNetwork network, NetworkAnalysis analysis, SpeedProfile profile,
        IReadOnlyList<GeoPoint> locationPoints, double horizonStart, double horizonEnd, OracleBuildOptions options)
    {
        var requests = locationPoints.Select((p, i) => new SnapRequest($"L{i}", p)).ToArray();
        var snapped = Snapper.Snap(network, analysis, requests);
        var nodes = snapped.Points.Select(p => p.Node).ToArray();
        options.CancellationToken.ThrowIfCancellationRequested();
        var search = PathSampledOracle.Build(snapped.Graph, profile, nodes, horizonStart, horizonEnd, options);
        var exact = new ExactOracle(snapped.Graph, profile, nodes);
        return new TravelModel(snapped, search, exact, nodes);
    }

    /// <summary>Driven path of a route (exact shortest paths at the planned departure times).</summary>
    public IReadOnlyList<GeoPoint> RouteGeometry(ProblemInstance instance, RoutePlan route)
    {
        var points = new List<GeoPoint>();
        if (route.IsEmpty) return points;
        var g = Snapped.Graph;
        void Leg(int from, int to, double t)
        {
            var edges = Exact.PathEdges(from, to, t);
            if (edges.Count == 0)
            {
                if (LocationNodes[from] >= 0) Add(g.NodePosition(LocationNodes[from]));
                return;
            }
            foreach (var e in edges) foreach (var p in g.EdgeGeometry(e)) Add(p);
        }
        void Add(GeoPoint p)
        {
            if (points.Count == 0 || points[^1] != p) points.Add(p);
        }

        var prevLoc = instance.VehicleStartLocation(route.Vehicle);
        var prevDep = route.Departure;
        for (var i = 0; i < route.Visits.Count; i++)
        {
            var loc = instance.StopLocation(route.Visits[i].Stop);
            Leg(prevLoc, loc, prevDep);
            prevLoc = loc;
            prevDep = route.Visits[i].Departure;
        }
        Leg(prevLoc, instance.VehicleEndLocation(route.Vehicle), prevDep);
        return points;
    }
}
