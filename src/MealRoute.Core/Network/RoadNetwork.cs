using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;

namespace MealRoute.Core.Network;

/// <summary>
/// Immutable directed road graph in CSR form. Edge ids are ordered by tail node, so the
/// out-edges of node v are the contiguous id range <see cref="OutEdgeRange"/>.
/// </summary>
public sealed class RoadNetwork
{
    internal RoadNetwork(
        GeoPoint[] nodes,
        int[] firstOut,
        int[] tail,
        int[] head,
        double[] length,
        RoadClass[] roadClass,
        int[] feature,
        bool[] reversed,
        IReadOnlyList<RoadFeature> features)
    {
        Nodes = nodes;
        FirstOut = firstOut;
        Tail = tail;
        Head = head;
        Length = length;
        Class = roadClass;
        Feature = feature;
        Reversed = reversed;
        Features = features;
    }

    internal GeoPoint[] Nodes { get; }
    internal int[] FirstOut { get; }
    internal int[] Tail { get; }
    internal int[] Head { get; }
    internal double[] Length { get; }
    internal RoadClass[] Class { get; }
    internal int[] Feature { get; }
    internal bool[] Reversed { get; }

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

    /// <summary>Edge geometry in travel direction (a copy).</summary>
    public GeoPoint[] EdgeGeometry(int edge)
    {
        var coords = Features[Feature[edge]].Coordinates;
        var copy = (GeoPoint[])coords.Clone();
        if (Reversed[edge]) Array.Reverse(copy);
        return copy;
    }
}
