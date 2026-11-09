using System.Collections.Concurrent;
using MealRoute.Core.Network;

namespace MealRoute.Core.Travel;

/// <summary>
/// L2: exact time-dependent shortest path (A*) for the actual departure time, memoised by
/// (from, to, departure). This is the authority for feasibility decisions and reports.
/// Thread-safe.
/// </summary>
public sealed class ExactOracle : ITravelTimeOracle
{
    private readonly RoadNetwork _graph;
    private readonly SpeedProfile _profile;
    private readonly int[] _nodes;
    private readonly ConcurrentBag<TimeDependentDijkstra> _pool = new();
    private readonly ConcurrentDictionary<(int, int, double), LegResult> _memo = new();

    /// <summary>Memo entries kept before the memo is cleared (bounds memory in long sessions).</summary>
    public const int MaxMemoEntries = 2_000_000;

    /// <param name="locationNodes">Graph node per location; -1 marks an unsnapped (unreachable) location.</param>
    public ExactOracle(RoadNetwork graph, SpeedProfile profile, IReadOnlyList<int> locationNodes)
    {
        _graph = graph;
        _profile = profile;
        _nodes = locationNodes.ToArray();
    }

    public int LocationCount => _nodes.Length;
    public RoadNetwork Graph => _graph;
    public SpeedProfile Profile => _profile;
    public int NodeOf(int location) => _nodes[location];

    public double ArrivalTime(int from, int to, double departure) => Leg(from, to, departure).Arrival;

    public LegResult Leg(int from, int to, double departure)
    {
        if (_nodes[from] == _nodes[to]) return new LegResult(departure, 0);
        if (_nodes[from] < 0 || _nodes[to] < 0) return new LegResult(double.PositiveInfinity, double.PositiveInfinity);
        if (_memo.TryGetValue((from, to, departure), out var cached)) return cached;
        var result = Compute(from, to, departure);
        if (_memo.Count >= MaxMemoEntries) _memo.Clear();
        _memo[(from, to, departure)] = result;
        return result;
    }

    /// <summary>Edge ids of the exact shortest path (empty for the same node or unreachable).</summary>
    public IReadOnlyList<int> PathEdges(int from, int to, double departure)
    {
        if (_nodes[from] < 0 || _nodes[to] < 0 || _nodes[from] == _nodes[to]) return Array.Empty<int>();
        var search = Rent();
        try
        {
            search.EarliestArrival(_nodes[from], _nodes[to], departure);
            return search.PathTo(_nodes[to]);
        }
        finally { _pool.Add(search); }
    }

    private LegResult Compute(int from, int to, double departure)
    {
        var search = Rent();
        try
        {
            var arrival = search.EarliestArrival(_nodes[from], _nodes[to], departure);
            if (double.IsPositiveInfinity(arrival)) return new LegResult(arrival, double.PositiveInfinity);
            double distance = 0;
            foreach (var e in search.PathTo(_nodes[to])) distance += _graph.EdgeLength(e);
            return new LegResult(arrival, distance);
        }
        finally { _pool.Add(search); }
    }

    private TimeDependentDijkstra Rent() =>
        _pool.TryTake(out var s) ? s : new TimeDependentDijkstra(_graph, _profile);
}
