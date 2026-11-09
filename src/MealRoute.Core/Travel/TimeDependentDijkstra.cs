using MealRoute.Core.Geo;
using MealRoute.Core.Network;

namespace MealRoute.Core.Travel;

/// <summary>
/// Earliest-arrival time-dependent Dijkstra (label setting is exact because the speed model is
/// FIFO) plus a point-to-point A* variant with an admissible bound (straight-line distance at
/// the profile's maximum speed). Holds reusable work arrays: not thread-safe, use one instance
/// per thread.
/// </summary>
public sealed class TimeDependentDijkstra
{
    private readonly RoadNetwork _g;
    private readonly SpeedProfile _profile;
    private readonly double[] _arrival;
    private readonly int[] _parentEdge;
    private readonly int[] _stamp;
    private readonly bool[] _settled;
    private readonly PriorityQueue<int, (double Key, int Node)> _queue = new();
    private int _current;

    public TimeDependentDijkstra(RoadNetwork graph, SpeedProfile profile)
    {
        _g = graph;
        _profile = profile;
        var n = graph.NodeCount;
        _arrival = new double[n];
        _parentEdge = new int[n];
        _stamp = new int[n];
        _settled = new bool[n];
    }

    public RoadNetwork Graph => _g;
    public SpeedProfile Profile => _profile;

    /// <summary>Arrival time at node from the last run (+∞ if not reached/settled).</summary>
    public double ArrivalAt(int node) => _stamp[node] == _current ? _arrival[node] : double.PositiveInfinity;

    /// <summary>Settled-or-labelled flag for the last run.</summary>
    public bool IsSettled(int node) => _stamp[node] == _current && _settled[node];

    public void Run(int source, double departure) => Run(new[] { (source, departure) });

    /// <summary>
    /// One-to-all from (possibly several) initial labels. If <paramref name="stopWhenSettled"/> is
    /// non-empty the search stops once all those nodes are settled; their labels are then final.
    /// </summary>
    public void Run(ReadOnlySpan<(int Node, double Time)> initial, ReadOnlySpan<int> stopWhenSettled = default)
    {
        Reset();
        foreach (var (node, time) in initial) Relax(node, time, -1, 0);

        var remaining = 0;
        HashSet<int>? targets = null;
        if (!stopWhenSettled.IsEmpty)
        {
            targets = new HashSet<int>(stopWhenSettled.ToArray());
            remaining = targets.Count;
        }

        while (_queue.TryDequeue(out var u, out var key))
        {
            if (_settled[u] || key.Key > _arrival[u]) continue;
            _settled[u] = true;
            if (targets is not null && targets.Contains(u) && --remaining == 0) return;
            Expand(u, null, 0);
        }
    }

    /// <summary>Point-to-point earliest arrival (A*). Afterwards <see cref="PathTo"/> gives the path.</summary>
    public double EarliestArrival(int source, int target, double departure)
    {
        Reset();
        var targetPos = _g.NodePosition(target);
        var invV = 1.0 / _profile.MaxSpeedMps;
        Relax(source, departure, -1, Heuristic(source, targetPos, invV));
        while (_queue.TryDequeue(out var u, out var key))
        {
            if (_settled[u]) continue;
            _settled[u] = true;
            if (u == target) return _arrival[u];
            Expand(u, targetPos, invV);
        }
        return double.PositiveInfinity;
    }

    /// <summary>Edge ids from the search source to <paramref name="target"/> (empty if unreachable or the source itself).</summary>
    public IReadOnlyList<int> PathTo(int target)
    {
        var path = new List<int>();
        if (_stamp[target] != _current) return path;
        for (var e = _parentEdge[target]; e >= 0; e = _parentEdge[_g.Tail[e]])
            path.Add(e);
        path.Reverse();
        return path;
    }

    private void Expand(int u, GeoPoint? targetPos, double invV)
    {
        var t = _arrival[u];
        var head = _g.Head;
        var len = _g.Length;
        var cls = _g.Class;
        for (int e = _g.FirstOut[u], end = _g.FirstOut[u + 1]; e < end; e++)
        {
            var v = head[e];
            if (_stamp[v] == _current && _settled[v]) continue;
            var arr = _profile.ArrivalTime(cls[e], len[e], t);
            var h = targetPos is { } tp ? Heuristic(v, tp, invV) : 0;
            Relax(v, arr, e, h);
        }
    }

    private double Heuristic(int node, GeoPoint target, double invV) =>
        Haversine.Distance(_g.NodePosition(node), target) * invV;

    private void Relax(int v, double time, int viaEdge, double heuristic)
    {
        if (_stamp[v] != _current)
        {
            _stamp[v] = _current;
            _settled[v] = false;
            _arrival[v] = double.PositiveInfinity;
            _parentEdge[v] = -1;
        }
        if (time < _arrival[v])
        {
            _arrival[v] = time;
            _parentEdge[v] = viaEdge;
            _queue.Enqueue(v, (time + heuristic, v));
        }
    }

    private void Reset()
    {
        _queue.Clear();
        if (++_current == int.MaxValue)
        {
            Array.Clear(_stamp);
            _current = 1;
        }
    }
}
