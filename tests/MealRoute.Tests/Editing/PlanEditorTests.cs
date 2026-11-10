using System.Diagnostics;
using MealRoute.Core.Editing;
using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Tests.Evaluation;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Optimization;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Editing;

public class PlanEditorTests
{
    internal static PlanEditor Editor(ProblemInstance inst, Solution sol)
    {
        var oracle = Oracle(inst);
        return new PlanEditor(inst, oracle, oracle, sol);
    }

    internal static (ProblemInstance Inst, Solution Sol) Solved(int seed, int stops = 30, int vehicles = 4)
    {
        var inst = RouteEvaluatorPropertyTests.RandomStaticInstance(new Random(seed), stops, vehicles, scale: 2_500);
        var sol = OptimizerTests.Optimize(inst, new OptimizerOptions { MaxIterations = 200, Runs = 1, Seed = (ulong)seed }).Solution;
        return (inst, sol);
    }

    /// <summary>Independent full recomputation: evaluate every route from scratch.</summary>
    private static (bool Feasible, ObjectiveValue Objective) Full(ProblemInstance inst, IReadOnlyList<IReadOnlyList<int>> routes)
    {
        var ev = new RouteEvaluator(inst, Oracle(inst));
        var plans = new List<RoutePlan>();
        var feasible = true;
        var served = new HashSet<int>();
        for (var v = 0; v < routes.Count; v++)
        {
            var e = ev.Evaluate(v, routes[v]);
            feasible &= e.IsFeasible;
            plans.Add(e.IsEmpty ? RoutePlan.Empty(v) : e.ToPlan());
            served.UnionWith(routes[v]);
        }
        var unserved = Enumerable.Range(0, inst.Stops.Count).Where(s => !served.Contains(s));
        return (feasible, ObjectiveValue.Compute(inst, plans, unserved));
    }

    [Fact]
    public void Moving_a_stop_between_routes_previews_feasibility_and_objective_change()
    {
        var inst = Instance(new[] { Stop("A", 100), Stop("B", 200), Stop("C", -300) }, new[] { Vehicle("V1"), Vehicle("V2") });
        var sol = new Solution(new[]
        {
            Editor(inst, new Solution(new[] { RoutePlan.Empty(0), RoutePlan.Empty(1) }, Array.Empty<UnservedStop>())).PlanOf(0, new[] { 0, 1 }),
            Editor(inst, new Solution(new[] { RoutePlan.Empty(0), RoutePlan.Empty(1) }, Array.Empty<UnservedStop>())).PlanOf(1, new[] { 2 }),
        }, Array.Empty<UnservedStop>());
        var ed = Editor(inst, sol);
        Assert.Equal(new ObjectiveValue(0, 0, 2, 400 + 600, 400 + 600), ed.Objective);

        var p = ed.Preview(new MoveStop(2, 0, 2)); // C to the end of V1
        Assert.True(p.IsAllowed);
        Assert.True(p.Feasible);
        Assert.Equal(1, p.After.VehiclesUsed);
        Assert.Equal(-1, p.After.VehiclesUsed - p.Before.VehiclesUsed);
        Assert.Equal(200 + 500 + 300, p.After.TravelSeconds, 6);
    }

    [Fact]
    public void Infeasible_preview_reports_the_first_violation_with_stop_name_and_window()
    {
        var late = new Stop("w", "王奶奶", new(0, 4200), "", "", 1, 0, 0, 11.5 * 3600, 12 * 3600, true, 1);
        var inst = Instance(new[] { Stop("A", 100), late }, new[] { Vehicle("V1", start: 11 * 3600), Vehicle("V2", start: 0) });
        var empty = new Solution(new[] { RoutePlan.Empty(0), RoutePlan.Empty(1) }, new[] { new UnservedStop(0, ""), new UnservedStop(1, "") });
        var ed = Editor(inst, empty);
        var p = ed.Preview(new MoveStop(1, 0, 0));
        Assert.False(p.Feasible);
        Assert.Equal("王奶奶 12:10 到，時間窗 11:30–12:00", p.FirstViolation!.Message);
        Assert.True(ed.Preview(new MoveStop(1, 1, 0)).Feasible);
    }

    [Fact]
    public void Apply_changes_the_plan_and_undo_restores_it()
    {
        var (inst, sol) = Solved(1);
        var ed = Editor(inst, sol);
        var before = ed.Objective;
        var stop = sol.Routes.First(r => !r.IsEmpty).Stops[0];
        Assert.True(ed.Apply(new MoveStop(stop, -1, 0)).Applied); // unassign
        Assert.Contains(stop, ed.Unserved);
        Assert.NotEqual(before, ed.Objective);
        ed.Undo();
        Assert.Equal(before, ed.Objective);
        Assert.DoesNotContain(stop, ed.Unserved);
        ed.Redo();
        Assert.Contains(stop, ed.Unserved);
    }

    [Fact]
    public void Infeasible_moves_are_not_applied_unless_forced()
    {
        var inst = Instance(new[] { Stop("A", 100, 0, 50) }, new[] { Vehicle() });
        var ed = Editor(inst, new Solution(new[] { RoutePlan.Empty(0) }, new[] { new UnservedStop(0, "") }));
        Assert.False(ed.Apply(new MoveStop(0, 0, 0)).Applied);
        Assert.Empty(ed.Routes[0]);
        Assert.True(ed.Apply(new MoveStop(0, 0, 0), allowInfeasible: true).Applied);
        Assert.Equal(new[] { 0 }, ed.Routes[0]);
        Assert.False(ed.CurrentEvaluation(0).IsFeasible);
    }

    [Fact]
    public void Locked_stop_cannot_be_moved_and_positions_before_it_are_frozen()
    {
        var (inst, sol) = Solved(2);
        var ed = Editor(inst, sol);
        var v = Enumerable.Range(0, inst.Vehicles.Count).First(x => sol.Routes[x].Stops.Count >= 3);
        var locked = sol.Routes[v].Stops[1];
        ed.LockStop(locked);

        var moveLocked = ed.Preview(new MoveStop(locked, v, 0));
        Assert.False(moveLocked.IsAllowed);
        Assert.Contains("鎖定", moveLocked.RejectReason, StringComparison.Ordinal);

        var before = ed.Preview(new MoveStop(sol.Routes[v].Stops[0], v, 2));
        Assert.False(before.IsAllowed); // would shift the locked stop

        var after = ed.Preview(new MoveStop(sol.Routes[v].Stops[^1], -1, 0));
        Assert.True(after.IsAllowed);
        Assert.Equal((v, 1), ed.Locks.LockedStops[locked]);
    }

    [Fact]
    public void Locked_route_rejects_all_changes()
    {
        var (inst, sol) = Solved(3);
        var ed = Editor(inst, sol);
        var v = Enumerable.Range(0, inst.Vehicles.Count).First(x => !sol.Routes[x].IsEmpty);
        ed.LockRoute(v);
        Assert.False(ed.Preview(new MoveStop(sol.Routes[v].Stops[^1], -1, 0)).IsAllowed);
        var other = sol.Unserved.Count > 0 ? sol.Unserved[0].Stop : sol.Routes.First(r => r.Vehicle != v && !r.IsEmpty).Stops[0];
        Assert.False(ed.Preview(new MoveStop(other, v, 0)).IsAllowed);
        ed.UnlockRoute(v);
        Assert.True(ed.Preview(new MoveStop(sol.Routes[v].Stops[^1], -1, 0)).IsAllowed);
    }

    [Fact]
    public void Property_incremental_preview_equals_full_recomputation_for_random_moves()
    {
        Property.ForAll(70_000, 40, (r, seed) =>
        {
            var (inst, sol) = Solved(seed, stops: r.Next(10, 40), vehicles: r.Next(2, 6));
            var ed = Editor(inst, sol);
            for (var step = 0; step < 30; step++)
            {
                var move = RandomMove(r, inst, ed);
                var preview = ed.Preview(move);
                var routes = ed.RoutesAfter(move);
                var (feasible, objective) = Full(inst, routes);
                Assert.Equal(feasible, preview.Feasible);
                Assert.Equal(objective, preview.After);
                if (r.NextDouble() < 0.5) ed.Apply(move, allowInfeasible: true);
                var (_, current) = Full(inst, ed.Routes);
                Assert.Equal(current, ed.FastObjective);
            }
        });
    }

    [Fact]
    public void Property_exact_preview_equals_full_recomputation_with_the_exact_oracle()
    {
        Property.ForAll(71_000, 20, (r, seed) =>
        {
            var (inst, sol) = Solved(seed, stops: 20, vehicles: 3);
            var ed = Editor(inst, sol);
            for (var step = 0; step < 15; step++)
            {
                var move = RandomMove(r, inst, ed);
                var (feasible, objective) = Full(inst, ed.RoutesAfter(move));
                var exact = ed.PreviewExact(move);
                Assert.Equal(feasible, exact.Feasible);
                Assert.Equal(objective, exact.After);
                ed.Apply(move, allowInfeasible: true);
            }
        });
    }

    internal static Move RandomMove(Random r, ProblemInstance inst, PlanEditor ed)
    {
        var stop = r.Next(inst.Stops.Count);
        if (r.NextDouble() < 0.3)
        {
            var other = r.Next(inst.Stops.Count);
            return new SwapStops(stop, other);
        }
        var v = r.NextDouble() < 0.15 ? -1 : r.Next(inst.Vehicles.Count);
        var len = v < 0 ? 0 : ed.Routes[v].Count - (ed.Routes[v].Contains(stop) ? 1 : 0);
        return new MoveStop(stop, v, v < 0 ? 0 : r.Next(len + 1));
    }

    [Fact]
    public void Incremental_preview_median_is_under_one_millisecond_for_200_stops_and_15_vehicles()
    {
        var inst = RouteEvaluatorPropertyTests.RandomStaticInstance(new Random(9), 200, 15, scale: 3_000);
        var sol = OptimizerTests.Optimize(inst, new OptimizerOptions { MaxIterations = 50, Runs = 1, Seed = 9 }).Solution;
        var ed = Editor(inst, sol);
        var r = new Random(1);
        var times = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            var move = RandomMove(r, inst, ed);
            var sw = Stopwatch.StartNew();
            ed.Preview(move);
            sw.Stop();
            if (i >= 50) times.Add(sw.Elapsed.TotalMilliseconds); // skip warm-up
        }
        times.Sort();
        var median = times[times.Count / 2];
        Assert.True(median <= 1.0, $"median {median:F3} ms");
    }
}
