using MealRoute.Core.Editing;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Tests.Editing;
using MealRoute.Tests.Infrastructure;

namespace MealRoute.Tests.Optimization;

public class LockTests
{
    [Fact]
    public void Property_reoptimisation_keeps_locked_stops_in_place_and_locked_routes_unchanged()
    {
        Property.ForAll(80_000, 12, (r, seed) =>
        {
            var (inst, sol) = PlanEditorTests.Solved(seed, stops: 40, vehicles: 5);
            var ed = PlanEditorTests.Editor(inst, sol);
            var used = Enumerable.Range(0, inst.Vehicles.Count).Where(v => sol.Routes[v].Stops.Count >= 2).ToList();
            if (used.Count < 2) return;

            var lockedRoute = used[r.Next(used.Count)];
            ed.LockRoute(lockedRoute);
            var lockedStops = new List<int>();
            foreach (var v in used.Where(v => v != lockedRoute))
            {
                var stops = sol.Routes[v].Stops;
                var stop = stops[r.Next(stops.Count)];
                ed.LockStop(stop);
                lockedStops.Add(stop);
            }
            var locks = ed.Locks;

            var res = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = (ulong)seed + 1, MaxIterations = 600, Runs = 2 }, locks, sol);

            Assert.Equal(sol.Routes[lockedRoute].Stops, res.Solution.Routes[lockedRoute].Stops);
            Assert.Equal(sol.Routes[lockedRoute].Visits, res.Solution.Routes[lockedRoute].Visits);
            foreach (var stop in lockedStops)
            {
                var (v, pos) = locks.LockedStops[stop];
                Assert.True(res.Solution.Routes[v].Stops.Count > pos, $"route {v} too short");
                Assert.Equal(stop, res.Solution.Routes[v].Stops[pos]);
            }
            Assert.True(OptimizerTests.Check(inst, res.Solution, requireAllMandatory: false).Feasible);
            // never worse than the locked starting point
            Assert.True(res.Objective.CompareTo(Core.Evaluation.ObjectiveValue.Compute(inst, sol)) <= 0);
        });
    }

    [Fact]
    public void Locks_that_do_not_match_the_initial_solution_are_rejected()
    {
        var (inst, sol) = PlanEditorTests.Solved(5);
        var v = Enumerable.Range(0, inst.Vehicles.Count).First(x => sol.Routes[x].Stops.Count >= 2);
        var wrong = new LockSet(new HashSet<int>(), new Dictionary<int, (int, int)> { [sol.Routes[v].Stops[0]] = (v, 1) });
        var ex = Assert.Throws<InvalidOperationException>(() => OptimizerTests.Optimize(inst, locks: wrong, initial: sol));
        Assert.Contains("鎖定", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Frozen_prefix_covers_positions_up_to_the_last_locked_stop()
    {
        var locks = new LockSet(new HashSet<int> { 2 }, new Dictionary<int, (int, int)> { [7] = (0, 3), [9] = (0, 1), [4] = (1, 0) });
        Assert.Equal(4, locks.FrozenPrefixLength(0, 10));
        Assert.Equal(1, locks.FrozenPrefixLength(1, 5));
        Assert.Equal(6, locks.FrozenPrefixLength(2, 6));
        Assert.Equal(0, locks.FrozenPrefixLength(3, 6));
    }

    [Fact]
    public void Manual_edit_then_reoptimise_keeps_the_users_lock()
    {
        var (inst, sol) = PlanEditorTests.Solved(6, stops: 25, vehicles: 3);
        var ed = PlanEditorTests.Editor(inst, sol);
        var from = Enumerable.Range(0, 3).First(v => sol.Routes[v].Stops.Count >= 1);
        var to = Enumerable.Range(0, 3).First(v => v != from);
        var stop = sol.Routes[from].Stops[0];
        var applied = ed.Apply(new MoveStop(stop, to, 0), allowInfeasible: false);
        if (!applied.Applied) return; // move happened to be infeasible for this seed
        ed.LockStop(stop);
        var res = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = 2, MaxIterations = 400, Runs = 2 }, ed.Locks, ed.ToSolution());
        Assert.Equal(stop, res.Solution.Routes[to].Stops[0]);
    }
}
