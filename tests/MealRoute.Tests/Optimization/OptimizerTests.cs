using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Tests.Evaluation;
using MealRoute.Tests.Reference;
using MealRoute.Tests.Travel;
using Microsoft.Extensions.Time.Testing;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Optimization;

public class OptimizerTests
{
    internal static OptimizationResult Optimize(ProblemInstance inst, OptimizerOptions? options = null, LockSet? locks = null,
        Solution? initial = null, IProgress<OptimizationProgress>? progress = null, CancellationToken ct = default)
    {
        var oracle = Oracle(inst);
        return new Optimizer(inst, oracle, oracle).Run(initial, locks ?? LockSet.Empty,
            options ?? new OptimizerOptions { MaxIterations = 1_000, Runs = 2, Seed = 7 }, progress, ct);
    }

    internal static RefFeasibilityChecker.Report Check(ProblemInstance inst, Solution sol, bool requireAllMandatory = true) =>
        RefFeasibilityChecker.Check(inst, sol, Oracle(inst).ArrivalTime, 1e-6, requireAllMandatory);

    private static ProblemInstance Medium(int seed, int stops = 30, int vehicles = 4) =>
        RouteEvaluatorPropertyTests.RandomStaticInstance(new Random(seed), stops, vehicles, scale: 2_500);

    [Fact]
    public void Result_is_feasible_according_to_the_reference_checker()
    {
        var inst = Medium(1);
        var res = Optimize(inst);
        var rep = Check(inst, res.Solution, requireAllMandatory: false);
        Assert.True(rep.Feasible, rep.ToString());
        Assert.Equal(ObjectiveValue.Compute(inst, res.Solution), res.Objective);
    }

    [Fact]
    public void Same_seed_and_iteration_limit_give_identical_results_regardless_of_thread_count()
    {
        var inst = Medium(2);
        OptimizationResult Go(int threads) => Optimize(inst, new OptimizerOptions { Seed = 42, MaxIterations = 800, Runs = 4, MaxDegreeOfParallelism = threads });
        var a = Go(1);
        var b = Go(4);
        var c = Go(3);
        Assert.Equal(a.Objective, b.Objective);
        Assert.Equal(a.Objective, c.Objective);
        for (var v = 0; v < inst.Vehicles.Count; v++)
        {
            Assert.Equal(a.Solution.Routes[v].Stops, b.Solution.Routes[v].Stops);
            Assert.Equal(a.Solution.Routes[v].Visits, b.Solution.Routes[v].Visits);
            Assert.Equal(a.Solution.Routes[v].Stops, c.Solution.Routes[v].Stops);
        }
        Assert.Equal(a.Solution.Unserved, b.Solution.Unserved);
    }

    [Fact]
    public void Mandatory_stop_is_served_before_any_optional_stops()
    {
        // capacity 3 meals: either the mandatory stop (3 meals) or both optional ones (priority 5 each)
        var inst = Instance(new[]
        {
            Stop("M", 100, meals: 3),
            Stop("O1", 200, meals: 1, mandatory: false, priority: 5),
            Stop("O2", 300, meals: 1, mandatory: false, priority: 5),
        }, new[] { Vehicle(meals: 3) });
        var res = Optimize(inst);
        Assert.Equal(new[] { 0 }, res.Solution.Routes[0].Stops);
        Assert.Equal(new ObjectiveValue(0, 10, 1, 200, 200), res.Objective);
    }

    [Fact]
    public void Higher_priority_optional_stops_are_preferred_when_not_all_fit()
    {
        var inst = Instance(new[]
        {
            Stop("low", 100, meals: 2, mandatory: false, priority: 1),
            Stop("high", 900, meals: 2, mandatory: false, priority: 4),
        }, new[] { Vehicle(meals: 2) });
        var res = Optimize(inst);
        Assert.Equal(new[] { 1 }, res.Solution.Routes[0].Stops);
        Assert.Equal(1, res.Objective.UnservedOptionalPriority);
    }

    [Fact]
    public void Unservable_mandatory_stop_is_listed_with_its_reason()
    {
        var unreachableInTime = new Stop("late", "陳爺爺", new GeoPoint(0, 5000), "", "", 1, 0, 0, 0, 1000, true, 1);
        var inst = Instance(new[] { Stop("ok", 100), unreachableInTime }, new[] { Vehicle(start: 0) });
        var res = Optimize(inst);
        var u = Assert.Single(res.Solution.Unserved);
        Assert.Equal(1, u.Stop);
        Assert.Contains("任何車輛單獨前往也無法服務", u.Reason, StringComparison.Ordinal);
        Assert.Contains("陳爺爺", u.Reason, StringComparison.Ordinal);
        Assert.Equal(1, res.Objective.UnservedMandatory);
    }

    [Fact]
    public void Capacity_limited_unserved_stop_gets_a_capacity_reason()
    {
        var inst = Instance(new[] { Stop("a", 100, meals: 2), Stop("b", 200, meals: 2) }, new[] { Vehicle(meals: 2) });
        var res = Optimize(inst);
        var u = Assert.Single(res.Solution.Unserved);
        Assert.Contains("容量", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Fewer_vehicles_are_preferred_over_shorter_travel()
    {
        // Two stops on opposite sides: one vehicle can do both (longer trip) — must use one vehicle.
        var inst = Instance(new[] { Stop("w", -1000), Stop("e", 1000) }, new[] { Vehicle("A"), Vehicle("B") });
        var res = Optimize(inst);
        Assert.Equal(1, res.Objective.VehiclesUsed);
    }

    [Fact]
    public void Progress_is_monotone_and_reaches_one()
    {
        var inst = Medium(3);
        var reports = new List<OptimizationProgress>();
        var res = Optimize(inst, progress: new SynchronousProgress<OptimizationProgress>(reports.Add));
        Assert.NotEmpty(reports);
        for (var i = 1; i < reports.Count; i++) Assert.True(reports[i].Fraction >= reports[i - 1].Fraction);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.False(res.Cancelled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void Cancelling_at_any_point_returns_a_complete_feasible_solution(int cancelAfterReports)
    {
        var inst = Medium(4, stops: 40);
        using var cts = new CancellationTokenSource();
        if (cancelAfterReports == 0) cts.Cancel();
        var count = 0;
        var reports = new List<double>();
        var progress = new SynchronousProgress<OptimizationProgress>(p =>
        {
            reports.Add(p.Fraction);
            if (++count == cancelAfterReports) cts.Cancel();
        });
        var res = Optimize(inst, new OptimizerOptions { MaxIterations = 20_000, Runs = 2, Seed = 3 }, progress: progress, ct: cts.Token);

        Assert.True(res.Cancelled);
        var rep = Check(inst, res.Solution, requireAllMandatory: false);
        Assert.True(rep.Feasible, rep.ToString());
        for (var i = 1; i < reports.Count; i++) Assert.True(reports[i] >= reports[i - 1]);
    }

    [Fact]
    public void Time_limit_stops_the_search_via_time_provider()
    {
        var inst = Medium(5);
        var clock = new FakeTimeProvider();
        var progress = new SynchronousProgress<OptimizationProgress>(_ => clock.Advance(TimeSpan.FromSeconds(1)));
        var res = Optimize(inst, new OptimizerOptions { MaxIterations = 100_000, Runs = 1, TimeLimit = TimeSpan.FromSeconds(5), TimeProvider = clock }, progress: progress);
        Assert.True(res.Iterations < 100_000);
        Assert.False(res.Cancelled);
        Assert.True(Check(inst, res.Solution, requireAllMandatory: false).Feasible);
    }
}
