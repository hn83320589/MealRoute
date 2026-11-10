using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Rescheduling;
using MealRoute.Tests.Reference;
using MealRoute.Tests.Travel;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Rescheduling;

public class ReschedulerTests
{
    private static readonly OptimizerOptions Opt = new() { MaxIterations = 300, Runs = 2, Seed = 3 };

    private static Rescheduler Make(ProblemInstance inst) =>
        new(inst, (points, _) =>
        {
            var o = new Core.Travel.EuclideanOracle(points.Select(p => (p.Lon, p.Lat)).ToArray());
            return (o, o);
        });

    /// <summary>Two vehicles with 4 stops each on opposite sides of the depot (x axis, 1 m/s).</summary>
    private static (ProblemInstance Inst, Solution Plan) Day()
    {
        var stops = new List<Stop>();
        for (var i = 0; i < 4; i++) stops.Add(Stop($"E{i}", 1000 * (i + 1), 0, 20_000, 60));
        for (var i = 0; i < 4; i++) stops.Add(Stop($"W{i}", -1000 * (i + 1), 0, 20_000, 60));
        stops.Add(Stop("X", 500, 0, 20_000, 60, mandatory: false, priority: 3)); // unassigned at start
        var inst = Instance(stops, new[] { Vehicle("A", meals: 6), Vehicle("B", meals: 6), Vehicle("C", start: 5_000, meals: 10) });
        var ev = new RouteEvaluator(inst, Oracle(inst));
        var plan = new Solution(new[]
        {
            ev.Evaluate(0, new[] { 0, 1, 2, 3 }).ToPlan(),
            ev.Evaluate(1, new[] { 4, 5, 6, 7 }).ToPlan(),
            RoutePlan.Empty(2),
        }, new[] { new UnservedStop(8, "") });
        return (inst, plan);
    }

    [Fact]
    public void Delay_report_keeps_completed_stops_and_restarts_from_the_reported_place_and_time()
    {
        var (inst, plan) = Day();
        // A completed E0 and E1, now at x=2000 at t=2500, delayed 20 minutes
        var report = new VehicleReport(0, 2_500, new GeoPoint(0, 2000), new[] { 0, 1 }, DelayMinutes: 20);
        var res = Make(inst).Run(new RescheduleRequest(plan, 2_500, new[] { report }, LockSet.Empty), Opt, null, default);

        var a = res.Solution.Routes[0];
        Assert.Equal(new[] { 0, 1 }, a.Stops.Take(2));
        Assert.Equal(plan.Routes[0].Visits[0], a.Visits[0]);
        Assert.Equal(plan.Routes[0].Visits[1], a.Visits[1]);
        Assert.Equal(new[] { 2, 3 }, a.Stops.Skip(2).OrderBy(s => s));
        // resumes at 2500 + 1200 from x=2000: E2 (x=3000) reached at 4700 at the earliest
        var e2 = a.Visits.Single(v => v.Stop == 2);
        Assert.True(e2.Arrival >= 2_500 + 1_200 + 1_000 - 1e-6, $"E2 arrival {e2.Arrival}");
        Assert.Equal(VehicleState.EnRouteReported, res.Statuses[0].State);
    }

    [Fact]
    public void Only_own_remaining_stops_are_served_by_a_departed_vehicle_and_its_load_is_what_is_left()
    {
        var (inst, plan) = Day();
        var report = new VehicleReport(0, 2_500, new GeoPoint(0, 2000), new[] { 0, 1 });
        var res = Make(inst).Run(new RescheduleRequest(plan, 2_500, new[] { report }, LockSet.Empty), Opt, null, default);
        // A (en route) cannot pick up X (not loaded) nor B's stops; B is en route too (departed at 0)
        Assert.DoesNotContain(8, res.Solution.Routes[0].Stops);
        Assert.All(res.Solution.Routes[0].Stops, s => Assert.Contains(s, new[] { 0, 1, 2, 3 }));
        Assert.All(res.Solution.Routes[1].Stops, s => Assert.Contains(s, new[] { 4, 5, 6, 7 }));
        // C has not departed and takes the unassigned optional stop X
        Assert.Contains(8, res.Solution.Routes[2].Stops);
    }

    [Fact]
    public void Departed_vehicle_without_report_keeps_the_stop_it_is_heading_to()
    {
        var (inst, plan) = Day();
        var b = plan.Routes[1];
        var now = (b.Visits[0].Departure + b.Visits[1].Arrival) / 2; // between W0 and W1
        var res = Make(inst).Run(new RescheduleRequest(plan, now, Array.Empty<VehicleReport>(), LockSet.Empty), Opt, null, default);

        Assert.Equal(VehicleState.EnRouteCommitted, res.Statuses[1].State);
        Assert.Equal(new[] { 4, 5 }, res.Statuses[1].FixedStops);
        Assert.Equal(b.Visits[0], res.Solution.Routes[1].Visits[0]);
        Assert.Equal(b.Visits[1], res.Solution.Routes[1].Visits[1]);
    }

    [Fact]
    public void Broken_down_vehicle_makes_its_remaining_stops_unservable_with_a_reason()
    {
        var (inst, plan) = Day();
        var report = new VehicleReport(1, 2_500, new GeoPoint(0, -2000), new[] { 4, 5 }, BrokenDown: true);
        var res = Make(inst).Run(new RescheduleRequest(plan, 2_500, new[] { report }, LockSet.Empty), Opt, null, default);

        Assert.Equal(new[] { 4, 5 }, res.Solution.Routes[1].Stops);
        foreach (var s in new[] { 6, 7 })
        {
            var u = Assert.Single(res.Solution.Unserved, x => x.Stop == s);
            Assert.Contains("故障", u.Reason, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(res.Solution.Routes[2].Stops, s => s is 6 or 7); // not transferred
        Assert.Equal(VehicleState.BrokenDown, res.Statuses[1].State);
    }

    [Fact]
    public void Breakdown_before_departure_releases_the_stops_to_other_vehicles()
    {
        var (inst, plan) = Day();
        var late = plan with
        {
            Routes = new[] { plan.Routes[0], RoutePlanAt(inst, 1, new[] { 4, 5, 6, 7 }, 4_000), plan.Routes[2] },
        };
        var report = new VehicleReport(1, 1_000, new GeoPoint(0, 0), Array.Empty<int>(), BrokenDown: true);
        var res = Make(inst).Run(new RescheduleRequest(late, 1_000, new[] { report }, LockSet.Empty), Opt, null, default);
        Assert.Empty(res.Solution.Routes[1].Stops);
        Assert.All(new[] { 4, 5, 6, 7 }, s => Assert.Contains(s, res.Solution.Routes[2].Stops));
    }

    [Fact]
    public void Accumulated_driving_carries_over_and_forces_an_earlier_break()
    {
        // A drives far: E stops at 4000.. so that remaining driving after the report needs a break
        var stops = new[] { Stop("a", 4_000, service: 60), Stop("b", 8_000, service: 60), Stop("c", 9_000, service: 60) };
        var inst = Instance(stops, new[] { Vehicle("A", meals: 10) });
        var ev = new RouteEvaluator(inst, Oracle(inst));
        var plan = new Solution(new[] { ev.Evaluate(0, new[] { 0, 1, 2 }).ToPlan() }, Array.Empty<UnservedStop>());
        // reported right after leaving 'a' (drove 4000 s); continuing to b, c and back = 4000+1000+9000
        var report = new VehicleReport(0, 4_060, new GeoPoint(0, 4_000), new[] { 0 });
        var res = Make(inst).Run(new RescheduleRequest(plan, 4_060, new[] { report }, LockSet.Empty), Opt, null, default);
        var route = res.Solution.Routes[0];
        Assert.Equal(new[] { 0, 1, 2 }, route.Stops);
        Assert.Contains(route.Visits.Skip(1), v => v.BreakAfter);
    }

    [Fact]
    public void Subproblem_result_is_feasible_per_reference_checker_including_en_route_state()
    {
        var (inst, plan) = Day();
        var report = new VehicleReport(0, 2_500, new GeoPoint(0, 2000), new[] { 0, 1 }, DelayMinutes: 10);
        var res = Make(inst).Run(new RescheduleRequest(plan, 2_500, new[] { report }, LockSet.Empty), Opt, null, default);
        // Check the re-planned part of A independently: start at (2000, 0) at 3100 with fixed departure
        var a = res.Solution.Routes[0];
        var tailStops = a.Stops.Skip(2).ToArray();
        var sub = Instance(inst.Stops, new[] { inst.Vehicles[0] with { StartDepot = new GeoPoint(0, 2000), EarliestStart = 3_100, FixedDeparture = true, MealCapacity = 2, WorkStartedAt = plan.Routes[0].Departure } });
        var tail = new RoutePlan(0, tailStops, 3_100, a.Visits.Skip(2).ToArray(), a.EndArrival);
        var unserved = Enumerable.Range(0, inst.Stops.Count).Except(tailStops).Select(s => new UnservedStop(s, "")).ToArray();
        var rep = RefFeasibilityChecker.Check(sub, new Solution(new[] { tail }, unserved), Oracle(sub).ArrivalTime, 1e-6, requireAllMandatory: false);
        Assert.True(rep.Feasible, rep.ToString());
    }

    [Fact]
    public void Cancelling_returns_a_complete_plan_and_progress_is_monotone()
    {
        var (inst, plan) = Day();
        using var cts = new CancellationTokenSource();
        var fractions = new List<double>();
        var progress = new SynchronousProgress<OptimizationProgress>(p => { fractions.Add(p.Fraction); if (fractions.Count == 2) cts.Cancel(); });
        var report = new VehicleReport(0, 2_500, new GeoPoint(0, 2000), new[] { 0, 1 }, DelayMinutes: 20);
        var res = Make(inst).Run(new RescheduleRequest(plan, 2_500, new[] { report }, LockSet.Empty),
            new OptimizerOptions { MaxIterations = 100_000, Runs = 2, Seed = 1 }, progress, cts.Token);
        Assert.True(res.Cancelled);
        var all = res.Solution.Routes.SelectMany(r => r.Stops).Concat(res.Solution.Unserved.Select(u => u.Stop)).OrderBy(s => s);
        Assert.Equal(Enumerable.Range(0, inst.Stops.Count), all);
        for (var i = 1; i < fractions.Count; i++) Assert.True(fractions[i] >= fractions[i - 1]);
    }

    private static RoutePlan RoutePlanAt(ProblemInstance inst, int v, int[] stops, double start)
    {
        var shifted = Instance(inst.Stops, inst.Vehicles.Select((x, i) => i == v ? x with { EarliestStart = start } : x).ToArray());
        return new RouteEvaluator(shifted, Oracle(shifted)).Evaluate(v, stops).ToPlan();
    }
}
