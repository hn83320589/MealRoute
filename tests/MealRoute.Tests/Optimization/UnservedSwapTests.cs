using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Optimization;

/// <summary>
/// When not every mandatory stop fits, the objective still depends on which one is dropped:
/// dropping a larger one can leave room for optional stops. Reaching that needs a swap of a served
/// mandatory stop for an unserved one plus an optional insertion in one move.
/// </summary>
public class UnservedSwapTests
{
    private static ProblemInstance SwapInstance()
    {
        // One vehicle with 4 meals. M1 (3 meals) or M2 (2 meals) fits, not both; O (2 meals,
        // priority 4) fits only next to M2.
        return Instance(new[]
        {
            Stop("M1", 100, meals: 3),
            Stop("M2", 200, meals: 2),
            Stop("O", 300, meals: 2, mandatory: false, priority: 4),
        }, new[] { Vehicle(meals: 4) });
    }

    private static AlnsRun Run(ProblemInstance inst)
    {
        var n = inst.Stops.Count;
        var related = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, n).Select(j => (double)Math.Abs(i - j)).ToArray()).ToArray();
        return new AlnsRun(inst, new RouteEvaluator(inst, Oracle(inst)), new Rng(1), new int[inst.Vehicles.Count],
            new bool[inst.Vehicles.Count], (_, _) => true, related, () => false, _ => { });
    }

    [Fact]
    public void Polish_swaps_which_mandatory_stop_is_dropped_when_that_frees_room_for_an_optional_stop()
    {
        var inst = SwapInstance();
        var s = new SearchState(1);
        s.Routes[0].Add(0);
        s.Departures[0] = new double[1];
        s.Costs[0] = new RouteEvaluator(inst, Oracle(inst)).EvaluateCost(0, new[] { 0 }, s.Departures[0]);
        s.Unassigned.AddRange(new[] { 1, 2 });
        var before = s.Objective(inst);
        Assert.Equal(1, before.UnservedMandatory);
        Assert.Equal(4, before.UnservedOptionalPriority);

        Run(inst).Polish(s);

        var after = s.Objective(inst);
        Assert.Equal(1, after.UnservedMandatory);
        Assert.Equal(0, after.UnservedOptionalPriority);
        Assert.Equal(new[] { 0 }, s.Unassigned);
        Assert.True(s.Costs[0].Feasible);
    }

    [Fact]
    public void Optimizer_drops_the_mandatory_stop_that_leaves_room_for_optional_ones()
    {
        var inst = SwapInstance();
        var res = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = 3, MaxIterations = 50, Runs = 1 });
        Assert.Equal(1, res.Objective.UnservedMandatory);
        Assert.Equal(0, res.Objective.UnservedOptionalPriority);
        Assert.Equal("M1", inst.Stops[Assert.Single(res.Solution.Unserved).Stop].Id);
    }
}
