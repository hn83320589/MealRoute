using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Evaluation;

public class RouteEvaluatorTests
{
    private static RouteEvaluation Eval(ProblemInstance inst, params int[] stops) =>
        new RouteEvaluator(inst, Oracle(inst)).Evaluate(0, stops);

    [Fact]
    public void Empty_route_is_unused_and_costs_nothing()
    {
        var inst = Instance(new[] { Stop("A", 100) }, new[] { Vehicle() });
        var r = Eval(inst);
        Assert.True(r.IsEmpty);
        Assert.True(r.IsFeasible);
        Assert.Equal(0, r.TravelSeconds);
        Assert.Equal(0, r.WorkSeconds);
    }

    [Fact]
    public void Schedule_waits_for_window_then_serves_then_departs()
    {
        // depot at 0, A at 100 (window 500..900, service 60), B at 300 (window 0..2000, service 30)
        var inst = Instance(new[] { Stop("A", 100, 500, 900, 60), Stop("B", 300, 0, 2000, 30) }, new[] { Vehicle(start: 0) });
        var r = Eval(inst, 0, 1);
        Assert.True(r.IsFeasible);
        // departure delayed to 400 so that arrival at A is exactly 500
        Assert.Equal(400, r.Departure, 6);
        Assert.Equal(500, r.Visits[0].Arrival, 6);
        Assert.Equal(500, r.Visits[0].ServiceStart, 6);
        Assert.Equal(560, r.Visits[0].Departure, 6);
        Assert.Equal(760, r.Visits[1].Arrival, 6);
        Assert.Equal(790, r.Visits[1].Departure, 6);
        Assert.Equal(1090, r.EndArrival, 6);
        Assert.Equal(100 + 200 + 300, r.TravelSeconds, 6);
        Assert.Equal(1090 - 400, r.WorkSeconds, 6);
        Assert.Equal(600, r.DistanceM, 6);
    }

    [Fact]
    public void Departure_is_not_delayed_when_no_initial_wait_and_never_before_earliest_start()
    {
        var inst = Instance(new[] { Stop("A", 100, 0, 900) }, new[] { Vehicle(start: 300) });
        var r = Eval(inst, 0);
        Assert.Equal(300, r.Departure);
        Assert.Equal(400, r.Visits[0].Arrival);
    }

    [Fact]
    public void Late_arrival_reports_the_stop_name_arrival_and_window()
    {
        var stop = new Stop("s1", "王奶奶", new(0, 4200), "", "", 1, 0, 0, 11.5 * 3600, 12 * 3600, true, 1);
        var inst = Instance(new[] { stop }, new[] { Vehicle(start: 11 * 3600) }); // 4200 s drive: arrives 12:10
        var r = Eval(inst, 0);
        Assert.False(r.IsFeasible);
        var v = r.FirstViolation!;
        Assert.Equal(ViolationKind.TimeWindow, v.Kind);
        Assert.Equal(0, v.Stop);
        Assert.Equal(12 * 3600 + 600, v.Time, 6);
        Assert.Equal("王奶奶 12:10 到，時間窗 11:30–12:00", v.Message);
    }

    [Fact]
    public void Capacity_is_checked_per_dimension()
    {
        var inst = Instance(new[] { Stop("A", 10, meals: 3, boxes: 1), Stop("B", 20, meals: 3, boxes: 2) }, new[] { Vehicle(meals: 10, boxes: 2) });
        var r = Eval(inst, 0, 1);
        Assert.False(r.IsFeasible);
        Assert.Equal(ViolationKind.BoxCapacity, r.FirstViolation!.Kind);
        Assert.Equal(6, r.Meals);
        Assert.Equal(3, r.Boxes);
        Assert.Contains("保溫箱", r.FirstViolation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_work_time_is_enforced()
    {
        var inst = Instance(new[] { Stop("A", 1000) }, new[] { Vehicle(maxWork: 1999) });
        var r = Eval(inst, 0);
        Assert.False(r.IsFeasible);
        Assert.Equal(ViolationKind.MaxWork, r.FirstViolation!.Kind);
    }

    [Fact]
    public void Latest_return_time_is_enforced()
    {
        var inst = Instance(new[] { Stop("A", 1000) }, new[] { Vehicle(start: 0, end: 1500) });
        var r = Eval(inst, 0);
        Assert.False(r.IsFeasible);
        Assert.Equal(ViolationKind.LatestReturn, r.FirstViolation!.Kind);
    }

    [Fact]
    public void Earliest_violation_in_time_is_reported_first()
    {
        // B is late at t=300, the route also returns too late; time window comes first
        var inst = Instance(new[] { Stop("A", 100), Stop("B", 300, 0, 250) }, new[] { Vehicle(end: 400) });
        var r = Eval(inst, 0, 1);
        Assert.Equal(ViolationKind.TimeWindow, r.FirstViolation!.Kind);
        Assert.Equal(1, r.FirstViolation.Stop);
    }

    [Fact]
    public void Unreachable_stop_is_reported()
    {
        var inst = Instance(new[] { Stop("A", 100) }, new[] { Vehicle() });
        var r = new RouteEvaluator(inst, new InfiniteOracle(inst.LocationCount)).Evaluate(0, new[] { 0 });
        Assert.False(r.IsFeasible);
        Assert.Equal(ViolationKind.Unreachable, r.FirstViolation!.Kind);
    }

    // ---- Breaks (acceptance #12) ----

    [Fact]
    public void Route_within_the_driving_limit_gets_no_break()
    {
        // total driving exactly 4.5 h: allowed
        var inst = Instance(new[] { Stop("A", 4050), Stop("B", 8100) }, new[] { Vehicle() });
        var r = Eval(inst, 0, 1);
        Assert.True(r.IsFeasible);
        Assert.Equal(16_200, r.TravelSeconds, 6);
        Assert.DoesNotContain(r.Visits, v => v.BreakAfter);
    }

    [Fact]
    public void Long_route_takes_a_break_at_a_stop_and_it_delays_later_arrivals()
    {
        // depot 0 -> A 6000 -> B 12000 -> back: legs 6000, 6000, 12000 (total 24000 s driving)
        var inst = Instance(new[] { Stop("A", 6000), Stop("B", 12000) }, new[] { Vehicle() });
        var r = Eval(inst, 0, 1);
        Assert.True(r.IsFeasible);
        // after B the return leg (12000) would push driving past 16200, so break at B (latest)…
        // …but B->depot alone is 12000 ≤ 16200 after reset. A break at A would leave 6000+12000 > 16200. So B.
        Assert.False(r.Visits[0].BreakAfter);
        Assert.True(r.Visits[1].BreakAfter);
        Assert.Equal(12_000 + 1800, r.Visits[1].Departure, 6);
        Assert.Equal(24_000 + 1800, r.EndArrival, 6);
        Assert.Equal(24_000, r.TravelSeconds, 6);
        Assert.Equal(24_000 + 1800, r.WorkSeconds, 6);
    }

    [Fact]
    public void Break_is_placed_before_a_waiting_period_when_that_returns_earlier()
    {
        // depot 0 -> A 6000 -> B 12000 -> C 9000 -> depot. Legs 6000, 6000, 3000, 9000 (24000 s driving).
        // Without a break, driving exceeds 4.5 h on the return leg, so a break at B or C is needed
        // (a break at A would leave 18000 s afterwards). C's window opens at 17000; arriving at 15000
        // means 2000 s of waiting, which absorbs a break taken at B. Break at B returns at 26000,
        // break at C (the latest position) at 27800.
        var inst = Instance(new[]
        {
            Stop("A", 6000),
            Stop("B", 12000),
            Stop("C", 9000, 17000, 17500),
        }, new[] { Vehicle(end: 27000) });
        var r = Eval(inst, 0, 1, 2);
        Assert.True(r.IsFeasible);
        Assert.Equal(new[] { false, true, false }, r.Visits.Select(v => v.BreakAfter));
        Assert.Equal(16800, r.Visits[2].Arrival, 6);
        Assert.Equal(17000, r.Visits[2].ServiceStart, 6);
        Assert.Equal(26000, r.EndArrival, 6);
        Assert.Equal(r.EndArrival, BruteForceEarliestReturn(inst, new[] { 0, 1, 2 }), 6);
    }

    [Fact]
    public void Latest_break_position_is_used_when_nothing_absorbs_the_delay()
    {
        // C closes at 16000: a break at B would make C late, so the break must be at C.
        var inst = Instance(new[]
        {
            Stop("A", 6000),
            Stop("B", 12000),
            Stop("C", 9000, 0, 16000),
        }, new[] { Vehicle(end: 40000) });
        var r = Eval(inst, 0, 1, 2);
        Assert.True(r.IsFeasible);
        Assert.Equal(new[] { false, false, true }, r.Visits.Select(v => v.BreakAfter));
        Assert.Equal(25800, r.EndArrival, 6);
    }

    [Fact]
    public void Diagnostic_schedule_of_an_infeasible_route_includes_the_break()
    {
        var inst = Instance(new[] { Stop("A", 6000), Stop("B", 12000) }, new[] { Vehicle(end: 20000) });
        var r = Eval(inst, 0, 1);
        Assert.False(r.IsFeasible);
        Assert.True(r.Visits[1].BreakAfter);
        Assert.Equal(ViolationKind.LatestReturn, r.FirstViolation!.Kind);
    }

    [Fact]
    public void Single_leg_longer_than_the_driving_limit_is_infeasible()
    {
        var inst = Instance(new[] { Stop("A", 17000) }, new[] { Vehicle() });
        var r = Eval(inst, 0);
        Assert.False(r.IsFeasible);
        Assert.Equal(ViolationKind.DrivingLimit, r.FirstViolation!.Kind);
    }

    [Fact]
    public void Breaks_can_be_disabled_for_static_benchmark_mode()
    {
        var inst = Instance(new[] { Stop("A", 17000) }, new[] { Vehicle() }, BreakRule.None);
        Assert.True(Eval(inst, 0).IsFeasible);
    }

    /// <summary>Independent brute force over all break subsets (departure 0) for the earliest feasible return.</summary>
    private static double BruteForceEarliestReturn(ProblemInstance inst, int[] route)
    {
        var best = double.PositiveInfinity;
        for (var mask = 0; mask < 1 << route.Length; mask++)
        {
            double t = 0, drive = 0, x = 0;
            var ok = true;
            for (var i = 0; i <= route.Length && ok; i++)
            {
                var nx = i < route.Length ? inst.Stops[route[i]].Location.Lon : 0;
                var leg = Math.Abs(nx - x);
                drive += leg;
                if (drive > 16200) { ok = false; break; }
                t += leg;
                x = nx;
                if (i == route.Length) break;
                var s = inst.Stops[route[i]];
                t = Math.Max(t, s.WindowStart);
                if (t > s.WindowEnd) { ok = false; break; }
                t += s.ServiceSeconds;
                if ((mask >> i & 1) == 1) { t += 1800; drive = 0; }
            }
            if (ok) best = Math.Min(best, t);
        }
        return best;
    }
}

internal sealed class InfiniteOracle(int count) : MealRoute.Core.Travel.ITravelTimeOracle
{
    public int LocationCount => count;
    public double ArrivalTime(int from, int to, double departure) => from == to ? departure : double.PositiveInfinity;
    public MealRoute.Core.Travel.LegResult Leg(int from, int to, double departure) => new(ArrivalTime(from, to, departure), double.PositiveInfinity);
}
