using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;

namespace MealRoute.Core.Optimization;

/// <summary>
/// One deterministic ALNS run (Ropke &amp; Pisinger style). Destroy: random, worst, related (Shaw),
/// route removal. Repair: greedy, greedy with noise, regret-2, regret-3, random order. Stops are inserted by
/// tier first (mandatory, then optional priority 5..1). Acceptance is simulated annealing on
/// travel plus finite penalties for unserved stops and vehicles; the best solution is tracked
/// with the exact lexicographic objective.
/// A first phase tries to empty whole routes (fewer vehicles) with the vehicle set restricted.
/// </summary>
internal sealed class AlnsRun
{
    private const double VehicleWeight = 1e6;
    private const double WorkWeight = 1e-3;
    private const double Infeasible = double.PositiveInfinity;
    private const double Blink = 0.1;
    private const int NeighbourCount = 12;

    private readonly ProblemInstance _inst;
    private readonly RouteEvaluator _eval;
    private readonly Rng _rng;
    private readonly Func<bool> _shouldStop;
    private readonly Action<int> _onIterations;
    private readonly int[] _frozen;
    private readonly bool[] _locked;
    private readonly bool[] _allowed;
    private readonly Func<int, int, bool> _canServe;
    private readonly double[][] _related;

    private readonly double[] _destroyWeights = { 1, 1, 1, 0.5 };
    private readonly double[] _repairWeights = { 1, 1, 1, 1, 1 };
    private readonly double[] _destroyScores = new double[4];
    private readonly double[] _repairScores = new double[5];
    private readonly int[] _destroyUses = new int[4];
    private readonly int[] _repairUses = new int[5];

    public AlnsRun(ProblemInstance inst, RouteEvaluator evaluator, Rng rng, int[] frozenPrefix, bool[] lockedVehicles,
        Func<int, int, bool> canServe, double[][] relatedness, Func<bool> shouldStop, Action<int> onIterations)
    {
        _inst = inst;
        _eval = evaluator;
        _rng = rng;
        _frozen = frozenPrefix;
        _locked = lockedVehicles;
        _canServe = canServe;
        _related = relatedness;
        _shouldStop = shouldStop;
        _onIterations = onIterations;
        _allowed = Enumerable.Repeat(true, inst.Vehicles.Count).ToArray();
    }

    public long Iterations { get; private set; }

    // ------------------------------------------------------------------ public entry points

    /// <summary>Inserts as many unassigned stops as possible (regret-2). Used for construction.</summary>
    public void Construct(SearchState s) => Repair(s, repairOp: 2);

    public SearchState Run(SearchState start, int maxIterations, double vehicleReductionShare)
    {
        var best = start.Clone();
        var bestObj = best.Objective(_inst);
        var reductionBudget = (int)(maxIterations * vehicleReductionShare);
        var used = 0;

        // Phase A: try to remove whole routes.
        while (used < reductionBudget && !_shouldStop())
        {
            var victim = PickRouteToEmpty(best);
            if (victim < 0) break;
            var trial = best.Clone();
            RemoveRoute(trial, victim);
            for (var v = 0; v < _allowed.Length; v++) _allowed[v] = trial.Routes[v].Count > 0 || _locked[v];
            var attemptBudget = Math.Min(reductionBudget - used, Math.Max(200, reductionBudget / 3));
            var (result, iterationsDone) = Search(trial, attemptBudget, ignoreVehicleTier: true, out var _);
            used += iterationsDone;
            Array.Fill(_allowed, true);

            var obj = result.Objective(_inst);
            if (obj < bestObj)
            {
                best = result;
                bestObj = obj;
            }
            else break; // could not empty a route without losing service
        }

        // Phase B: improve with all vehicles available.
        var (final, _) = Search(best, maxIterations - used, ignoreVehicleTier: false, out var _);
        var outcome = final.Objective(_inst) < bestObj ? final : best;
        Polish(outcome);
        return outcome;
    }

    // ------------------------------------------------------------------ core search loop

    private (SearchState Best, int Iterations) Search(SearchState start, int iterations, bool ignoreVehicleTier, out bool stopped)
    {
        stopped = false;
        var current = start.Clone();
        Repair(current, 2);
        var best = current.Clone();
        var bestObj = best.Objective(_inst);
        var curObj = bestObj;

        // Penalties for the annealing scalar, scaled to the average travel per served stop.
        var served = Math.Max(1, _inst.Stops.Count - current.Unassigned.Count);
        _penaltyBase = Math.Max(60, curObj.TravelSeconds / served);
        var t0 = Math.Max(1.0, 0.05 * Math.Max(curObj.TravelSeconds, 600) / Math.Log(2));
        var tEnd = t0 / 1000;
        var cooling = iterations > 1 ? Math.Pow(tEnd / t0, 1.0 / iterations) : 1;
        var temperature = t0;
        var done = 0;
        var pendingReport = 0;

        for (; done < iterations; done++)
        {
            if (_shouldStop()) { stopped = true; break; }
            var d = Roulette(_destroyWeights);
            var r = Roulette(_repairWeights);
            _destroyUses[d]++;
            _repairUses[r]++;

            var cand = current.Clone();
            Destroy(cand, d);
            Repair(cand, r);

            double score = 0;
            if (AllFeasible(cand))
            {
                var candObj = cand.Objective(_inst);
                if (candObj < bestObj)
                {
                    if (_inst.Stops.Count <= 80) Polish(cand);
                    candObj = cand.Objective(_inst);
                    best = cand.Clone();
                    bestObj = candObj;
                    current = cand;
                    curObj = candObj;
                    score = 33;
                }
                else if (Accept(candObj, curObj, temperature, ignoreVehicleTier))
                {
                    score = candObj < curObj ? 9 : 13;
                    current = cand;
                    curObj = candObj;
                }
            }
            _destroyScores[d] += score;
            _repairScores[r] += score;

            temperature *= cooling;
            Iterations++;
            if (++pendingReport == 50) { _onIterations(pendingReport); pendingReport = 0; }
            if ((done + 1) % 100 == 0) UpdateWeights();
        }
        if (pendingReport > 0) _onIterations(pendingReport);
        return (best, done);
    }

    private double _penaltyBase = 600;

    /// <summary>
    /// Simulated annealing on a scalar with finite penalties for unserved stops and vehicles, so
    /// the search can pass through states that temporarily drop a stop or add a vehicle. The best
    /// solution is still tracked with the exact lexicographic objective.
    /// </summary>
    /// <summary>Every route the search may change is feasible (locked routes are kept as they are).</summary>
    private bool AllFeasible(SearchState s)
    {
        for (var v = 0; v < s.Routes.Length; v++)
            if (!_locked[v] && !s.Costs[v].Feasible) return false;
        return true;
    }

    private bool Accept(ObjectiveValue cand, ObjectiveValue cur, double temperature, bool ignoreVehicleTier)
    {
        var delta = Scalar(cand, ignoreVehicleTier) - Scalar(cur, ignoreVehicleTier);
        return delta <= 0 || _rng.NextDouble() < Math.Exp(-delta / temperature);
    }

    private double Scalar(ObjectiveValue o, bool ignoreVehicleTier) =>
        o.UnservedMandatory * 10 * _penaltyBase
        + o.UnservedOptionalPriority * 2 * _penaltyBase
        + (ignoreVehicleTier ? 0 : o.VehiclesUsed * 3 * _penaltyBase)
        + o.TravelSeconds + WorkWeight * o.WorkSeconds;

    private int Roulette(double[] weights)
    {
        var total = 0.0;
        foreach (var w in weights) total += w;
        var x = _rng.NextDouble() * total;
        for (var i = 0; i < weights.Length; i++)
        {
            x -= weights[i];
            if (x < 0) return i;
        }
        return weights.Length - 1;
    }

    private void UpdateWeights()
    {
        const double reaction = 0.1;
        Update(_destroyWeights, _destroyScores, _destroyUses);
        Update(_repairWeights, _repairScores, _repairUses);

        static void Update(double[] w, double[] score, int[] uses)
        {
            for (var i = 0; i < w.Length; i++)
            {
                if (uses[i] > 0) w[i] = Math.Max(0.05, w[i] * (1 - reaction) + reaction * score[i] / uses[i]);
                score[i] = 0;
                uses[i] = 0;
            }
        }
    }

    // ------------------------------------------------------------------ intra-route polish

    /// <summary>
    /// Relocate and 2-opt moves inside each route (first improvement), comparing travel then work
    /// (travel within 1e-9 s counts as equal, so e.g. a reversed route that waits less is taken).
    /// </summary>
    internal void Polish(SearchState s)
    {
        for (var round = 0; round < 10; round++)
        {
            var changed = false;
            for (var v = 0; v < s.Routes.Length; v++)
            {
                if (_locked[v]) continue;
                var f = _frozen[v];
                if (s.Routes[v].Count - f < 2) continue;
                var buf = new int[s.Routes[v].Count];
                for (var pass = 0; pass < 20; pass++)
                {
                    if (_shouldStop()) return;
                    if (!TryImproveRoute(s, v, f, buf)) break;
                    changed = true;
                }
            }
            if (_shouldStop()) return;
            if (InterRoutePass(s)) changed = true;
            if (_shouldStop()) return;
            if (UnservedSwapPass(s)) changed = true;
            if (!changed) return;
        }
    }

    // ------------------------------------------------------------------ choosing which mandatory stop to drop

    /// <summary>
    /// When some mandatory stops cannot be served, the objective still depends on which ones are
    /// dropped: a smaller or better-placed mandatory stop can leave room for optional ones. Repair
    /// inserts by tier and cost, so it keeps re-inserting the same mandatory stop. This move takes
    /// each unserved mandatory stop, removes a related served mandatory stop (the granular
    /// neighbours, which include close time windows), inserts the unserved one at its cheapest
    /// position, then fills the freed room with unserved optional stops (highest priority first).
    /// The first trial that improves the exact objective is applied.
    /// </summary>
    private bool UnservedSwapPass(SearchState s)
    {
        var missing = s.Unassigned.Where(x => _inst.Stops[x].Mandatory).ToArray();
        if (missing.Length == 0) return false;
        var baseline = s.Objective(_inst);
        var neighbours = Neighbours();
        foreach (var u in missing)
        {
            foreach (var m in neighbours[u])
            {
                if (_shouldStop()) return false;
                if (!_inst.Stops[m].Mandatory || !TryLocate(s, m, out var v, out var p)) continue;
                var trial = s.Clone();
                trial.Routes[v].RemoveAt(p);
                Reevaluate(trial, v);
                trial.AddUnassigned(m);
                if (!InsertCheapest(trial, u)) continue;
                foreach (var o in trial.Unassigned.Where(x => !_inst.Stops[x].Mandatory && x != m)
                             .OrderByDescending(x => _inst.Stops[x].Priority).ThenBy(x => x).ToArray())
                    InsertCheapest(trial, o);
                if (trial.Objective(_inst) < baseline)
                {
                    s.CopyFrom(trial);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Finds a stop in a route the search may change (not locked, not in a frozen prefix).</summary>
    private bool TryLocate(SearchState s, int stop, out int vehicle, out int position)
    {
        for (vehicle = 0; vehicle < s.Routes.Length; vehicle++)
        {
            if (_locked[vehicle]) continue;
            position = s.Routes[vehicle].IndexOf(stop);
            if (position >= _frozen[vehicle]) return true;
        }
        vehicle = position = -1;
        return false;
    }

    private bool InsertCheapest(SearchState s, int stop)
    {
        var best = new Insertion(Infeasible, -1);
        var bv = -1;
        for (var v = 0; v < s.Routes.Length; v++)
        {
            var ins = BestInsertion(s, stop, v, 0);
            if (ins.Cost < best.Cost) { best = ins; bv = v; }
        }
        if (bv < 0) return false;
        s.Routes[bv].Insert(best.Position, stop);
        Reevaluate(s, bv);
        s.Unassigned.Remove(stop);
        return true;
    }

    // ------------------------------------------------------------------ inter-route (granular) polish

    private int[][]? _neighbours;

    private int[][] Neighbours()
    {
        if (_neighbours is not null) return _neighbours;
        var n = _inst.Stops.Count;
        _neighbours = new int[n][];
        for (var i = 0; i < n; i++)
        {
            var me = i;
            _neighbours[i] = Enumerable.Range(0, n).Where(j => j != me)
                .OrderBy(j => _related[me][j]).ThenBy(j => j).Take(NeighbourCount).ToArray();
        }
        return _neighbours;
    }

    /// <summary>
    /// One pass of relocate (next to a related stop in another route) and swap (with a related stop
    /// in another route). Applies each move that improves vehicles, then travel, then work.
    /// </summary>
    private bool InterRoutePass(SearchState s)
    {
        var where = new (int Route, int Pos)[_inst.Stops.Count];
        void Index()
        {
            Array.Fill(where, (-1, -1));
            for (var v = 0; v < s.Routes.Length; v++)
                for (var p = 0; p < s.Routes[v].Count; p++) where[s.Routes[v][p]] = (v, p);
        }
        Index();
        var neighbours = Neighbours();
        var improved = false;

        for (var stop = 0; stop < _inst.Stops.Count; stop++)
        {
            if (_shouldStop()) return improved;
            if (TryMoveStop(s, stop, where, neighbours[stop]))
            {
                Index();
                improved = true;
            }
        }
        return improved;
    }

    private bool TryMoveStop(SearchState s, int stop, (int Route, int Pos)[] where, int[] neighbours)
    {
        var (a, i) = where[stop];
        if (a < 0 || _locked[a] || i < _frozen[a]) return false;
        foreach (var other in neighbours)
        {
            var (b, j) = where[other];
            if (b < 0 || b == a || _locked[b] || !_allowed[b]) continue;

            // relocate the stop into b just before / after its neighbour
            if (_canServe(b, stop))
            {
                foreach (var at in new[] { j, j + 1 })
                {
                    if (at < _frozen[b]) continue;
                    if (TwoRouteImprovement(s, a, Without(s.Routes[a], i), b, With(s.Routes[b], at, stop))) return true;
                }
            }

            // swap the stop and its neighbour
            if (j >= _frozen[b] && _canServe(b, stop) && _canServe(a, other))
            {
                var newA = s.Routes[a].ToArray();
                var newB = s.Routes[b].ToArray();
                newA[i] = other;
                newB[j] = stop;
                if (TwoRouteImprovement(s, a, newA, b, newB)) return true;
            }
        }
        return false;
    }

    private static int[] Without(List<int> route, int pos)
    {
        var r = new int[route.Count - 1];
        for (int k = 0, w = 0; k < route.Count; k++) if (k != pos) r[w++] = route[k];
        return r;
    }

    private static int[] With(List<int> route, int pos, int stop)
    {
        var r = new int[route.Count + 1];
        for (int k = 0, w = 0; k <= route.Count; k++) r[k] = k == pos ? stop : route[w++];
        return r;
    }

    private bool TwoRouteImprovement(SearchState s, int a, int[] newA, int b, int[] newB)
    {
        var depA = new double[newA.Length];
        var depB = new double[newB.Length];
        var ca = _eval.EvaluateCost(a, newA, depA);
        if (!ca.Feasible) return false;
        var cb = _eval.EvaluateCost(b, newB, depB);
        if (!cb.Feasible) return false;

        var oldA = s.Costs[a];
        var oldB = s.Costs[b];
        static int Used(int count) => count == 0 ? 0 : 1;
        var dVeh = Used(newA.Length) + Used(newB.Length) - Used(s.Routes[a].Count) - Used(s.Routes[b].Count);
        var dTravel = ca.Travel + cb.Travel - oldA.Travel - oldB.Travel;
        var dWork = ca.Work + cb.Work - oldA.Work - oldB.Work;
        var better = dVeh < 0 || (dVeh == 0 && (dTravel < -1e-9 || (Math.Abs(dTravel) <= 1e-9 && dWork < -1e-9)));
        if (!better) return false;

        s.Routes[a].Clear(); s.Routes[a].AddRange(newA);
        s.Routes[b].Clear(); s.Routes[b].AddRange(newB);
        s.Costs[a] = ca;
        s.Costs[b] = cb;
        s.Departures[a] = depA;
        s.Departures[b] = depB;
        return true;
    }

    private bool TryImproveRoute(SearchState s, int v, int f, int[] buf)
    {
        var route = s.Routes[v];
        var n = route.Count;
        var cur = s.Costs[v];

        bool Better(RouteCost c) => c.Feasible &&
            (c.Travel < cur.Travel - 1e-9 || (Math.Abs(c.Travel - cur.Travel) <= 1e-9 && c.Work < cur.Work - 1e-9));

        // relocate: take the stop at i out, put it at position j of the shortened route
        var tmp = new int[n - 1];
        for (var i = f; i < n; i++)
        {
            var moved = route[i];
            for (int k = 0, w = 0; k < n; k++) if (k != i) tmp[w++] = route[k];
            for (var j = f; j < n; j++)
            {
                if (j == i) continue;
                for (var k = 0; k < n; k++) buf[k] = k < j ? tmp[k] : k == j ? moved : tmp[k - 1];
                var c = _eval.EvaluateCost(v, buf);
                if (Better(c)) { Apply(s, v, buf); return true; }
            }
        }

        // 2-opt: reverse [i..j]
        for (var i = f; i < n - 1; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                for (var k = 0; k < n; k++) buf[k] = k < i || k > j ? route[k] : route[j - (k - i)];
                var c = _eval.EvaluateCost(v, buf);
                if (Better(c)) { Apply(s, v, buf); return true; }
            }
        }
        return false;
    }

    private void Apply(SearchState s, int v, int[] buf)
    {
        var route = s.Routes[v];
        for (var k = 0; k < route.Count; k++) route[k] = buf[k];
        Reevaluate(s, v);
    }

    // ------------------------------------------------------------------ destroy

    private List<(int Vehicle, int Position)> Removable(SearchState s)
    {
        var list = new List<(int, int)>();
        for (var v = 0; v < s.Routes.Length; v++)
        {
            if (_locked[v]) continue;
            for (var p = _frozen[v]; p < s.Routes[v].Count; p++) list.Add((v, p));
        }
        return list;
    }

    private void Destroy(SearchState s, int op)
    {
        var removable = Removable(s);
        if (removable.Count == 0) return;
        var n = removable.Count;
        var qMin = Math.Max(1, Math.Min(4, n / 10));
        var qMax = Math.Min(n, n <= 12 ? n : Math.Max(2, Math.Min(60, (int)(0.4 * n))));
        var q = _rng.Next(Math.Min(qMin, qMax), qMax + 1);
        var chosen = new HashSet<int>();

        switch (op)
        {
            case 0: // random
                while (chosen.Count < q) { var (v, p) = removable[_rng.Next(removable.Count)]; chosen.Add(s.Routes[v][p]); }
                break;
            case 1: // worst (largest travel saving), randomised
            {
                var savings = new List<(double Saving, int Stop)>();
                var buffer = new int[_inst.Stops.Count];
                foreach (var (v, p) in removable)
                {
                    var route = s.Routes[v];
                    var without = buffer.AsSpan(0, route.Count - 1);
                    for (int i = 0, k = 0; i < route.Count; i++) if (i != p) without[k++] = route[i];
                    var c = _eval.EvaluateCost(v, without);
                    var saving = c.Feasible ? s.Costs[v].Travel - c.Travel : 0;
                    savings.Add((saving, route[p]));
                }
                savings.Sort((a, b) => b.Saving != a.Saving ? b.Saving.CompareTo(a.Saving) : a.Stop.CompareTo(b.Stop));
                while (chosen.Count < q)
                {
                    var idx = (int)(Math.Pow(_rng.NextDouble(), 3) * savings.Count);
                    chosen.Add(savings[idx].Stop);
                    savings.RemoveAt(idx);
                }
                break;
            }
            case 2: // related (Shaw)
            {
                var pool = removable.Select(x => s.Routes[x.Vehicle][x.Position]).ToList();
                var seed = pool[_rng.Next(pool.Count)];
                chosen.Add(seed);
                pool.Remove(seed);
                var removed = new List<int> { seed };
                while (chosen.Count < q && pool.Count > 0)
                {
                    var r = removed[_rng.Next(removed.Count)];
                    pool.Sort((a, b) =>
                    {
                        var c = _related[r][a].CompareTo(_related[r][b]);
                        return c != 0 ? c : a.CompareTo(b);
                    });
                    var idx = (int)(Math.Pow(_rng.NextDouble(), 6) * pool.Count);
                    chosen.Add(pool[idx]);
                    removed.Add(pool[idx]);
                    pool.RemoveAt(idx);
                }
                break;
            }
            default: // route removal
            {
                var victim = PickRandomRoute(s);
                if (victim >= 0)
                    for (var p = _frozen[victim]; p < s.Routes[victim].Count; p++) chosen.Add(s.Routes[victim][p]);
                break;
            }
        }

        RemoveStops(s, chosen);
    }

    private int PickRandomRoute(SearchState s)
    {
        var candidates = new List<int>();
        for (var v = 0; v < s.Routes.Length; v++)
            if (!_locked[v] && s.Routes[v].Count > _frozen[v]) candidates.Add(v);
        return candidates.Count == 0 ? -1 : candidates[_rng.Next(candidates.Count)];
    }

    private int PickRouteToEmpty(SearchState s)
    {
        var best = -1;
        for (var v = 0; v < s.Routes.Length; v++)
        {
            if (_locked[v] || _frozen[v] > 0 || s.Routes[v].Count == 0) continue;
            if (best < 0 || s.Routes[v].Count < s.Routes[best].Count) best = v;
        }
        // need at least one other used route to move stops to
        var used = s.Routes.Count(r => r.Count > 0);
        return used >= 2 ? best : -1;
    }

    private void RemoveRoute(SearchState s, int v)
    {
        var stops = new HashSet<int>(s.Routes[v]);
        RemoveStops(s, stops);
    }

    private void RemoveStops(SearchState s, HashSet<int> stops)
    {
        if (stops.Count == 0) return;
        for (var v = 0; v < s.Routes.Length; v++)
        {
            var route = s.Routes[v];
            var before = route.Count;
            for (var p = route.Count - 1; p >= _frozen[v]; p--)
                if (stops.Contains(route[p])) route.RemoveAt(p);
            if (route.Count != before) Reevaluate(s, v);
        }
        foreach (var stop in stops) s.AddUnassigned(stop);
    }

    private void Reevaluate(SearchState s, int v)
    {
        var route = s.Routes[v];
        var deps = new double[route.Count];
        s.Costs[v] = _eval.EvaluateCost(v, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(route), deps);
        s.Departures[v] = deps;
    }

    // ------------------------------------------------------------------ repair

    private readonly record struct Insertion(double Cost, int Position);

    private static int Tier(Stop stop) => stop.Mandatory ? 6 : stop.Priority;

    /// <param name="repairOp">0 greedy, 1 greedy+noise, 2 regret-2, 3 regret-3, 4 random order.</param>
    private void Repair(SearchState s, int repairOp)
    {
        if (s.Unassigned.Count == 0) return;
        if (repairOp == 4) { RepairRandomOrder(s); return; }
        var noise = repairOp == 1 ? 0.15 : 0.0;
        var k = repairOp >= 2 ? repairOp : 1;
        var vehicles = s.Routes.Length;
        var pending = new List<int>(s.Unassigned);
        var table = new Dictionary<int, Insertion[]>();
        foreach (var stop in pending) table[stop] = ComputeRow(s, stop, noise);

        while (pending.Count > 0 && !_shouldStop())
        {
            var bestStop = -1;
            var bestTier = -1;
            double bestKey = double.NegativeInfinity, bestCost = Infeasible;
            foreach (var stop in pending)
            {
                var row = table[stop];
                var c1 = Infeasible;
                foreach (var ins in row) if (ins.Cost < c1) c1 = ins.Cost;
                if (double.IsPositiveInfinity(c1)) continue;
                var tier = Tier(_inst.Stops[stop]);
                double key;
                if (k == 1) key = -c1;
                else
                {
                    var costs = row.Select(x => x.Cost).OrderBy(x => x).Take(k).ToArray();
                    key = 0;
                    for (var i = 1; i < k; i++) key += (i < costs.Length && !double.IsPositiveInfinity(costs[i]) ? costs[i] : c1 + 1e7) - c1;
                }
                if (tier > bestTier || (tier == bestTier && (key > bestKey || (key == bestKey && c1 < bestCost))))
                {
                    bestTier = tier;
                    bestKey = key;
                    bestCost = c1;
                    bestStop = stop;
                }
            }
            if (bestStop < 0) break;

            var bestRow = table[bestStop];
            var bv = 0;
            for (var v = 1; v < vehicles; v++) if (bestRow[v].Cost < bestRow[bv].Cost) bv = v;
            s.Routes[bv].Insert(bestRow[bv].Position, bestStop);
            Reevaluate(s, bv);
            s.Unassigned.Remove(bestStop);
            pending.Remove(bestStop);
            table.Remove(bestStop);
            foreach (var stop in pending) table[stop][bv] = BestInsertion(s, stop, bv, noise);
        }
    }

    /// <summary>Inserts stops one by one in random order (within tier) at their cheapest position.</summary>
    private void RepairRandomOrder(SearchState s)
    {
        var order = s.Unassigned.Select(x => (Stop: x, Tier: Tier(_inst.Stops[x]), Key: _rng.NextDouble())).ToList();
        order.Sort((a, b) => a.Tier != b.Tier ? b.Tier.CompareTo(a.Tier) : a.Key.CompareTo(b.Key));
        foreach (var (stop, _, _) in order)
        {
            if (_shouldStop()) return;
            var best = new Insertion(Infeasible, -1);
            var bv = -1;
            for (var v = 0; v < s.Routes.Length; v++)
            {
                var ins = BestInsertion(s, stop, v, 0, Blink);
                if (ins.Cost < best.Cost) { best = ins; bv = v; }
            }
            if (bv < 0) continue;
            s.Routes[bv].Insert(best.Position, stop);
            Reevaluate(s, bv);
            s.Unassigned.Remove(stop);
        }
    }

    private Insertion[] ComputeRow(SearchState s, int stop, double noise)
    {
        var row = new Insertion[s.Routes.Length];
        for (var v = 0; v < row.Length; v++) row[v] = BestInsertion(s, stop, v, noise);
        return row;
    }

    private Insertion BestInsertion(SearchState s, int stop, int v, double noise, double blink = 0)
    {
        var none = new Insertion(Infeasible, -1);
        if (_locked[v] || !_allowed[v] || !_canServe(v, stop) || !s.Costs[v].Feasible) return none;
        var route = s.Routes[v];
        var veh = _inst.Vehicles[v];
        var st = _inst.Stops[stop];
        int meals = st.Meals, boxes = st.Boxes;
        foreach (var x in route) { meals += _inst.Stops[x].Meals; boxes += _inst.Stops[x].Boxes; }
        if (meals > veh.MealCapacity || boxes > veh.BoxCapacity) return none;

        var n = route.Count;
        var cur = s.Costs[v];
        var deps = s.Departures[v];
        Span<int> cand = stackalloc int[n + 1];
        var best = none;
        for (var p = _frozen[v]; p <= n; p++)
        {
            if (p >= 1)
            {
                // Departures along the route increase, so once the predecessor leaves after the
                // window end every later position is infeasible too.
                if (deps[p - 1] > st.WindowEnd) break;
                var arr = _eval.Oracle.ArrivalTime(_inst.StopLocation(route[p - 1]), _inst.StopLocation(stop), deps[p - 1]);
                if (arr > st.WindowEnd) continue;
            }
            if (blink > 0 && _rng.NextDouble() < blink) continue; // SISR-style "blink": skip a position at random
            for (int i = 0, j = 0; i <= n; i++) cand[i] = i == p ? stop : route[j++];
            var c = _eval.EvaluateCost(v, cand);
            if (!c.Feasible) continue;
            var delta = (n == 0 ? VehicleWeight : 0) + (c.Travel - cur.Travel) + WorkWeight * (c.Work - cur.Work);
            if (noise > 0) delta *= 1 + noise * (2 * _rng.NextDouble() - 1);
            if (delta < best.Cost) best = new Insertion(delta, p);
        }
        return best;
    }
}
