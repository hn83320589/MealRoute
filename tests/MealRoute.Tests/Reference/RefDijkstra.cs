namespace MealRoute.Tests.Reference;

/// <summary>A plain edge list graph for the references: (from, to, class, length).</summary>
internal sealed record RefGraph(int NodeCount, IReadOnlyList<(int From, int To, int Class, double Length)> Edges);

/// <summary>Independent O(V²) Dijkstra implementations (static and time-dependent).</summary>
internal static class RefDijkstra
{
    /// <summary>Static shortest distances (metres) from source.</summary>
    public static double[] StaticDistances(RefGraph g, int source)
    {
        var dist = Enumerable.Repeat(double.PositiveInfinity, g.NodeCount).ToArray();
        var done = new bool[g.NodeCount];
        dist[source] = 0;
        for (var iter = 0; iter < g.NodeCount; iter++)
        {
            var u = -1;
            for (var v = 0; v < g.NodeCount; v++)
                if (!done[v] && (u == -1 || dist[v] < dist[u])) u = v;
            if (u == -1 || double.IsPositiveInfinity(dist[u])) break;
            done[u] = true;
            foreach (var e in g.Edges)
                if (e.From == u && dist[u] + e.Length < dist[e.To]) dist[e.To] = dist[u] + e.Length;
        }
        return dist;
    }

    /// <summary>
    /// Time-dependent earliest arrival from source at departure time, every edge integrated with
    /// <see cref="RefTdIntegrator"/>. Also returns, per node, the edge sequence of the best path so
    /// tests can integrate the path independently.
    /// </summary>
    public static (double[] Arrival, List<int>?[] PathEdges) TimeDependent(RefGraph g, double[][] speeds, int source, double departure)
    {
        var arr = Enumerable.Repeat(double.PositiveInfinity, g.NodeCount).ToArray();
        var pred = Enumerable.Repeat(-1, g.NodeCount).ToArray();
        var done = new bool[g.NodeCount];
        arr[source] = departure;
        for (var iter = 0; iter < g.NodeCount; iter++)
        {
            var u = -1;
            for (var v = 0; v < g.NodeCount; v++)
                if (!done[v] && (u == -1 || arr[v] < arr[u])) u = v;
            if (u == -1 || double.IsPositiveInfinity(arr[u])) break;
            done[u] = true;
            for (var ei = 0; ei < g.Edges.Count; ei++)
            {
                var e = g.Edges[ei];
                if (e.From != u) continue;
                var t = RefTdIntegrator.Arrival(speeds, e.Class, e.Length, arr[u]);
                if (t < arr[e.To]) { arr[e.To] = t; pred[e.To] = ei; }
            }
        }

        var paths = new List<int>?[g.NodeCount];
        for (var v = 0; v < g.NodeCount; v++)
        {
            if (double.IsPositiveInfinity(arr[v])) continue;
            var list = new List<int>();
            for (var x = v; x != source; x = g.Edges[pred[x]].From) list.Add(pred[x]);
            list.Reverse();
            paths[v] = list;
        }
        return (arr, paths);
    }
}
