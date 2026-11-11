using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Network;
using MealRoute.Core.Optimization;
using MealRoute.Core.Travel;
using MealRoute.Tests.Evaluation;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Network;
using MealRoute.Tests.Reference;
using MealRoute.Tests.Travel;
using Xunit.Abstractions;

namespace MealRoute.Tests.Optimization;

public class OptimizerPropertyTests(ITestOutputHelper output)
{
    /// <summary>Small static instance for exhaustive comparison; some need breaks.</summary>
    internal static ProblemInstance ExhaustiveInstance(Random r)
    {
        var withBreaks = r.NextDouble() < 0.25;
        var n = withBreaks ? r.Next(3, 7) : r.Next(3, 10);
        var V = r.Next(1, 4);
        var scale = withBreaks ? 4_500 : 1_500;
        var stops = new List<Stop>();
        for (var i = 0; i < n; i++)
        {
            var x = (r.NextDouble() - 0.5) * 2 * scale;
            var y = (r.NextDouble() - 0.5) * 2 * scale;
            var a = r.NextDouble() * (withBreaks ? 15_000 : 6_000);
            var w = r.NextDouble() < 0.3 ? 40_000 : 900 + r.NextDouble() * 5_000;
            stops.Add(new Stop($"s{i}", $"站{i}", new GeoPoint(y, x), "", "", r.Next(1, 4), r.Next(0, 2), r.Next(0, 3) * 120,
                a, a + w, r.NextDouble() < 0.5, r.Next(1, 6)));
        }
        var vehicles = Enumerable.Range(0, V).Select(v => new Vehicle($"V{v}", $"D{v}", default, default,
            r.NextDouble() * 1_000, withBreaks ? 60_000 : 20_000 + r.NextDouble() * 10_000,
            r.Next(4, 12), r.Next(1, 4), withBreaks ? 50_000 : 9_000 + r.NextDouble() * 12_000)).ToArray();
        return new ProblemInstance(stops, vehicles, BreakRule.Default);
    }

    [Fact]
    public void Property_optimizer_matches_exhaustive_optimum_on_200_small_instances()
    {
        const int count = 200;
        int same = 0, compared = 0;
        var worstGap = 0.0;
        var failures = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var seed = 50_000 + i;
            var inst = ExhaustiveInstance(new Random(seed));
            var exhaustive = RefExhaustiveSolver.Solve(inst);
            var res = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = (ulong)seed, MaxIterations = 4_000, Runs = 4 });
            var o = res.Objective;
            compared++;

            var tiersEqual = o.UnservedMandatory == exhaustive.UnservedMandatory && o.UnservedOptionalPriority == exhaustive.UnservedPriority && o.VehiclesUsed == exhaustive.Vehicles;
            if (tiersEqual && Math.Abs(o.TravelSeconds - exhaustive.Travel) <= 1e-6 && Math.Abs(o.WorkSeconds - exhaustive.Work) <= 1.0)
            {
                same++;
                continue;
            }
            // Never better than the optimum (would indicate a reference or checker bug).
            var ours = new RefExhaustiveSolver.Objective(o.UnservedMandatory, o.UnservedOptionalPriority, o.VehiclesUsed, o.TravelSeconds, o.WorkSeconds - 1.0);
            Assert.False(RefExhaustiveSolver.Less(ours, exhaustive) && !tiersEqual, $"seed {seed}: optimizer {o} beats exhaustive {exhaustive}");
            var gap = tiersEqual ? (o.TravelSeconds - exhaustive.Travel) / Math.Max(1, exhaustive.Travel) : double.PositiveInfinity;
            worstGap = Math.Max(worstGap, gap);
            failures.Add($"seed {seed}: ours {o} vs exhaustive {exhaustive} (gap {gap:P2})");
        }
        output.WriteLine($"identical {same}/{compared}, worst gap {worstGap:P2}");
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(same >= 0.95 * compared, $"only {same}/{compared} identical. " + string.Join(" | ", failures.Take(5)));
        Assert.True(worstGap <= 0.02, $"worst gap {worstGap:P2}. " + string.Join(" | ", failures.Take(5)));
    }

    /// <summary>
    /// Lunch and dinner windows with a tight work-time limit: routes are only feasible when the
    /// departure absorbs the gap, and capacity is sometimes too small to serve every mandatory stop.
    /// </summary>
    internal static ProblemInstance TwoShiftInstance(Random r)
    {
        var n = r.Next(4, 10);
        var V = r.Next(1, 4);
        var stops = new List<Stop>();
        for (var i = 0; i < n; i++)
        {
            var x = (r.NextDouble() - 0.5) * 3_000;
            var y = (r.NextDouble() - 0.5) * 3_000;
            var a = r.NextDouble() < 0.5 ? 3_000 + r.NextDouble() * 1_000 : 12_000 + r.NextDouble() * 1_000;
            stops.Add(new Stop($"s{i}", $"站{i}", new GeoPoint(y, x), "", "", r.Next(1, 4), r.Next(0, 2), r.Next(0, 3) * 120,
                a, a + 600 + r.NextDouble() * 1_500, r.NextDouble() < 0.6, r.Next(1, 6)));
        }
        var vehicles = Enumerable.Range(0, V).Select(v => new Vehicle($"V{v}", $"D{v}", default, default,
            r.NextDouble() * 500, 20_000, r.Next(3, 10), r.Next(1, 4), 2_500 + r.NextDouble() * 9_000)).ToArray();
        return new ProblemInstance(stops, vehicles, BreakRule.Default);
    }

    [Fact]
    public void Property_optimizer_matches_exhaustive_optimum_with_lunch_and_dinner_windows()
    {
        const int count = 120;
        var same = 0;
        var failures = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var seed = 52_000 + i;
            var inst = TwoShiftInstance(new Random(seed));
            var exhaustive = RefExhaustiveSolver.Solve(inst);
            var o = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = (ulong)seed, MaxIterations = 4_000, Runs = 4 }).Objective;
            var tiersEqual = o.UnservedMandatory == exhaustive.UnservedMandatory && o.UnservedOptionalPriority == exhaustive.UnservedPriority && o.VehiclesUsed == exhaustive.Vehicles;
            if (tiersEqual && Math.Abs(o.TravelSeconds - exhaustive.Travel) <= 1e-6 && Math.Abs(o.WorkSeconds - exhaustive.Work) <= 1.0) { same++; continue; }
            var ours = new RefExhaustiveSolver.Objective(o.UnservedMandatory, o.UnservedOptionalPriority, o.VehiclesUsed, o.TravelSeconds, o.WorkSeconds - 1.0);
            Assert.False(RefExhaustiveSolver.Less(ours, exhaustive) && !tiersEqual, $"seed {seed}: optimizer {o} beats exhaustive {exhaustive}");
            failures.Add($"seed {seed}: ours {o} vs exhaustive {exhaustive}");
        }
        output.WriteLine($"identical {same}/{count}");
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(same >= 0.95 * count, $"only {same}/{count} identical. " + string.Join(" | ", failures.Take(5)));
    }

    [Fact]
    public void Property_every_optimized_static_solution_passes_the_reference_checker()
    {
        Property.ForAll(60_000, 25, (r, seed) =>
        {
            var inst = RouteEvaluatorPropertyTests.RandomStaticInstance(r, r.Next(5, 35), r.Next(1, 5), scale: r.NextDouble() < 0.7 ? 2_500 : 7_000);
            var res = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = (ulong)seed, MaxIterations = 300, Runs = 2 });
            var rep = OptimizerTests.Check(inst, res.Solution, requireAllMandatory: false);
            Assert.True(rep.Feasible, rep.ToString());
            Assert.True(rep.MaxTimeDeviation <= 1e-6);
            // every unserved mandatory stop must come with a reason
            Assert.All(res.Solution.Unserved, u => Assert.False(string.IsNullOrWhiteSpace(u.Reason)));
        });
    }

    [Fact]
    public void Property_time_dependent_optimized_solutions_match_reference_times_within_one_second()
    {
        Property.ForAll(61_000, 12, (r, seed) =>
        {
            var features = SnapperTests.RandomGrid(r, 7, out var unusedCount);
            var net = NetworkBuilder.Build(features);
            var analysis = NetworkAnalysis.Analyze(net);
            var nodes = Enumerable.Range(0, net.NodeCount).Where(analysis.IsInLargestComponent).ToArray();
            var profile = SpeedModelTests.RandomProfile(r, out var raw);
            var stops = Enumerable.Range(0, r.Next(4, 12)).Select(i =>
            {
                var a = 36_000 + r.NextDouble() * 3_600;
                return new Stop($"s{i}", $"站{i}", default, "", "", 1, 0, 120, a, a + 2_400, r.NextDouble() < 0.6, r.Next(1, 6));
            }).ToArray();
            var V = r.Next(1, 4);
            var vehicles = Enumerable.Range(0, V).Select(v => new Vehicle($"V{v}", $"D{v}", default, default, 34_000, 50_000, 6, 3, 14_400)).ToArray();
            var inst = new ProblemInstance(stops, vehicles, BreakRule.Default);
            var depot = nodes[r.Next(nodes.Length)];
            var locNodes = stops.Select(_ => nodes[r.Next(nodes.Length)]).Concat(Enumerable.Repeat(depot, 2 * V)).ToArray();

            var l1 = PathSampledOracle.Build(net, profile, locNodes, 34_000, 50_000, new OracleBuildOptions { MaxDegreeOfParallelism = 1 });
            var l2 = new ExactOracle(net, profile, locNodes);
            var res = new Optimizer(inst, l1, l2).Run(null, LockSet.Empty, new OptimizerOptions { Seed = (ulong)seed, MaxIterations = 300, Runs = 2 }, null, default);

            var refGraph = TimeDependentDijkstraTests.ToRef(net);
            double RefArrival(int from, int to, double t) =>
                locNodes[from] == locNodes[to] ? t : RefDijkstra.TimeDependent(refGraph, raw, locNodes[from], t).Arrival[locNodes[to]];
            var rep = RefFeasibilityChecker.Check(inst, res.Solution, RefArrival, timeTolerance: 1.0, requireAllMandatory: false);
            Assert.True(rep.Feasible, rep.ToString());
            Assert.True(rep.MaxTimeDeviation <= 1.0, $"max deviation {rep.MaxTimeDeviation}");
        });
    }
}
