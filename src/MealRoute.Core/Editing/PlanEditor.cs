using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Editing;

/// <summary>A manual change to the plan.</summary>
public abstract record Move;

/// <summary>Moves a stop to <paramref name="ToVehicle"/> at <paramref name="ToPosition"/> (index in the route without the stop); vehicle -1 = unserved.</summary>
public sealed record MoveStop(int Stop, int ToVehicle, int ToPosition) : Move;

/// <summary>Exchanges the places of two stops (either may be unserved).</summary>
public sealed record SwapStops(int StopA, int StopB) : Move;

/// <param name="IsAllowed">False when the move would change a locked stop or route (see <see cref="RejectReason"/>).</param>
/// <param name="Feasible">All routes feasible after the move.</param>
/// <param name="FirstViolation">Earliest violation in the changed routes (else in any route).</param>
public sealed record MovePreview(
    bool IsAllowed, string? RejectReason, bool Feasible,
    ObjectiveValue Before, ObjectiveValue After, Violation? FirstViolation,
    IReadOnlyList<RouteEvaluation> ChangedRoutes);

public sealed record ApplyResult(bool Applied, MovePreview Preview);

/// <summary>
/// Interactive editing session. Keeps one evaluation per route for the fast oracle (L1) and one
/// for the exact oracle (L2); a move changes at most two routes, so only those are re-evaluated
/// (incremental evaluation). <see cref="Preview"/> uses L1 (microseconds), <see cref="PreviewExact"/>
/// and <see cref="Apply"/> use L2. Locks follow the optimizer's rules: a locked route never
/// changes; for a locked stop, all positions up to it stay fixed.
/// </summary>
public sealed class PlanEditor
{
    private readonly ProblemInstance _inst;
    private readonly RouteEvaluator _fast;
    private readonly RouteEvaluator _exact;
    private readonly List<int>[] _routes;
    private readonly RouteEvaluation[] _fastEval;
    private readonly RouteEvaluation[] _exactEval;
    private readonly HashSet<int> _lockedRoutes = new();
    private readonly Dictionary<int, (int Vehicle, int Position)> _lockedStops = new();
    private readonly Dictionary<int, string> _unservedReasons = new();
    private readonly Stack<int[][]> _undo = new();
    private readonly Stack<int[][]> _redo = new();

    public PlanEditor(ProblemInstance instance, ITravelTimeOracle fastOracle, ITravelTimeOracle exactOracle, Solution solution)
    {
        _inst = instance;
        _fast = new RouteEvaluator(instance, fastOracle);
        _exact = new RouteEvaluator(instance, exactOracle);
        var V = instance.Vehicles.Count;
        _routes = new List<int>[V];
        _fastEval = new RouteEvaluation[V];
        _exactEval = new RouteEvaluation[V];
        for (var v = 0; v < V; v++)
        {
            _routes[v] = solution.Routes[v].Stops.ToList();
            Reevaluate(v);
        }
        foreach (var u in solution.Unserved) _unservedReasons[u.Stop] = u.Reason;
    }

    public ProblemInstance Instance => _inst;
    public IReadOnlyList<IReadOnlyList<int>> Routes => _routes;
    public IReadOnlyCollection<int> Unserved => Enumerable.Range(0, _inst.Stops.Count).Where(s => Locate(s).Vehicle < 0).ToArray();
    public RouteEvaluation CurrentEvaluation(int vehicle) => _exactEval[vehicle];
    public ObjectiveValue Objective => Compute(_exactEval, _routes);
    public ObjectiveValue FastObjective => Compute(_fastEval, _routes);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public LockSet Locks => new(new HashSet<int>(_lockedRoutes), new Dictionary<int, (int, int)>(_lockedStops));

    /// <summary>Current plan as a solution (exact times).</summary>
    public Solution ToSolution() => new(
        _exactEval.Select((e, v) => e.IsEmpty ? RoutePlan.Empty(v) : e.ToPlan()).ToArray(),
        Unserved.Select(s => new UnservedStop(s, _unservedReasons.TryGetValue(s, out var r) ? r : "手動移出路線")).ToArray());

    /// <summary>Exact plan of a stop sequence for a vehicle (helper for building solutions).</summary>
    public RoutePlan PlanOf(int vehicle, IReadOnlyList<int> stops) =>
        stops.Count == 0 ? RoutePlan.Empty(vehicle) : _exact.Evaluate(vehicle, stops).ToPlan();

    // ------------------------------------------------------------------ locks

    public void LockStop(int stop)
    {
        var (v, p) = Locate(stop);
        if (v < 0) throw new InvalidOperationException("未排入路線的站無法鎖定。");
        _lockedStops[stop] = (v, p);
    }

    public void UnlockStop(int stop) => _lockedStops.Remove(stop);
    public void LockRoute(int vehicle) => _lockedRoutes.Add(vehicle);
    public void UnlockRoute(int vehicle) => _lockedRoutes.Remove(vehicle);
    public bool IsStopLocked(int stop) => _lockedStops.ContainsKey(stop);
    public bool IsRouteLocked(int vehicle) => _lockedRoutes.Contains(vehicle);

    // ------------------------------------------------------------------ moves

    public MovePreview Preview(Move move) => PreviewWith(move, exact: false);

    public MovePreview PreviewExact(Move move) => PreviewWith(move, exact: true);

    /// <summary>Routes as they would be after the move (for inspection and tests).</summary>
    public IReadOnlyList<IReadOnlyList<int>> RoutesAfter(Move move)
    {
        var changed = Changed(move);
        return _routes.Select((r, v) => changed.TryGetValue(v, out var nr) ? (IReadOnlyList<int>)nr : r).ToArray();
    }

    public ApplyResult Apply(Move move, bool allowInfeasible = false)
    {
        var preview = PreviewExact(move);
        if (!preview.IsAllowed || (!preview.Feasible && !allowInfeasible)) return new ApplyResult(false, preview);
        _undo.Push(Snapshot());
        _redo.Clear();
        foreach (var (v, route) in Changed(move))
        {
            _routes[v] = route;
            Reevaluate(v);
        }
        return new ApplyResult(true, preview);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    /// <summary>Replaces the whole plan (e.g. after re-optimisation); clears redo, keeps undo.</summary>
    public void ReplaceSolution(Solution solution)
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        for (var v = 0; v < _routes.Length; v++)
        {
            _routes[v] = solution.Routes[v].Stops.ToList();
            Reevaluate(v);
        }
        foreach (var u in solution.Unserved) _unservedReasons[u.Stop] = u.Reason;
    }

    // ------------------------------------------------------------------ internals

    private MovePreview PreviewWith(Move move, bool exact)
    {
        var evals = exact ? _exactEval : _fastEval;
        var evaluator = exact ? _exact : _fast;
        var before = Compute(evals, _routes);
        var changed = Changed(move);

        var reject = CheckLocks(move, changed);
        if (reject is not null)
            return new MovePreview(false, reject, AllFeasible(evals), before, before, null, Array.Empty<RouteEvaluation>());

        var after = (RouteEvaluation[])evals.Clone();
        var routesAfter = (IReadOnlyList<int>[])_routes.ToArray();
        var changedEvals = new List<RouteEvaluation>();
        foreach (var (v, route) in changed)
        {
            after[v] = evaluator.Evaluate(v, route);
            routesAfter[v] = route;
            changedEvals.Add(after[v]);
        }

        Violation? first = null;
        foreach (var e in changedEvals) first = Earlier(first, e.FirstViolation);
        if (first is null) foreach (var e in after) first = Earlier(first, e.FirstViolation);

        return new MovePreview(true, null, AllFeasible(after), before, Compute(after, routesAfter), first, changedEvals);
    }

    private static Violation? Earlier(Violation? a, Violation? b) =>
        b is null ? a : a is null || b.Time < a.Time ? b : a;

    private static bool AllFeasible(RouteEvaluation[] evals) => evals.All(e => e.IsFeasible);

    private ObjectiveValue Compute(RouteEvaluation[] evals, IReadOnlyList<IReadOnlyList<int>> routes)
    {
        var served = new bool[_inst.Stops.Count];
        foreach (var r in routes) foreach (var s in r) served[s] = true;
        var plans = evals.Select((e, v) => e.IsEmpty ? RoutePlan.Empty(v) : e.ToPlan());
        return ObjectiveValue.Compute(_inst, plans, Enumerable.Range(0, served.Length).Where(s => !served[s]));
    }

    private (int Vehicle, int Position) Locate(int stop)
    {
        for (var v = 0; v < _routes.Length; v++)
        {
            var p = _routes[v].IndexOf(stop);
            if (p >= 0) return (v, p);
        }
        return (-1, -1);
    }

    /// <summary>New stop lists of the routes the move changes.</summary>
    private Dictionary<int, List<int>> Changed(Move move)
    {
        var result = new Dictionary<int, List<int>>();
        List<int> Get(int v) => result.TryGetValue(v, out var r) ? r : result[v] = _routes[v].ToList();

        switch (move)
        {
            case MoveStop m:
            {
                var (from, _) = Locate(m.Stop);
                if (from >= 0) Get(from).Remove(m.Stop);
                if (m.ToVehicle >= 0)
                {
                    var target = Get(m.ToVehicle);
                    target.Insert(Math.Clamp(m.ToPosition, 0, target.Count), m.Stop);
                }
                break;
            }
            case SwapStops s when s.StopA != s.StopB:
            {
                var (va, pa) = Locate(s.StopA);
                var (vb, pb) = Locate(s.StopB);
                if (va >= 0) Get(va)[pa] = s.StopB;
                if (vb >= 0) Get(vb)[pb] = s.StopA;
                break;
            }
        }
        // drop entries that did not actually change
        foreach (var v in result.Keys.ToArray())
            if (result[v].SequenceEqual(_routes[v])) result.Remove(v);
        return result;
    }

    private string? CheckLocks(Move move, Dictionary<int, List<int>> changed)
    {
        var moved = move switch
        {
            MoveStop m => new[] { m.Stop },
            SwapStops s => new[] { s.StopA, s.StopB },
            _ => Array.Empty<int>(),
        };
        foreach (var stop in moved)
            if (_lockedStops.ContainsKey(stop) && changed.Count > 0)
                return $"「{_inst.Stops[stop].Name}」已鎖定，不能移動。";

        foreach (var (v, route) in changed)
        {
            if (_lockedRoutes.Contains(v)) return $"{_inst.Vehicles[v].DriverName} 的路線已鎖定。";
            foreach (var (stop, (lv, pos)) in _lockedStops)
            {
                if (lv != v) continue;
                for (var p = 0; p <= pos; p++)
                    if (p >= route.Count || route[p] != _routes[v][p])
                        return $"會改變鎖定站「{_inst.Stops[stop].Name}」的位置（第 {pos + 1} 站以前不可變動）。";
            }
        }
        return null;
    }

    private void Reevaluate(int v)
    {
        _fastEval[v] = _fast.Evaluate(v, _routes[v]);
        _exactEval[v] = ReferenceEquals(_fast.Oracle, _exact.Oracle) ? _fastEval[v] : _exact.Evaluate(v, _routes[v]);
    }

    private int[][] Snapshot() => _routes.Select(r => r.ToArray()).ToArray();

    private void Restore(int[][] snapshot)
    {
        for (var v = 0; v < _routes.Length; v++)
        {
            if (_routes[v].SequenceEqual(snapshot[v])) continue;
            _routes[v] = snapshot[v].ToList();
            Reevaluate(v);
        }
    }
}
