using System.Globalization;
using MealRoute.Core.Geo;

namespace MealRoute.Core.Network;

public sealed record SnapRequest(string Id, GeoPoint Point);

/// <param name="FeatureIndex">Index into the network's feature list, or -1 when unsnappable.</param>
/// <param name="OffsetM">Distance along the feature (coordinate order) of the snapped position.</param>
/// <param name="Node">Node in <see cref="SnappedNetwork.Graph"/>, or -1 when unsnappable.</param>
public sealed record SnappedPoint(
    string Id, GeoPoint Original, GeoPoint Projected, double DistanceM,
    int FeatureIndex, double OffsetM, int Node, bool IsFar)
{
    public bool IsSnapped => Node >= 0;
}

public sealed record SnapWarning(string PointId, string Message);

/// <summary>Road network with virtual nodes inserted at snapped positions.</summary>
public sealed record SnappedNetwork(RoadNetwork Graph, IReadOnlyList<SnappedPoint> Points, IReadOnlyList<SnapWarning> Warnings);

/// <summary>
/// Snaps points to the nearest road of the largest strongly connected component and splits that
/// road at the projection by inserting a virtual node. Two-way roads get the node on both directed
/// edges; one-way roads only along the driving direction.
/// </summary>
public static class Snapper
{
    public const double FarThresholdM = 150.0;
    /// <summary>Projections closer than this to an endpoint (or to another split point) reuse it.</summary>
    public const double MergeToleranceM = 1.0;

    public static SnappedNetwork Snap(RoadNetwork network, NetworkAnalysis analysis, IReadOnlyList<SnapRequest> requests)
    {
        var usable = UsableFeatures(network, analysis);
        var index = new SegmentSpatialIndex(network.Features, f => usable[f]);
        var cumByFeature = new Dictionary<int, double[]>();
        double[] Cum(int f) => cumByFeature.TryGetValue(f, out var c) ? c : cumByFeature[f] = PolylineMath.Cumulative(network.Features[f].Coordinates);

        var nodes = new List<GeoPoint>(network.Nodes);
        var points = new SnappedPoint[requests.Count];
        var warnings = new List<(int Request, SnapWarning Warning)>();
        var endNodes = FeatureEndNodes(network);
        // per feature: sorted split offsets -> virtual node
        var splits = new SortedDictionary<int, List<(double Offset, int Node)>>();
        var pending = new List<(int Request, int Feature, double Offset, GeoPoint Foot, double Dist)>();

        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            var hit = index.FindNearest(req.Point);
            if (hit is null)
            {
                points[i] = new SnappedPoint(req.Id, req.Point, req.Point, double.PositiveInfinity, -1, 0, -1, true);
                warnings.Add((i, new SnapWarning(req.Id, "找不到可吸附的路段（路網最大強連通分量為空）。")));
                continue;
            }

            var h = hit.Value;
            var cum = Cum(h.Feature);
            var segLen = cum[h.Segment + 1] - cum[h.Segment];
            var offset = cum[h.Segment] + h.Projection.T * segLen;
            pending.Add((i, h.Feature, offset, h.Projection.Foot, h.Projection.DistanceM));
        }

        // Resolve endpoint reuse and merge near-identical splits, in (feature, offset, request) order.
        foreach (var p in pending.OrderBy(p => p.Feature).ThenBy(p => p.Offset).ThenBy(p => p.Request))
        {
            var req = requests[p.Request];
            var total = Cum(p.Feature)[^1];
            var (startNode, endNode) = endNodes[p.Feature];
            int node;
            double offset = p.Offset;
            GeoPoint pos;
            if (p.Offset < MergeToleranceM) { node = startNode; offset = 0; pos = network.Nodes[node]; }
            else if (total - p.Offset < MergeToleranceM) { node = endNode; offset = total; pos = network.Nodes[node]; }
            else
            {
                if (!splits.TryGetValue(p.Feature, out var list)) splits[p.Feature] = list = new();
                if (list.Count > 0 && p.Offset - list[^1].Offset < MergeToleranceM)
                {
                    (offset, node) = list[^1];
                    pos = nodes[node];
                }
                else
                {
                    node = nodes.Count;
                    nodes.Add(p.Foot);
                    list.Add((p.Offset, node));
                    pos = p.Foot;
                }
            }

            var far = p.Dist > FarThresholdM;
            points[p.Request] = new SnappedPoint(req.Id, req.Point, pos, p.Dist, p.Feature, offset, node, far);
            if (far)
            {
                warnings.Add((p.Request, new SnapWarning(req.Id,
                    string.Create(CultureInfo.InvariantCulture, $"距離最近路段 {p.Dist:F0} 公尺，超過 {FarThresholdM:F0} 公尺。"))));
            }
        }

        var graph = splits.Count == 0 && nodes.Count == network.NodeCount
            ? network
            : RoadNetwork.Create(nodes.ToArray(), SplitEdges(network, splits), network.Features);

        var ordered = warnings.OrderBy(w => w.Request).Select(w => w.Warning).ToArray();
        return new SnappedNetwork(graph, points, ordered);
    }

    private static bool[] UsableFeatures(RoadNetwork network, NetworkAnalysis analysis)
    {
        var usable = new bool[network.Features.Count];
        for (var e = 0; e < network.EdgeCount; e++)
            if (analysis.IsEdgeInLargestComponent(network, e)) usable[network.EdgeFeature(e)] = true;
        return usable;
    }

    /// <summary>(start, end) node of each feature in coordinate order; any edge of the feature identifies them.</summary>
    private static (int Start, int End)[] FeatureEndNodes(RoadNetwork network)
    {
        var ends = new (int Start, int End)[network.Features.Count];
        for (var e = 0; e < network.EdgeCount; e++)
            ends[network.Feature[e]] = network.Reversed[e] ? (network.Head[e], network.Tail[e]) : (network.Tail[e], network.Head[e]);
        return ends;
    }

    private static List<RoadNetwork.RawEdge> SplitEdges(RoadNetwork network, SortedDictionary<int, List<(double Offset, int Node)>> splits)
    {
        var edges = new List<RoadNetwork.RawEdge>(network.EdgeCount + splits.Sum(s => s.Value.Count) * 2);
        for (var e = 0; e < network.EdgeCount; e++)
        {
            var f = network.Feature[e];
            if (!splits.TryGetValue(f, out var list))
            {
                edges.Add(new(network.Tail[e], network.Head[e], network.Length[e], network.Class[e], f, network.Reversed[e], network.StartOffset[e], network.EndOffset[e]));
                continue;
            }

            var total = network.Length[e];
            var reversed = network.Reversed[e];
            var startNode = reversed ? network.Head[e] : network.Tail[e];
            var endNode = reversed ? network.Tail[e] : network.Head[e];

            // Chain in coordinate order: startNode, v1..vk, endNode with offsets 0, o1..ok, total.
            var chainNodes = new int[list.Count + 2];
            var chainOffsets = new double[list.Count + 2];
            chainNodes[0] = startNode;
            for (var k = 0; k < list.Count; k++) { chainNodes[k + 1] = list[k].Node; chainOffsets[k + 1] = list[k].Offset; }
            chainNodes[^1] = endNode;
            chainOffsets[^1] = total;

            for (var k = 0; k + 1 < chainNodes.Length; k++)
            {
                var len = chainOffsets[k + 1] - chainOffsets[k];
                var (from, to) = reversed ? (chainNodes[k + 1], chainNodes[k]) : (chainNodes[k], chainNodes[k + 1]);
                edges.Add(new(from, to, len, network.Class[e], f, reversed, chainOffsets[k], chainOffsets[k + 1]));
            }
        }
        return edges;
    }
}
