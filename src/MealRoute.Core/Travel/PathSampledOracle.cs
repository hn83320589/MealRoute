using MealRoute.Core.Network;

namespace MealRoute.Core.Travel;

public sealed class OracleBuildOptions
{
    /// <summary>Spacing of departure-time samples (seconds); default one speed slot.</summary>
    public double SampleIntervalSeconds { get; init; } = SpeedProfile.SlotSeconds;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    /// <summary>Fraction of sources done, monotone in [0, 1].</summary>
    public IProgress<double>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// L1 cache. For every source location and sample departure time t_k a one-to-all TD Dijkstra
/// records the optimal path to every other location (compressed to road-class runs, deduplicated
/// per pair). A query at t in [t_k, t_k+1] evaluates the candidate paths of both ends exactly at t
/// and takes the minimum. Properties:
/// <list type="bullet">
/// <item>the value is the exact time of a real path, so it is never below the exact optimum;</item>
/// <item>it equals the exact optimum at every sample time;</item>
/// <item>it is FIFO: each path function is non-decreasing, so is their minimum, and adjacent
/// intervals agree (exact) at their shared sample time.</item>
/// </list>
/// Departures outside the sampled horizon use the boundary sample's paths (properties hold).
/// Query methods are thread-safe; <see cref="Heal"/> must be called only at deterministic points.
/// </summary>
public sealed class PathSampledOracle : ITravelTimeOracle
{
    private readonly SpeedProfile _profile;
    private readonly int[] _nodes;
    private readonly double _t0, _dt;
    private readonly int _sampleCount;
    // _pairPaths[from][to] = distinct candidate paths; _sampleIdx[from][to * K + k] = index into it
    private readonly CompressedPath[][][] _pairPaths;
    private readonly byte[][][] _sampleIdxStore;
    private readonly Dictionary<(int From, int To, int Interval), CompressedPath[]> _healed = new();
    private volatile bool _hasHealed;

    private PathSampledOracle(SpeedProfile profile, int[] nodes, double t0, double dt, int sampleCount, CompressedPath[][][] pairPaths, byte[][][] sampleIdx)
    {
        _profile = profile;
        _nodes = nodes;
        _t0 = t0;
        _dt = dt;
        _sampleCount = sampleCount;
        _pairPaths = pairPaths;
        _sampleIdxStore = sampleIdx;
        SampleTimes = Enumerable.Range(0, sampleCount).Select(k => t0 + k * dt).ToArray();
    }

    public int LocationCount => _nodes.Length;
    public IReadOnlyList<double> SampleTimes { get; }

    /// <summary>Builds the cache for departures in [horizonStart, horizonEnd] (samples aligned to the interval grid).</summary>
    public static PathSampledOracle Build(
        RoadNetwork graph, SpeedProfile profile, IReadOnlyList<int> locationNodes,
        double horizonStart, double horizonEnd, OracleBuildOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonEnd, horizonStart);
        var dt = options.SampleIntervalSeconds;
        var t0 = Math.Floor(horizonStart / dt) * dt;
        var sampleCount = (int)Math.Ceiling((horizonEnd - t0) / dt) + 1;
        if (sampleCount < 2) sampleCount = 2;

        var nodes = locationNodes.ToArray();
        var n = nodes.Length;
        var distinctTargets = nodes.Where(v => v >= 0).Distinct().ToArray();
        var pairPaths = new CompressedPath[n][][];
        var sampleIdx = new byte[n][][];
        var done = 0;
        var lastReported = 0.0;
        var progressGate = new object();

        using var search = new ThreadLocal<TimeDependentDijkstra>(() => new TimeDependentDijkstra(graph, profile));
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism), CancellationToken = options.CancellationToken }, from =>
        {
            var paths = new List<CompressedPath>[n];
            var idx = new byte[n][];
            for (var to = 0; to < n; to++) { paths[to] = new List<CompressedPath>(2); idx[to] = new byte[sampleCount]; }

            if (nodes[from] >= 0)
            {
                var td = search.Value!;
                for (var k = 0; k < sampleCount; k++)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    td.Run(new[] { (nodes[from], t0 + k * dt) }, distinctTargets);
                    for (var to = 0; to < n; to++)
                    {
                        if (nodes[to] < 0 || double.IsPositiveInfinity(td.ArrivalAt(nodes[to])))
                        {
                            idx[to][k] = byte.MaxValue;
                            continue;
                        }
                        var cp = CompressedPath.FromEdges(graph, td.PathTo(nodes[to]));
                        var list = paths[to];
                        var found = list.FindIndex(p => p.SameAs(cp));
                        if (found < 0)
                        {
                            if (list.Count >= byte.MaxValue) throw new InvalidOperationException("Too many distinct paths for one location pair.");
                            list.Add(cp);
                            found = list.Count - 1;
                        }
                        idx[to][k] = (byte)found;
                    }
                }
            }
            else
            {
                for (var to = 0; to < n; to++) Array.Fill(idx[to], byte.MaxValue);
            }

            pairPaths[from] = paths.Select(p => p.ToArray()).ToArray();
            sampleIdx[from] = idx;

            var count = Interlocked.Increment(ref done);
            if (options.Progress is { } progress)
            {
                lock (progressGate)
                {
                    var fraction = (double)count / n;
                    if (fraction > lastReported) { lastReported = fraction; progress.Report(fraction); }
                }
            }
        });

        return new PathSampledOracle(profile, nodes, t0, dt, sampleCount, pairPaths, sampleIdx);
    }

    public double ArrivalTime(int from, int to, double departure) => Leg(from, to, departure).Arrival;

    public LegResult Leg(int from, int to, double departure)
    {
        if (_nodes[from] == _nodes[to]) return new LegResult(departure, 0);
        var idx = _sampleIdxStore[from][to];
        var paths = _pairPaths[from][to];

        var k = Interval(departure);
        var best = new LegResult(double.PositiveInfinity, double.PositiveInfinity);
        Consider(paths, idx[k], departure, ref best);
        if (k + 1 < _sampleCount) Consider(paths, idx[k + 1], departure, ref best);

        if (_hasHealed)
        {
            lock (_healed)
            {
                if (_healed.TryGetValue((from, to, k), out var extra))
                    foreach (var p in extra) Better(p, departure, ref best);
            }
        }
        return best;
    }

    /// <summary>
    /// Adds the exact optimal path for (from, to, departure) to that interval's candidates. Keeps
    /// all properties (adds a real path; sample-time values are already exact). Not to be called
    /// while other threads depend on reproducible results.
    /// </summary>
    public void Heal(int from, int to, double departure, ExactOracle exact)
    {
        if (_nodes[from] == _nodes[to] || _nodes[from] < 0 || _nodes[to] < 0) return;
        var edges = exact.PathEdges(from, to, departure);
        if (edges.Count == 0) return;
        var cp = CompressedPath.FromEdges(exact.Graph, edges);
        var key = (from, to, Interval(departure));
        lock (_healed)
        {
            var existing = _healed.TryGetValue(key, out var arr) ? arr : Array.Empty<CompressedPath>();
            if (existing.Any(p => p.SameAs(cp))) return;
            _healed[key] = existing.Append(cp).ToArray();
            _hasHealed = true;
        }
    }

    private int Interval(double t)
    {
        var k = (int)Math.Floor((t - _t0) / _dt);
        return Math.Clamp(k, 0, _sampleCount - 2);
    }

    private void Consider(CompressedPath[] paths, byte index, double t, ref LegResult best)
    {
        if (index == byte.MaxValue) return;
        Better(paths[index], t, ref best);
    }

    private void Better(CompressedPath p, double t, ref LegResult best)
    {
        var arrival = p.Arrival(_profile, t);
        if (arrival < best.Arrival || (arrival == best.Arrival && p.LengthM < best.DistanceM))
            best = new LegResult(arrival, p.LengthM);
    }
}
