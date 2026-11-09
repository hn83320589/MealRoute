namespace MealRoute.Core.Network;

/// <param name="ComponentOf">Component id per node. Ids are 0..k-1 ordered by the smallest node id in each component.</param>
/// <param name="Sizes">Node count per component.</param>
/// <param name="Largest">Component with most nodes; ties go to the smallest id.</param>
public sealed record SccResult(int[] ComponentOf, int ComponentCount, int[] Sizes, int Largest);

/// <summary>Iterative Tarjan SCC (no recursion, safe for large graphs).</summary>
public static class StronglyConnectedComponents
{
    public static SccResult Compute(RoadNetwork network) =>
        Compute(network.NodeCount, network.FirstOut, network.Head);

    /// <param name="firstOut">CSR offsets, length n + 1.</param>
    /// <param name="head">CSR targets.</param>
    public static SccResult Compute(int n, int[] firstOut, int[] head)
    {
        const int Unvisited = -1;
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        var rawComp = new int[n];
        Array.Fill(index, Unvisited);

        var sccStack = new int[n];
        var sccTop = 0;
        var callNode = new int[n];
        var callEdge = new int[n];
        var counter = 0;
        var rawCount = 0;

        for (var root = 0; root < n; root++)
        {
            if (index[root] != Unvisited) continue;

            var depth = 0;
            callNode[0] = root;
            callEdge[0] = firstOut[root];
            index[root] = low[root] = counter++;
            sccStack[sccTop++] = root;
            onStack[root] = true;

            while (depth >= 0)
            {
                var v = callNode[depth];
                if (callEdge[depth] < firstOut[v + 1])
                {
                    var w = head[callEdge[depth]++];
                    if (index[w] == Unvisited)
                    {
                        index[w] = low[w] = counter++;
                        sccStack[sccTop++] = w;
                        onStack[w] = true;
                        depth++;
                        callNode[depth] = w;
                        callEdge[depth] = firstOut[w];
                    }
                    else if (onStack[w] && index[w] < low[v])
                    {
                        low[v] = index[w];
                    }
                    continue;
                }

                // v is finished
                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = sccStack[--sccTop];
                        onStack[w] = false;
                        rawComp[w] = rawCount;
                    } while (w != v);
                    rawCount++;
                }
                depth--;
                if (depth >= 0)
                {
                    var parent = callNode[depth];
                    if (low[v] < low[parent]) low[parent] = low[v];
                }
            }
        }

        // Canonical renumbering: order components by their smallest node id.
        var remap = new int[rawCount];
        Array.Fill(remap, -1);
        var next = 0;
        var comp = new int[n];
        for (var v = 0; v < n; v++)
        {
            if (remap[rawComp[v]] == -1) remap[rawComp[v]] = next++;
            comp[v] = remap[rawComp[v]];
        }

        var sizes = new int[next];
        foreach (var c in comp) sizes[c]++;
        var largest = 0;
        for (var c = 1; c < next; c++)
            if (sizes[c] > sizes[largest]) largest = c;

        return new SccResult(comp, next, sizes, n == 0 ? -1 : largest);
    }
}
