using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Network;
using MealRoute.Core.Travel;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Network;
using MealRoute.Tests.Reference;
using MealRoute.Tests.Travel;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Evaluation;

public class RouteEvaluatorPropertyTests
{
    /// <summary>Random static instance on a line/plane, with windows, service, capacities and long legs so breaks matter.</summary>
    internal static ProblemInstance RandomStaticInstance(Random r, int stops, int vehicles, double scale = 3000)
    {
        var list = new List<Stop>();
        for (var i = 0; i < stops; i++)
        {
            var x = (r.NextDouble() - 0.5) * scale * 2;
            var y = (r.NextDouble() - 0.5) * scale * 2;
            var a = r.NextDouble() * 20_000;
            var width = r.NextDouble() < 0.2 ? 86_400 : 1_000 + r.NextDouble() * 12_000;
            list.Add(new Stop($"s{i}", $"站{i}", new GeoPoint(y, x), "", "", r.Next(1, 4), r.Next(0, 2),
                r.Next(0, 4) * 120, a, a + width, r.NextDouble() < 0.6, r.Next(1, 6)));
        }
        var vs = Enumerable.Range(0, vehicles).Select(v => new Vehicle($"V{v}", $"駕駛{v}", new GeoPoint(0, 0), new GeoPoint(0, 0),
            r.NextDouble() * 3_000, 40_000 + r.NextDouble() * 20_000, r.Next(4, 15), r.Next(1, 5), 20_000 + r.NextDouble() * 30_000)).ToArray();
        return new ProblemInstance(list, vs, BreakRule.Default);
    }

    private static Solution SingleRouteSolution(ProblemInstance inst, RouteEvaluation eval)
    {
        var routes = Enumerable.Range(0, inst.Vehicles.Count).Select(v => v == eval.Vehicle ? eval.ToPlan() : RoutePlan.Empty(v)).ToArray();
        var unserved = Enumerable.Range(0, inst.Stops.Count).Except(eval.Stops).Select(s => new UnservedStop(s, "")).ToArray();
        return new Solution(routes, unserved);
    }

    [Fact]
    public void Property_static_feasible_evaluations_are_confirmed_by_the_reference_checker()
    {
        var feasibleSeen = 0;
        Property.ForAll(40_000, 400, (r, _) =>
        {
            var inst = RandomStaticInstance(r, r.Next(1, 8), 1, scale: r.NextDouble() < 0.5 ? 3_000 : 9_000);
            var oracle = Oracle(inst);
            var route = Enumerable.Range(0, inst.Stops.Count).OrderBy(_ => r.Next()).Take(r.Next(1, inst.Stops.Count + 1)).ToArray();
            var eval = new RouteEvaluator(inst, oracle).Evaluate(0, route);
            if (!eval.IsFeasible) return;
            feasibleSeen++;

            var rep = RefFeasibilityChecker.Check(inst, SingleRouteSolution(inst, eval), oracle.ArrivalTime, 1e-6, requireAllMandatory: false);
            Assert.True(rep.Feasible, rep.ToString());
            Assert.Equal(eval.TravelSeconds, rep.TotalTravel, 6);
        });
        Assert.True(feasibleSeen > 50, $"only {feasibleSeen} feasible cases generated");
    }

    [Fact]
    public void Property_static_infeasible_evaluations_have_no_feasible_break_placement()
    {
        var infeasibleSeen = 0;
        Property.ForAll(41_000, 400, (r, _) =>
        {
            var inst = RandomStaticInstance(r, r.Next(1, 7), 1, scale: 8_000);
            var oracle = Oracle(inst);
            var route = Enumerable.Range(0, inst.Stops.Count).OrderBy(_ => r.Next()).ToArray();
            var eval = new RouteEvaluator(inst, oracle).Evaluate(0, route);
            if (eval.IsFeasible) return;
            infeasibleSeen++;
            Assert.NotNull(eval.FirstViolation);
            Assert.False(BruteForceFeasible(inst, oracle, route), "brute force found a feasible schedule the evaluator missed");
        });
        Assert.True(infeasibleSeen > 50, $"only {infeasibleSeen} infeasible cases generated");
    }

    [Fact]
    public void Property_static_feasible_evaluations_are_optimal_in_travel_then_return_over_break_placements()
    {
        Property.ForAll(42_000, 400, (r, _) =>
        {
            var inst = RandomStaticInstance(r, r.Next(2, 7), 1, scale: 8_000);
            var oracle = Oracle(inst);
            var route = Enumerable.Range(0, inst.Stops.Count).OrderBy(_ => r.Next()).ToArray();
            var eval = new RouteEvaluator(inst, oracle).Evaluate(0, route);
            if (!eval.IsFeasible) return;
            var best = BruteForceBestEnd(inst, oracle, route, eval.Departure);
            Assert.Equal(best, eval.EndArrival, 6);
        });
    }

    [Fact]
    public void Property_time_dependent_feasible_evaluations_match_reference_td_integration()
    {
        var feasibleSeen = 0;
        Property.ForAll(43_000, 60, (r, _) =>
        {
            var features = SnapperTests.RandomGrid(r, 6, out var unusedCount);
            var net = NetworkBuilder.Build(features);
            var analysis = NetworkAnalysis.Analyze(net);
            var nodes = Enumerable.Range(0, net.NodeCount).Where(analysis.IsInLargestComponent).ToArray();
            var profile = SpeedModelTests.RandomProfile(r, out var raw);

            var stops = Enumerable.Range(0, r.Next(1, 6)).Select(i =>
            {
                var a = 36_000 + r.NextDouble() * 1_800;
                return new Stop($"s{i}", $"站{i}", default, "", "", 1, 0, 60, a, a + 3_600, true, 1);
            }).ToArray();
            var depot = nodes[r.Next(nodes.Length)];
            var inst = new ProblemInstance(stops, new[] { new Vehicle("V", "D", default, default, 34_000, 60_000, 50, 50, 30_000) }, BreakRule.Default);
            var locNodes = stops.Select(_ => nodes[r.Next(nodes.Length)]).Concat(new[] { depot, depot }).ToArray();

            var exact = new ExactOracle(net, profile, locNodes);
            var eval = new RouteEvaluator(inst, exact).Evaluate(0, Enumerable.Range(0, stops.Length).ToArray());
            if (!eval.IsFeasible) return;
            feasibleSeen++;

            var refGraph = TimeDependentDijkstraTests.ToRef(net);
            double RefArrival(int from, int to, double t) =>
                locNodes[from] == locNodes[to] ? t : RefDijkstra.TimeDependent(refGraph, raw, locNodes[from], t).Arrival[locNodes[to]];
            var rep = RefFeasibilityChecker.Check(inst, SingleRouteSolution(inst, eval), RefArrival, 1e-6);
            Assert.True(rep.Feasible, rep.ToString());
        });
        Assert.True(feasibleSeen > 10, $"only {feasibleSeen} feasible TD cases");
    }

    // ---- independent brute force over departures {earliest, delayed} and all break subsets ----

    private static IEnumerable<double> CandidateDepartures(ProblemInstance inst, EuclideanOracle oracle, int[] route)
    {
        var veh = inst.Vehicles[0];
        yield return veh.EarliestStart;
        var first = inst.Stops[route[0]];
        var travel = oracle.Distance(inst.VehicleStartLocation(0), route[0]);
        if (first.WindowStart - travel > veh.EarliestStart) yield return first.WindowStart - travel;
    }

    private static double? Schedule(ProblemInstance inst, EuclideanOracle oracle, int[] route, double d, int mask)
    {
        var veh = inst.Vehicles[0];
        int meals = route.Sum(s => inst.Stops[s].Meals), boxes = route.Sum(s => inst.Stops[s].Boxes);
        if (meals > veh.MealCapacity || boxes > veh.BoxCapacity) return null;
        double t = d, drive = 0;
        var loc = inst.VehicleStartLocation(0);
        for (var i = 0; i <= route.Length; i++)
        {
            var next = i == route.Length ? inst.VehicleEndLocation(0) : route[i];
            var leg = oracle.Distance(loc, next);
            drive += leg;
            if (drive > inst.Breaks.MaxDrivingSeconds) return null;
            t += leg;
            loc = next;
            if (i == route.Length) break;
            var s = inst.Stops[route[i]];
            t = Math.Max(t, s.WindowStart);
            if (t > s.WindowEnd) return null;
            t += s.ServiceSeconds;
            if ((mask >> i & 1) == 1) { t += inst.Breaks.BreakSeconds; drive = 0; }
        }
        if (t > veh.LatestEnd || t - d > veh.MaxWorkSeconds) return null;
        return t;
    }

    private static bool BruteForceFeasible(ProblemInstance inst, EuclideanOracle oracle, int[] route)
    {
        foreach (var d in CandidateDepartures(inst, oracle, route))
            for (var mask = 0; mask < 1 << route.Length; mask++)
                if (Schedule(inst, oracle, route, d, mask) is not null) return true;
        return false;
    }

    private static double BruteForceBestEnd(ProblemInstance inst, EuclideanOracle oracle, int[] route, double d)
    {
        var best = double.PositiveInfinity;
        for (var mask = 0; mask < 1 << route.Length; mask++)
            if (Schedule(inst, oracle, route, d, mask) is { } end) best = Math.Min(best, end);
        return best;
    }
}

public class RouteCostPropertyTests
{
    [Fact]
    public void Property_fast_cost_path_agrees_with_full_evaluation()
    {
        Property.ForAll(44_000, 600, (r, _) =>
        {
            var inst = RouteEvaluatorPropertyTests.RandomStaticInstance(r, r.Next(1, 9), 1, scale: r.NextDouble() < 0.5 ? 3_000 : 8_000);
            var evaluator = new RouteEvaluator(inst, TestInstances.Oracle(inst));
            var route = Enumerable.Range(0, inst.Stops.Count).OrderBy(_ => r.Next()).Take(r.Next(1, inst.Stops.Count + 1)).ToArray();
            var full = evaluator.Evaluate(0, route);
            var deps = new double[route.Length];
            var fast = evaluator.EvaluateCost(0, route, deps);

            Assert.Equal(full.IsFeasible, fast.Feasible);
            if (!full.IsFeasible) return;
            Assert.Equal(full.Departure, fast.Departure);
            Assert.Equal(full.EndArrival, fast.End);
            Assert.Equal(full.TravelSeconds, fast.Travel, 9);
            Assert.Equal(full.DistanceM, fast.Distance, 9);
            Assert.Equal(full.Visits.Select(v => v.Departure), deps);
        });
    }
}
