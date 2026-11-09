namespace MealRoute.Tests.Reference;

/// <summary>
/// Independent SCC reference (Kosaraju, adjacency lists, explicit stacks).
/// Returns a canonical labelling: component id = smallest node id inside the component.
/// </summary>
internal static class RefSccKosaraju
{
    public static int[] CanonicalLabels(int n, IReadOnlyList<(int From, int To)> edges)
    {
        var fwd = new List<int>[n];
        var rev = new List<int>[n];
        for (var i = 0; i < n; i++) { fwd[i] = new(); rev[i] = new(); }
        foreach (var (f, t) in edges) { fwd[f].Add(t); rev[t].Add(f); }

        var visited = new bool[n];
        var order = new List<int>(n);
        for (var s = 0; s < n; s++)
        {
            if (visited[s]) continue;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((s, 0));
            visited[s] = true;
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                if (i < fwd[v].Count)
                {
                    stack.Push((v, i + 1));
                    var w = fwd[v][i];
                    if (!visited[w]) { visited[w] = true; stack.Push((w, 0)); }
                }
                else order.Add(v);
            }
        }

        var comp = Enumerable.Repeat(-1, n).ToArray();
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var root = order[k];
            if (comp[root] != -1) continue;
            var members = new List<int>();
            var st = new Stack<int>();
            st.Push(root); comp[root] = root;
            while (st.Count > 0)
            {
                var v = st.Pop(); members.Add(v);
                foreach (var w in rev[v])
                    if (comp[w] == -1) { comp[w] = root; st.Push(w); }
            }
            var min = members.Min();
            foreach (var m in members) comp[m] = min;
        }
        return comp;
    }
}
