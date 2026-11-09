using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;

namespace MealRoute.Core.Network;

/// <summary>
/// Immutable directed road graph in CSR form. Edge ids are ordered by tail node, so the
/// out-edges of node v are the contiguous id range <see cref="OutEdgeRange"/>.
/// Each edge covers the part [StartOffset, EndOffset] (metres along the feature's coordinate
/// order) of its source feature; split edges produced by snapping cover a sub-range.
/// </summary>
public sealed class RoadNetwork
{
    internal readonly record struct RawEdge(int Tail, int Head, double Length, RoadClass Class, int Feature, bool Reversed, double StartOffset, double EndOffset);

    private RoadNetwork(GeoPoint[] nodes, IReadOnlyList<RoadFeature> features, int edgeCount)
    {
        Nodes = nodes;
        Features = features;
        FirstOut = new int[nodes.Length + 1];
        Tail = new int[edgeCount];
        Head = new int[edgeCount];
        Length = new double[edgeCount];
        Class = new RoadClass[edgeCount];
        Feature = new int[edgeCount];
        Reversed = new bool[edgeCount];
        StartOffset = new double[edgeCount];
        EndOffset = new double[edgeCount];
    }

    /// <summary>Builds the CSR with a stable counting sort by tail (creation order kept per tail).</summary>
    internal static RoadNetwork Create(GeoPoint[] nodes, IReadOnlyList<RawEdge> edges, IReadOnlyList<RoadFeature> features)
    {
        var net = new RoadNetwork(nodes, features, edges.Count);
        var firstOut = net.FirstOut;
        foreach (var e in edges) firstOut[e.Tail + 1]++;
        for (var v = 0; v < nodes.Length; v++) firstOut[v + 1] += firstOut[v];

        var next = (int[])firstOut.Clone();
        foreach (var e in edges)
        {
            var id = next[e.Tail]++;
            net.Tail[id] = e.Tail;
            net.Head[id] = e.Head;
            net.Length[id] = e.Length;
            net.Class[id] = e.Class;
            net.Feature[id] = e.Feature;
            net.Reversed[id] = e.Reversed;
            net.StartOffset[id] = e.StartOffset;
            net.EndOffset[id] = e.EndOffset;
        }
        return net;
    }

    internal GeoPoint[] Nodes { get; }
    internal int[] FirstOut { get; }
    internal int[] Tail { get; }
    internal int[] Head { get; }
    internal double[] Length { get; }
    internal RoadClass[] Class { get; }
    internal int[] Feature { get; }
    internal bool[] Reversed { get; }
    internal double[] StartOffset { get; }
    internal double[] EndOffset { get; }

    public IReadOnlyList<RoadFeature> Features { get; }

    public int NodeCount => Nodes.Length;
    public int EdgeCount => Head.Length;

    public GeoPoint NodePosition(int node) => Nodes[node];
    public (int Start, int End) OutEdgeRange(int node) => (FirstOut[node], FirstOut[node + 1]);
    public int EdgeTail(int edge) => Tail[edge];
    public int EdgeHead(int edge) => Head[edge];
    public double EdgeLength(int edge) => Length[edge];
    public RoadClass EdgeClass(int edge) => Class[edge];
    /// <summary>Index into <see cref="Features"/>.</summary>
    public int EdgeFeature(int edge) => Feature[edge];
    /// <summary>True when the edge runs against the feature's coordinate order.</summary>
    public bool EdgeIsReversed(int edge) => Reversed[edge];
    /// <summary>Covered range along the feature in coordinate order (metres), StartOffset &lt; EndOffset.</summary>
    public (double Start, double End) EdgeOffsets(int edge) => (StartOffset[edge], EndOffset[edge]);

    /// <summary>
    /// Edge geometry in travel direction: tail position, the feature's interior vertices strictly
    /// inside the covered range, head position.
    /// </summary>
    public GeoPoint[] EdgeGeometry(int edge)
    {
        var coords = Features[Feature[edge]].Coordinates;
        var cum = PolylineMath.Cumulative(coords);
        var s = StartOffset[edge];
        var e = EndOffset[edge];
        var pts = new List<GeoPoint> { Reversed[edge] ? Nodes[Head[edge]] : Nodes[Tail[edge]] };
        for (var k = 1; k < coords.Length - 1; k++)
            if (cum[k] > s && cum[k] < e) pts.Add(coords[k]);
        pts.Add(Reversed[edge] ? Nodes[Tail[edge]] : Nodes[Head[edge]]);
        if (Reversed[edge]) pts.Reverse();
        return pts.ToArray();
    }
}
