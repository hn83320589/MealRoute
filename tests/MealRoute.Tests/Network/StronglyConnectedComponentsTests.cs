using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Network;

public class StronglyConnectedComponentsTests
{
    private static int[] CanonicalFromResult(SccResult r, int n)
    {
        var min = Enumerable.Repeat(int.MaxValue, r.ComponentCount).ToArray();
        for (var v = 0; v < n; v++) min[r.ComponentOf[v]] = Math.Min(min[r.ComponentOf[v]], v);
        return Enumerable.Range(0, n).Select(v => min[r.ComponentOf[v]]).ToArray();
    }

    internal static (int[] FirstOut, int[] Head) Csr(int n, List<(int From, int To)> edges)
    {
        var sorted = edges.OrderBy(e => e.From).ThenBy(e => e.To).ToList();
        var firstOut = new int[n + 1];
        foreach (var (f, _) in sorted) firstOut[f + 1]++;
        for (var i = 0; i < n; i++) firstOut[i + 1] += firstOut[i];
        return (firstOut, sorted.Select(e => e.To).ToArray());
    }

    [Fact]
    public void Property_random_digraphs_match_reference_kosaraju()
    {
        Property.ForAll(2_000, 300, (r, _) =>
        {
            var n = r.Next(1, 60);
            var m = r.Next(0, n * 3);
            var edges = Enumerable.Range(0, m).Select(_ => (r.Next(n), r.Next(n))).ToList();
            var (fo, head) = Csr(n, edges);

            var result = StronglyConnectedComponents.Compute(n, fo, head);

            Assert.Equal(RefSccKosaraju.CanonicalLabels(n, edges), CanonicalFromResult(result, n));
            Assert.Equal(n, result.Sizes.Sum());
        });
    }

    [Fact]
    public void Component_ids_are_ordered_by_smallest_member_node()
    {
        // 0->1 one-way only: two singleton components; 2<->3 one component
        var edges = new List<(int, int)> { (0, 1), (2, 3), (3, 2) };
        var (fo, head) = Csr(4, edges);
        var r = StronglyConnectedComponents.Compute(4, fo, head);
        Assert.Equal(new[] { 0, 1, 2, 2 }, r.ComponentOf);
        Assert.Equal(2, r.Largest);
    }

    [Fact]
    public void Largest_component_ties_break_to_smallest_id()
    {
        var edges = new List<(int, int)> { (0, 1), (1, 0), (2, 3), (3, 2) };
        var (fo, head) = Csr(4, edges);
        Assert.Equal(0, StronglyConnectedComponents.Compute(4, fo, head).Largest);
    }

    [Fact]
    public void Long_cycle_of_100k_nodes_does_not_overflow_the_stack()
    {
        const int n = 100_000;
        var edges = Enumerable.Range(0, n).Select(i => (i, (i + 1) % n)).ToList();
        var (fo, head) = Csr(n, edges);
        var r = StronglyConnectedComponents.Compute(n, fo, head);
        Assert.Equal(1, r.ComponentCount);
    }

    [Fact]
    public void Network_analysis_flags_small_components_with_feature_ids()
    {
        static GeoPoint P(double lat, double lon) => new(lat, lon);
        var features = new[]
        {
            new RoadFeature(0, "main1", new[] { P(25, 121.500), P(25, 121.501) }, OnewayDirection.Both, RoadClass.Arterial),
            new RoadFeature(1, "main2", new[] { P(25, 121.501), P(25, 121.502) }, OnewayDirection.Both, RoadClass.Arterial),
            // dead-end one-way out of the main component: its head node is a separate SCC
            new RoadFeature(2, "trap", new[] { P(25, 121.502), P(25.001, 121.502) }, OnewayDirection.Forward, RoadClass.Residential),
            // isolated island
            new RoadFeature(3, "island", new[] { P(26, 121.0), P(26, 121.001) }, OnewayDirection.Both, RoadClass.Residential),
        };
        var net = NetworkBuilder.Build(features);
        var analysis = NetworkAnalysis.Analyze(net);

        Assert.Equal(3, analysis.Scc.Sizes[analysis.Scc.Largest]);
        Assert.Equal(2, analysis.SmallComponents.Count);
        Assert.Contains(analysis.SmallComponents, c => c.FeatureIds.Contains("island"));
        Assert.Contains(analysis.SmallComponents, c => c.FeatureIds.Contains("trap") && c.NodeCount == 1);
        Assert.True(analysis.IsInLargestComponent(net.EdgeTail(0)));
    }
}
