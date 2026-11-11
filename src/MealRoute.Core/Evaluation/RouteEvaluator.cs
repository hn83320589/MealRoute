using MealRoute.Core.Model;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Evaluation;

public sealed class RouteEvaluation
{
    public required int Vehicle { get; init; }
    public required IReadOnlyList<int> Stops { get; init; }
    public bool IsEmpty => Stops.Count == 0;
    public required bool IsFeasible { get; init; }
    /// <summary>Earliest violation in time (null when feasible).</summary>
    public Violation? FirstViolation { get; init; }
    public double Departure { get; init; }
    public double EndArrival { get; init; }
    public double TravelSeconds { get; init; }
    public double WorkSeconds { get; init; }
    public double DistanceM { get; init; }
    public int Meals { get; init; }
    public int Boxes { get; init; }
    public required IReadOnlyList<StopVisit> Visits { get; init; }

    public static RouteEvaluation Empty(int vehicle) => new()
    {
        Vehicle = vehicle, Stops = Array.Empty<int>(), IsFeasible = true, Visits = Array.Empty<StopVisit>(),
    };

    /// <summary>The plain-data plan of this route (what is stored, exported and checked).</summary>
    public RoutePlan ToPlan() => new(Vehicle, Stops.ToArray(), Departure, Visits.ToArray(), EndArrival);
}

/// <summary>
/// Evaluates one vehicle's stop sequence:
/// <list type="number">
/// <item>Departure is a decision: starting from the earliest start, the vehicle leaves later by as
/// much waiting as the route can absorb (forward time slack: the total waiting, capped where an
/// earlier window would close), so waiting before the first stop and in the middle of the route
/// both stop counting as work. Arrival times only move later with a later departure (FIFO), so the
/// earliest start decides time-window feasibility and the latest return; the delay only shortens
/// work time, which is why it matters for the work-time limit. With time-dependent travel the
/// delay is refined by a few fixed-point steps, never accepting one that breaks the schedule, and a
/// delay that adds travel (heavier traffic) is only taken when the work-time limit needs it.</item>
/// <item>Breaks: considered only where accumulated driving would otherwise exceed the limit; every
/// allowed stop up to that point is tried (recursively for further breaks) and the feasible
/// schedule with least travel time, then least work time, then earliest return, is kept.</item>
/// <item>Infeasible routes get a diagnostic schedule (latest-possible breaks, continuing past
/// violations) and the earliest violation in time.</item>
/// </list>
/// Not thread-safe only in the sense of the oracle; the evaluator itself keeps no state.
/// </summary>
public sealed class RouteEvaluator
{
    /// <summary>Remaining absorbable waiting (seconds) below which the departure is not moved further.</summary>
    private const double SettleTolerance = 0.5;

    private readonly ProblemInstance _inst;
    private readonly ITravelTimeOracle _oracle;

    public RouteEvaluator(ProblemInstance instance, ITravelTimeOracle oracle)
    {
        _inst = instance;
        _oracle = oracle;
    }

    public ProblemInstance Instance => _inst;
    public ITravelTimeOracle Oracle => _oracle;

    public RouteEvaluation Evaluate(int vehicle, IReadOnlyList<int> stops)
    {
        if (stops.Count == 0) return RouteEvaluation.Empty(vehicle);
        var veh = _inst.Vehicles[vehicle];
        var route = stops.ToArray();

        int meals = 0, boxes = 0;
        foreach (var s in route) { meals += _inst.Stops[s].Meals; boxes += _inst.Stops[s].Boxes; }

        var d0 = veh.EarliestStart;
        var dStar = DelayedDeparture(vehicle, route[0], d0);
        Violation? capacity = null;
        if (meals > veh.MealCapacity)
            capacity = new Violation(ViolationKind.MealCapacity, -1, dStar, $"餐盒 {meals} 份超過容量 {veh.MealCapacity} 份");
        else if (boxes > veh.BoxCapacity)
            capacity = new Violation(ViolationKind.BoxCapacity, -1, dStar, $"保溫箱 {boxes} 個超過容量 {veh.BoxCapacity} 個");

        var diagDeparture = dStar;
        if (capacity is null)
        {
            if (Plan(vehicle, route, out var cost, out var breaks))
            {
                var sim = Simulate(vehicle, route, cost.Departure, breaks, diagnostic: false);
                return Build(vehicle, route, sim, meals, boxes, null);
            }
            // Only the work-time limit failed: diagnose at the departure with the least work.
            if (!double.IsNaN(cost.Departure)) diagDeparture = cost.Departure;
        }

        var diag = Simulate(vehicle, route, diagDeparture, null, diagnostic: true);
        var first = capacity ?? diag.FirstViolation
            ?? throw new InvalidOperationException("Route reported infeasible but no violation was found.");
        return Build(vehicle, route, diag, meals, boxes, first);
    }

    /// <summary>
    /// Same verdict and costs as <see cref="Evaluate"/> without building visits or diagnostics.
    /// Allocation-free unless the driving limit forces a break search. Optionally writes into
    /// <paramref name="earliestDepartures"/> (length ≥ route length) each stop's departure time when
    /// leaving at the earliest start with the chosen breaks: the earliest the vehicle can be there,
    /// a lower bound that insertion pre-checks rely on (the planned, delayed schedule is later).
    /// </summary>
    public RouteCost EvaluateCost(int vehicle, ReadOnlySpan<int> route, Span<double> earliestDepartures = default)
    {
        if (route.IsEmpty) return RouteCost.Empty;
        var veh = _inst.Vehicles[vehicle];
        int meals = 0, boxes = 0;
        foreach (var s in route) { meals += _inst.Stops[s].Meals; boxes += _inst.Stops[s].Boxes; }
        if (meals > veh.MealCapacity || boxes > veh.BoxCapacity) return RouteCost.Infeasible;

        if (!Plan(vehicle, route, out var cost, out var breaks)) return RouteCost.Infeasible;
        if (!earliestDepartures.IsEmpty) Forward(vehicle, route, veh.EarliestStart, breaks, earliestDepartures, out _, out _);
        return cost;
    }

    private enum ForwardStatus { Feasible, Infeasible, DrivingLimit }

    /// <summary>
    /// Chooses breaks and departure. Returns false when infeasible; when only the work-time limit
    /// failed, <paramref name="cost"/> still carries the least-work departure (otherwise it is NaN).
    /// </summary>
    private bool Plan(int vehicle, ReadOnlySpan<int> route, out RouteCost cost, out bool[]? breaks)
    {
        var veh = _inst.Vehicles[vehicle];
        breaks = null;
        var status = Settle(vehicle, route, null, out cost);
        if (status == ForwardStatus.DrivingLimit)
        {
            breaks = SearchBreaks(vehicle, route.ToArray(), veh.EarliestStart);
            if (breaks is null) { cost = RouteCost.Infeasible; return false; }
            status = Settle(vehicle, route, breaks, out cost);
        }
        if (status != ForwardStatus.Feasible) { cost = RouteCost.Infeasible; return false; }
        if (cost.End - (veh.WorkStartedAt ?? cost.Departure) > veh.MaxWorkSeconds)
        {
            cost = cost with { Feasible = false };
            return false;
        }
        return true;
    }

    /// <summary>
    /// Simulates from the earliest start, then leaves later by the waiting the schedule can absorb
    /// (see the class remarks). Every schedule tried (the earliest start, each delay step, and the
    /// departure with no wait at the first stop) is a candidate; the chosen one meets the work-time
    /// limit if any does, then has the least travel, then the least work, the objective's order.
    /// With static travel all candidates travel equally, so the most delayed one wins; with
    /// time-dependent travel a delay into heavier traffic is not taken just to shorten work.
    /// Feasibility here excludes the work-time limit.
    /// </summary>
    private ForwardStatus Settle(int vehicle, ReadOnlySpan<int> route, bool[]? breaks, out RouteCost cost)
    {
        var veh = _inst.Vehicles[vehicle];
        var status = Forward(vehicle, route, veh.EarliestStart, breaks, default, out var walk, out var step);
        cost = walk;
        if (status != ForwardStatus.Feasible || veh.FixedDeparture) return status;

        for (var i = 0; i < 12 && step > SettleTolerance; i++)
        {
            if (Forward(vehicle, route, walk.Departure + step, breaks, default, out var later, out var more) == ForwardStatus.Feasible
                && later.Work < walk.Work)
            {
                walk = later;
                step = more;
                if (Preferred(veh, later, cost)) cost = later;
            }
            else
            {
                step /= 2; // time-dependent travel moved a window or the return; try a smaller delay
            }
        }

        var noFirstWait = DelayedDeparture(vehicle, route[0], veh.EarliestStart);
        if (noFirstWait != veh.EarliestStart && noFirstWait != walk.Departure
            && Forward(vehicle, route, noFirstWait, breaks, default, out var alt, out _) == ForwardStatus.Feasible
            && Preferred(veh, alt, cost))
        {
            cost = alt;
        }
        return status;
    }

    /// <summary>Within the work-time limit first, then less travel (microsecond grid, as the objective), then less work.</summary>
    private static bool Preferred(Vehicle veh, RouteCost a, RouteCost b)
    {
        bool okA = a.End - (veh.WorkStartedAt ?? a.Departure) <= veh.MaxWorkSeconds;
        bool okB = b.End - (veh.WorkStartedAt ?? b.Departure) <= veh.MaxWorkSeconds;
        if (okA != okB) return okA;
        var ta = Math.Round(a.Travel * 1e6);
        var tb = Math.Round(b.Travel * 1e6);
        if (ta != tb) return ta < tb;
        return a.Work < b.Work;
    }

    /// <summary>
    /// One pass at departure <paramref name="d"/> with breaks exactly where <paramref name="breaks"/>
    /// says (none when null; exceeding the driving limit then reports <see cref="ForwardStatus.DrivingLimit"/>).
    /// Checks windows, driving and the latest return, not the work-time limit. <paramref name="delay"/>
    /// is the forward time slack at the depot: the total waiting, capped at each stop by its window
    /// end's remaining room plus the waiting before it.
    /// </summary>
    private ForwardStatus Forward(int vehicle, ReadOnlySpan<int> route, double d, bool[]? breaks, Span<double> departures,
        out RouteCost cost, out double delay)
    {
        cost = RouteCost.Infeasible;
        delay = 0;
        var veh = _inst.Vehicles[vehicle];
        var rule = _inst.Breaks;
        var n = route.Length;
        double cur = d, drive = veh.InitialDrivingSeconds, travel = 0, distance = 0;
        double waited = 0, slack = double.PositiveInfinity;
        var from = _inst.VehicleStartLocation(vehicle);
        for (var j = 0; j <= n; j++)
        {
            var to = j == n ? _inst.VehicleEndLocation(vehicle) : _inst.StopLocation(route[j]);
            var leg = _oracle.Leg(from, to, cur);
            if (double.IsInfinity(leg.Arrival)) return ForwardStatus.Infeasible;
            drive += leg.Arrival - cur;
            travel += leg.Arrival - cur;
            if (drive > rule.MaxDrivingSeconds) return breaks is null ? ForwardStatus.DrivingLimit : ForwardStatus.Infeasible;
            distance += leg.DistanceM;
            cur = leg.Arrival;
            if (j == n) break;
            var stop = _inst.Stops[route[j]];
            if (cur < stop.WindowStart)
            {
                waited += stop.WindowStart - cur;
                cur = stop.WindowStart;
            }
            if (cur > stop.WindowEnd) return ForwardStatus.Infeasible;
            slack = Math.Min(slack, waited + stop.WindowEnd - cur);
            cur += stop.ServiceSeconds;
            if (breaks is not null && breaks[j])
            {
                cur += rule.BreakSeconds;
                drive = 0;
            }
            if (!departures.IsEmpty) departures[j] = cur;
            from = to;
        }
        if (cur > veh.LatestEnd) return ForwardStatus.Infeasible;
        cost = new RouteCost(true, d, cur, travel, distance);
        delay = Math.Min(waited, slack);
        return ForwardStatus.Feasible;
    }

    private RouteEvaluation Build(int vehicle, int[] route, SimResult sim, int meals, int boxes, Violation? violation) => new()
    {
        Vehicle = vehicle,
        Stops = route,
        IsFeasible = violation is null,
        FirstViolation = violation,
        Departure = sim.Departure,
        EndArrival = sim.End,
        TravelSeconds = sim.Travel,
        WorkSeconds = sim.End - sim.Departure,
        DistanceM = sim.Distance,
        Meals = meals,
        Boxes = boxes,
        Visits = sim.Visits,
    };

    /// <summary>Latest departure (≥ earliest start) that still reaches the first stop by its window start.</summary>
    private double DelayedDeparture(int vehicle, int firstStop, double d0)
    {
        var from = _inst.VehicleStartLocation(vehicle);
        var to = _inst.StopLocation(firstStop);
        var a = _inst.Stops[firstStop].WindowStart;
        var arr0 = _oracle.ArrivalTime(from, to, d0);
        if (double.IsInfinity(arr0) || arr0 >= a || _inst.Vehicles[vehicle].FixedDeparture) return d0;

        // Fixed-point iteration d <- d + (a - arr(d)); keep the latest d with arr(d) <= a (FIFO).
        // On overshoot step back by the overshoot (handles 1-ulp rounding), bisect only if that stalls.
        var lo = d0;
        var loArr = arr0;
        var d = d0 + (a - arr0);
        for (var i = 0; i < 12 && a - loArr > 0.5; i++)
        {
            var arr = _oracle.ArrivalTime(from, to, d);
            if (arr <= a)
            {
                lo = d;
                loArr = arr;
                d += a - arr;
            }
            else
            {
                var back = d - (arr - a) - 1e-6;
                d = back > lo ? back : lo + (d - lo) / 2;
            }
        }
        return lo;
    }

    private sealed class SimResult
    {
        public double Departure;
        public double End;
        public double Travel;
        public double Distance;
        public StopVisit[] Visits = Array.Empty<StopVisit>();
        public Violation? FirstViolation;
    }

    /// <summary>
    /// Finds the best feasible break placement for departure <paramref name="d"/>; null if none.
    /// Work time counts the departure delay each placement allows (exact for static travel; the
    /// caller re-checks the final schedule). Returned array marks the stop positions after which
    /// a break is taken.
    /// </summary>
    private bool[]? SearchBreaks(int vehicle, int[] route, double d)
    {
        var veh = _inst.Vehicles[vehicle];
        var rule = _inst.Breaks;
        var n = route.Length;
        bool[]? best = null;
        double bestTravel = double.PositiveInfinity, bestWork = double.PositiveInfinity, bestEnd = double.PositiveInfinity;
        var current = new bool[n];

        void Recurse(int origin, double t, double drive, double travel, double waited, double slack)
        {
            var cur = t;
            var curDrive = drive;
            var curTravel = travel;
            var curWaited = waited;
            var curSlack = slack;
            // State after each stop this level simulates: service end, travel, waiting, slack.
            var localEnd = new double[n];
            var localTravel = new double[n];
            var localWaited = new double[n];
            var localSlack = new double[n];

            for (var j = origin + 1; j <= n; j++)
            {
                var from = j == 0 ? _inst.VehicleStartLocation(vehicle) : _inst.StopLocation(route[j - 1]);
                var to = j == n ? _inst.VehicleEndLocation(vehicle) : _inst.StopLocation(route[j]);
                var arr = _oracle.ArrivalTime(from, to, cur);
                if (double.IsInfinity(arr)) return;
                var dur = arr - cur;

                if (curDrive + dur > rule.MaxDrivingSeconds)
                {
                    // A break is required at some stop k in (origin, j-1].
                    for (var k = origin + 1; k <= j - 1; k++)
                    {
                        if (k < 0) continue;
                        current[k] = true;
                        Recurse(k, localEnd[k] + rule.BreakSeconds, 0, localTravel[k], localWaited[k], localSlack[k]);
                        current[k] = false;
                    }
                    return;
                }

                curDrive += dur;
                curTravel += dur;
                cur = arr;
                if (j == n)
                {
                    if (arr > veh.LatestEnd) return;
                    var departure = veh.FixedDeparture ? d : d + Math.Min(curWaited, curSlack);
                    if (arr - (veh.WorkStartedAt ?? departure) > veh.MaxWorkSeconds) return;
                    var work = arr - departure;
                    // Travel sums of different placements differ by float noise only when they are
                    // "equal", so compare with a tolerance; then least work, then earliest return.
                    var better = curTravel < bestTravel - 1e-9
                        || (Math.Abs(curTravel - bestTravel) <= 1e-9
                            && (work < bestWork - 1e-9 || (Math.Abs(work - bestWork) <= 1e-9 && arr < bestEnd)));
                    if (better)
                    {
                        bestTravel = curTravel;
                        bestWork = work;
                        bestEnd = arr;
                        best = (bool[])current.Clone();
                    }
                    return;
                }

                var stop = _inst.Stops[route[j]];
                if (arr < stop.WindowStart) curWaited += stop.WindowStart - arr;
                var start = Math.Max(arr, stop.WindowStart);
                if (start > stop.WindowEnd) return; // breaks only delay, so no placement helps
                curSlack = Math.Min(curSlack, curWaited + stop.WindowEnd - start);
                cur = start + stop.ServiceSeconds;
                localEnd[j] = cur;
                localTravel[j] = curTravel;
                localWaited[j] = curWaited;
                localSlack[j] = curSlack;
            }
        }

        Recurse(-1, d, veh.InitialDrivingSeconds, 0, 0, double.PositiveInfinity);
        return best;
    }

    /// <summary>
    /// Simulates a schedule. With <paramref name="breaks"/> given, breaks are taken exactly there.
    /// In diagnostic mode breaks are taken at the latest possible stop and the simulation continues
    /// past violations, recording the earliest one.
    /// </summary>
    private SimResult Simulate(int vehicle, int[] route, double d, bool[]? breaks, bool diagnostic)
    {
        var veh = _inst.Vehicles[vehicle];
        var rule = _inst.Breaks;
        var n = route.Length;
        var res = new SimResult { Departure = d };
        var visits = new StopVisit[n];
        var takenBreak = new bool[n];
        var cur = d;
        double drive = veh.InitialDrivingSeconds;
        var workStart = veh.WorkStartedAt ?? d;

        void Report(ViolationKind kind, int stop, double time, string message)
        {
            if (res.FirstViolation is null || time < res.FirstViolation.Time)
                res.FirstViolation = new Violation(kind, stop, time, message);
        }

        for (var j = 0; j <= n; j++)
        {
            var from = j == 0 ? _inst.VehicleStartLocation(vehicle) : _inst.StopLocation(route[j - 1]);
            var to = j == n ? _inst.VehicleEndLocation(vehicle) : _inst.StopLocation(route[j]);
            var leg = _oracle.Leg(from, to, cur);

            if (diagnostic && !double.IsInfinity(leg.Arrival) && drive + (leg.Arrival - cur) > rule.MaxDrivingSeconds)
            {
                if (j > 0 && !takenBreak[j - 1])
                {
                    takenBreak[j - 1] = true;
                    var prev = visits[j - 1];
                    visits[j - 1] = prev with { Departure = prev.Departure + rule.BreakSeconds, BreakAfter = true };
                    cur += rule.BreakSeconds;
                    drive = 0;
                    leg = _oracle.Leg(from, to, cur);
                }
                if (drive + (leg.Arrival - cur) > rule.MaxDrivingSeconds)
                {
                    Report(ViolationKind.DrivingLimit, j == 0 ? -1 : route[j - 1], cur,
                        $"連續行車 {TimeOfDay.FormatDuration(drive + leg.Arrival - cur)} 超過 {TimeOfDay.FormatDuration(rule.MaxDrivingSeconds)}，且沒有可休息的站點");
                }
            }

            if (double.IsInfinity(leg.Arrival))
            {
                var name = j == n ? "終點據點" : _inst.Stops[route[j]].Name;
                Report(ViolationKind.Unreachable, j == n ? -1 : route[j], cur, $"{name} 無法從路網抵達");
                for (var k = j; k < n; k++) visits[k] = new StopVisit(route[k], double.NaN, double.NaN, double.NaN, false);
                res.End = double.PositiveInfinity;
                break;
            }

            var dur = leg.Arrival - cur;
            drive += dur;
            res.Travel += dur;
            res.Distance += leg.DistanceM;
            cur = leg.Arrival;

            if (j == n)
            {
                res.End = cur;
                if (cur > veh.LatestEnd)
                    Report(ViolationKind.LatestReturn, -1, cur, $"{TimeOfDay.Format(cur)} 回到據點，晚於 {TimeOfDay.Format(veh.LatestEnd)}");
                if (cur - workStart > veh.MaxWorkSeconds)
                    Report(ViolationKind.MaxWork, -1, cur, $"工時 {TimeOfDay.FormatDuration(cur - workStart)} 超過上限 {TimeOfDay.FormatDuration(veh.MaxWorkSeconds)}");
                break;
            }

            var stop = _inst.Stops[route[j]];
            var start = Math.Max(cur, stop.WindowStart);
            if (start > stop.WindowEnd)
            {
                Report(ViolationKind.TimeWindow, route[j], cur,
                    $"{stop.Name} {TimeOfDay.Format(cur)} 到，時間窗 {TimeOfDay.Format(stop.WindowStart)}–{TimeOfDay.Format(stop.WindowEnd)}");
            }
            var departure = start + stop.ServiceSeconds;
            var breakHere = breaks is not null && breaks[j];
            if (breakHere)
            {
                departure += rule.BreakSeconds;
                drive = 0;
                takenBreak[j] = true;
            }
            visits[j] = new StopVisit(route[j], cur, start, departure, breakHere);
            cur = departure;
        }

        res.Visits = visits;
        return res;
    }
}
