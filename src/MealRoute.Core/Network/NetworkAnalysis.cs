namespace MealRoute.Core.Network;

/// <summary>A strongly connected component other than the largest one (shown as a warning).</summary>
public sealed record SmallComponent(int ComponentId, int NodeCount, IReadOnlyList<string> FeatureIds);

public sealed class NetworkAnalysis
{
    private NetworkAnalysis(SccResult scc, IReadOnlyList<SmallComponent> small)
    {
        Scc = scc;
        SmallComponents = small;
    }

    public SccResult Scc { get; }
    public IReadOnlyList<SmallComponent> SmallComponents { get; }

    public bool IsInLargestComponent(int node) => Scc.ComponentOf[node] == Scc.Largest;

    /// <summary>An edge is usable for snapping only when both its endpoints are in the largest SCC.</summary>
    public bool IsEdgeInLargestComponent(RoadNetwork network, int edge) =>
        IsInLargestComponent(network.EdgeTail(edge)) && IsInLargestComponent(network.EdgeHead(edge));

    public static NetworkAnalysis Analyze(RoadNetwork network)
    {
        var scc = StronglyConnectedComponents.Compute(network);
        var featureSets = new SortedSet<int>?[scc.ComponentCount];
        for (var e = 0; e < network.EdgeCount; e++)
        {
            foreach (var node in new[] { network.EdgeTail(e), network.EdgeHead(e) })
            {
                var c = scc.ComponentOf[node];
                if (c == scc.Largest) continue;
                (featureSets[c] ??= new SortedSet<int>()).Add(network.EdgeFeature(e));
            }
        }

        var small = new List<SmallComponent>();
        for (var c = 0; c < scc.ComponentCount; c++)
        {
            if (c == scc.Largest) continue;
            var ids = featureSets[c]?.Select(f => network.Features[f].Id).ToArray() ?? Array.Empty<string>();
            small.Add(new SmallComponent(c, scc.Sizes[c], ids));
        }
        return new NetworkAnalysis(scc, small);
    }
}
