using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Optimization;

/// <summary>
/// Orchestrates construction, a fixed number of independent ALNS runs (in parallel; results do
/// not depend on the thread count) and the final exact verification.
/// <para>The search uses the fast oracle (L1); the returned solution is re-evaluated with the
/// exact oracle (L2): routes that become infeasible are repaired and unserved stops are retried,
/// so every reported time and feasibility verdict comes from exact computation.</para>
/// </summary>
public sealed class Optimizer
{
    private readonly ProblemInstance _inst;
    private readonly RouteEvaluator _search;
    private readonly RouteEvaluator _exact;
    private readonly Func<int, int, bool> _canServe;

    public Optimizer(ProblemInstance instance, ITravelTimeOracle searchOracle, ITravelTimeOracle exactOracle, Func<int, int, bool>? canServe = null)
    {
        _inst = instance;
        _search = new RouteEvaluator(instance, searchOracle);
        _exact = new RouteEvaluator(instance, exactOracle);
        _canServe = canServe ?? ((_, _) => true);
        SameOracle = ReferenceEquals(searchOracle, exactOracle);
    }

    private bool SameOracle { get; }

    public Task<OptimizationResult> RunAsync(Solution? initial, LockSet locks, OptimizerOptions options,
        IProgress<OptimizationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(initial, locks, options, progress, cancellationToken), CancellationToken.None);

    public OptimizationResult Run(Solution? initial, LockSet locks, OptimizerOptions options,
        IProgress<OptimizationProgress>? progress, CancellationToken cancellationToken)
    {
        var time = options.TimeProvider;
        var startStamp = time.GetTimestamp();
        var deadline = options.TimeLimit;
        bool ShouldStop() => cancellationToken.IsCancellationRequested ||
                             (deadline is { } d && time.GetElapsedTime(startStamp) >= d);

        var V = _inst.Vehicles.Count;
        if (initial is not null) locks.Validate(initial);
        var frozen = new int[V];
        var locked = new bool[V];
        for (var v = 0; v < V; v++)
        {
            locked[v] = locks.LockedVehicles.Contains(v);
            frozen[v] = initial is null ? 0 : locks.FrozenPrefixLength(v, initial.Routes[v].Stops.Count);
        }
        var related = Relatedness();

        var tracker = new ProgressTracker(progress, options, time, startStamp);
        var start = InitialState(initial);
        RepairInfeasibleRoutes(start, frozen, locked);
        // Routes still infeasible (violation inside a locked/frozen part) are kept as the user fixed them.
        for (var v = 0; v < V; v++) if (!start.Costs[v].Feasible) locked[v] = true;
        var constructor = new AlnsRun(_inst, _search, new Rng(options.Seed), frozen, locked, _canServe, related, ShouldStop, _ => { });
        constructor.Construct(start);
        tracker.Best(start.Objective(_inst));

        var results = new (SearchState State, ObjectiveValue Obj)[options.Runs];
        long totalIterations = 0;
        if (!ShouldStop())
        {
            Parallel.For(0, options.Runs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism) }, i =>
            {
                var run = new AlnsRun(_inst, _search, new Rng(Rng.Derive(options.Seed, i)), frozen, locked, _canServe, related, ShouldStop,
                    n => { Interlocked.Add(ref totalIterations, n); tracker.Iterations(Interlocked.Read(ref totalIterations)); });
                var best = run.Run(start, options.MaxIterations, options.VehicleReductionShare);
                var obj = best.Objective(_inst);
                results[i] = (best, obj);
                tracker.Best(obj);
            });
        }

        var winner = start;
        var winnerObj = start.Objective(_inst);
        foreach (var (state, obj) in results)
        {
            if (state is null) continue;
            if (obj < winnerObj) { winner = state; winnerObj = obj; }
        }

        var solution = FinalizeExact(winner, frozen, locked);
        var objective = ObjectiveValue.Compute(_inst, solution);
        var cancelled = cancellationToken.IsCancellationRequested;
        tracker.Finish(objective, completed: !cancelled);
        return new OptimizationResult(solution, objective, Interlocked.Read(ref totalIterations), cancelled, time.GetElapsedTime(startStamp));
    }

    // ------------------------------------------------------------------ setup

    private SearchState InitialState(Solution? initial)
    {
        var V = _inst.Vehicles.Count;
        var s = new SearchState(V);
        var assigned = new bool[_inst.Stops.Count];
        if (initial is not null)
        {
            for (var v = 0; v < V; v++)
            {
                s.Routes[v].AddRange(initial.Routes[v].Stops);
                foreach (var stop in initial.Routes[v].Stops) assigned[stop] = true;
                var deps = new double[s.Routes[v].Count];
                s.Costs[v] = _search.EvaluateCost(v, s.Routes[v].ToArray(), deps);
                s.Departures[v] = deps;
            }
        }
        for (var stop = 0; stop < _inst.Stops.Count; stop++)
            if (!assigned[stop]) s.Unassigned.Add(stop);
        return s;
    }

    /// <summary>
    /// An initial route may be infeasible (a delay report, a forced manual edit). The search only
    /// works on feasible routes, so stops are taken out (at the first violation, else the last
    /// free one) until the route is feasible; they become unassigned and may be re-inserted.
    /// Locked routes and frozen prefixes are left as the user fixed them.
    /// </summary>
    private void RepairInfeasibleRoutes(SearchState s, int[] frozen, bool[] locked)
    {
        for (var v = 0; v < s.Routes.Length; v++)
        {
            if (locked[v]) continue;
            var route = s.Routes[v];
            while (!s.Costs[v].Feasible && route.Count > frozen[v])
            {
                var eval = _search.Evaluate(v, route);
                var at = eval.FirstViolation is { Stop: >= 0 } fv ? route.IndexOf(fv.Stop) : -1;
                if (at < frozen[v]) at = route.Count - 1;
                s.AddUnassigned(route[at]);
                route.RemoveAt(at);
                var deps = new double[route.Count];
                s.Costs[v] = _search.EvaluateCost(v, route.ToArray(), deps);
                s.Departures[v] = deps;
            }
        }
    }

    /// <summary>Shaw relatedness: normalised travel time at mid-horizon plus window-start difference.</summary>
    private double[][] Relatedness()
    {
        var n = _inst.Stops.Count;
        var mid = _inst.Vehicles.Count == 0 ? 43_200 : _inst.Vehicles.Average(v => (v.EarliestStart + v.LatestEnd) / 2);
        var travel = new double[n][];
        double maxT = 1, maxW = 1;
        for (var i = 0; i < n; i++)
        {
            travel[i] = new double[n];
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                var t = _search.Oracle.ArrivalTime(_inst.StopLocation(i), _inst.StopLocation(j), mid) - mid;
                travel[i][j] = double.IsInfinity(t) ? 1e9 : t;
                if (!double.IsInfinity(t)) maxT = Math.Max(maxT, t);
            }
            maxW = Math.Max(maxW, _inst.Stops[i].WindowStart);
        }
        var rel = new double[n][];
        for (var i = 0; i < n; i++)
        {
            rel[i] = new double[n];
            for (var j = 0; j < n; j++)
                rel[i][j] = travel[i][j] / maxT + Math.Abs(_inst.Stops[i].WindowStart - _inst.Stops[j].WindowStart) / maxW;
        }
        return rel;
    }

    // ------------------------------------------------------------------ exact finalisation

    private Solution FinalizeExact(SearchState state, int[] frozen, bool[] locked)
    {
        var V = _inst.Vehicles.Count;
        var routes = state.Routes.Select(r => r.ToList()).ToArray();
        var unserved = new SortedSet<int>(state.Unassigned);

        if (!SameOracle)
        {
            // 1. Repair routes the exact oracle finds infeasible (rare: L1 is an upper bound, so only
            //    driving-limit effects of shorter exact legs can flip a verdict).
            for (var v = 0; v < V; v++)
            {
                if (locked[v]) continue;
                while (routes[v].Count > frozen[v] && !_exact.EvaluateCost(v, routes[v].ToArray()).Feasible)
                {
                    var eval = _exact.Evaluate(v, routes[v]);
                    var at = eval.FirstViolation is { Stop: >= 0 } fv ? routes[v].IndexOf(fv.Stop) : -1;
                    if (at < frozen[v]) at = routes[v].Count - 1;
                    unserved.Add(routes[v][at]);
                    routes[v].RemoveAt(at);
                }
            }

            // 2. Exact legs are never slower than L1's, so unserved stops may now fit.
            foreach (var stop in unserved.OrderByDescending(s => _inst.Stops[s].Mandatory ? 6 : _inst.Stops[s].Priority).ThenBy(s => s).ToArray())
            {
                if (unserved.Count > 40) break;
                var best = (Cost: double.PositiveInfinity, V: -1, P: -1);
                for (var v = 0; v < V; v++)
                {
                    if (locked[v] || !_canServe(v, stop)) continue;
                    var cur = _exact.EvaluateCost(v, routes[v].ToArray());
                    for (var p = frozen[v]; p <= routes[v].Count; p++)
                    {
                        var cand = routes[v].ToList();
                        cand.Insert(p, stop);
                        var c = _exact.EvaluateCost(v, cand.ToArray());
                        if (!c.Feasible) continue;
                        var delta = (routes[v].Count == 0 ? 1e6 : 0) + c.Travel - cur.Travel;
                        if (delta < best.Cost) best = (delta, v, p);
                    }
                }
                if (best.V >= 0)
                {
                    routes[best.V].Insert(best.P, stop);
                    unserved.Remove(stop);
                }
            }
        }

        var plans = new RoutePlan[V];
        for (var v = 0; v < V; v++)
            plans[v] = routes[v].Count == 0 ? RoutePlan.Empty(v) : _exact.Evaluate(v, routes[v]).ToPlan();
        var reasons = unserved.Select(s => new UnservedStop(s, UnservedReason(s))).ToArray();
        return new Solution(plans, reasons);
    }

    /// <summary>Explains why a stop could not be served.</summary>
    public string UnservedReason(int stop)
    {
        var V = _inst.Vehicles.Count;
        var reachable = false;
        Violation? alone = null;
        for (var v = 0; v < V; v++)
        {
            var arr = _exact.Oracle.ArrivalTime(_inst.VehicleStartLocation(v), _inst.StopLocation(stop), _inst.Vehicles[v].EarliestStart);
            if (!double.IsInfinity(arr)) reachable = true;
        }
        if (!reachable) return "無法從路網抵達（未吸附到路網或位於孤立路段）";

        var anyServeAlone = false;
        var allowedAny = false;
        for (var v = 0; v < V; v++)
        {
            if (!_canServe(v, stop)) continue;
            allowedAny = true;
            var e = _exact.Evaluate(v, new[] { stop });
            if (e.IsFeasible) { anyServeAlone = true; break; }
            if (alone is null || e.FirstViolation!.Time > alone.Time) alone = e.FirstViolation;
        }
        if (!allowedAny) return "沒有可服務此站的車輛";
        if (!anyServeAlone) return $"任何車輛單獨前往也無法服務：{alone!.Message}";
        return "所有可用車輛的容量、時間或工時已滿，加入任何路線都會違反限制";
    }

    /// <summary>Monotone progress aggregation across runs (thread-safe).</summary>
    private sealed class ProgressTracker(IProgress<OptimizationProgress>? progress, OptimizerOptions options, TimeProvider time, long startStamp)
    {
        private readonly object _gate = new();
        private double _fraction;
        private long _iterations;
        private ObjectiveValue? _best;

        public void Iterations(long total)
        {
            lock (_gate) { _iterations = Math.Max(_iterations, total); Report(); }
        }

        public void Best(ObjectiveValue obj)
        {
            lock (_gate)
            {
                if (_best is null || obj < _best.Value) _best = obj;
                Report();
            }
        }

        public void Finish(ObjectiveValue obj, bool completed)
        {
            lock (_gate)
            {
                _best = obj;
                if (completed) _fraction = 1.0;
                Report(force: true);
            }
        }

        private void Report(bool force = false)
        {
            if (progress is null || _best is null) return;
            var elapsed = time.GetElapsedTime(startStamp);
            var byIter = (double)_iterations / Math.Max(1, (long)options.Runs * options.MaxIterations);
            var byTime = options.TimeLimit is { } limit && limit > TimeSpan.Zero ? elapsed / limit : 0;
            var f = Math.Min(1.0, Math.Max(byIter, byTime));
            if (f < _fraction) f = _fraction;
            if (!force && f == _fraction && _iterations % 500 != 0) return;
            _fraction = f;
            progress.Report(new OptimizationProgress(f, _iterations, _best.Value, elapsed));
        }
    }
}
