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
/// <item>Departure: delayed from the vehicle's earliest start so the first stop needs no waiting
/// (within 0.5 s); if that schedule is infeasible the earliest start is tried.</item>
/// <item>Breaks: considered only where accumulated driving would otherwise exceed the limit; every
/// allowed stop up to that point is tried (recursively for further breaks) and the feasible
/// schedule with least travel time, then earliest return, is kept.</item>
/// <item>Infeasible routes get a diagnostic schedule (latest-possible breaks, continuing past
/// violations) and the earliest violation in time.</item>
/// </list>
/// Not thread-safe only in the sense of the oracle; the evaluator itself keeps no state.
/// </summary>
public sealed class RouteEvaluator
{
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

        if (capacity is null)
        {
            foreach (var d in dStar == d0 ? new[] { d0 } : new[] { dStar, d0 })
            {
                var breaks = SearchBreaks(vehicle, route, d);
                if (breaks is not null)
                {
                    var sim = Simulate(vehicle, route, d, breaks, diagnostic: false);
                    return Build(vehicle, route, sim, meals, boxes, null);
                }
            }
        }

        var diag = Simulate(vehicle, route, dStar, null, diagnostic: true);
        var first = capacity ?? diag.FirstViolation
            ?? throw new InvalidOperationException("Route reported infeasible but no violation was found.");
        return Build(vehicle, route, diag, meals, boxes, first);
    }

    /// <summary>
    /// Same verdict and costs as <see cref="Evaluate"/> without building visits or diagnostics.
    /// Allocation-free unless the driving limit forces a break search. Optionally writes each
    /// stop's departure time into <paramref name="departures"/> (length ≥ route length).
    /// </summary>
    public RouteCost EvaluateCost(int vehicle, ReadOnlySpan<int> route, Span<double> departures = default)
    {
        if (route.IsEmpty) return RouteCost.Empty;
        var veh = _inst.Vehicles[vehicle];
        int meals = 0, boxes = 0;
        foreach (var s in route) { meals += _inst.Stops[s].Meals; boxes += _inst.Stops[s].Boxes; }
        if (meals > veh.MealCapacity || boxes > veh.BoxCapacity) return RouteCost.Infeasible;

        var d0 = veh.EarliestStart;
        var dStar = DelayedDeparture(vehicle, route[0], d0);
        var tried = 0;
        for (var d = dStar; tried < 2; d = d0, tried++)
        {
            if (tried == 1 && dStar == d0) break;
            var status = ForwardNoBreak(vehicle, route, d, departures, out var cost);
            if (status == ForwardStatus.Feasible) return cost;
            if (status == ForwardStatus.DrivingLimit)
            {
                var arr = route.ToArray();
                var breaks = SearchBreaks(vehicle, arr, d);
                if (breaks is not null)
                {
                    var sim = Simulate(vehicle, arr, d, breaks, diagnostic: false);
                    if (!departures.IsEmpty)
                        for (var i = 0; i < arr.Length; i++) departures[i] = sim.Visits[i].Departure;
                    return new RouteCost(true, sim.Departure, sim.End, sim.Travel, sim.Distance);
                }
            }
        }
        return RouteCost.Infeasible;
    }

    private enum ForwardStatus { Feasible, Infeasible, DrivingLimit }

    private ForwardStatus ForwardNoBreak(int vehicle, ReadOnlySpan<int> route, double d, Span<double> departures, out RouteCost cost)
    {
        cost = RouteCost.Infeasible;
        var veh = _inst.Vehicles[vehicle];
        var limit = _inst.Breaks.MaxDrivingSeconds;
        var n = route.Length;
        double cur = d, drive = veh.InitialDrivingSeconds, distance = 0;
        var workStart = veh.WorkStartedAt ?? d;
        var from = _inst.VehicleStartLocation(vehicle);
        for (var j = 0; j <= n; j++)
        {
            var to = j == n ? _inst.VehicleEndLocation(vehicle) : _inst.StopLocation(route[j]);
            var leg = _oracle.Leg(from, to, cur);
            if (double.IsInfinity(leg.Arrival)) return ForwardStatus.Infeasible;
            drive += leg.Arrival - cur;
            if (drive > limit) return ForwardStatus.DrivingLimit;
            distance += leg.DistanceM;
            cur = leg.Arrival;
            if (j == n) break;
            var stop = _inst.Stops[route[j]];
            if (cur < stop.WindowStart) cur = stop.WindowStart;
            if (cur > stop.WindowEnd) return ForwardStatus.Infeasible;
            cur += stop.ServiceSeconds;
            if (!departures.IsEmpty) departures[j] = cur;
            from = to;
        }
        if (cur > veh.LatestEnd || cur - workStart > veh.MaxWorkSeconds) return ForwardStatus.Infeasible;
        cost = new RouteCost(true, d, cur, drive - veh.InitialDrivingSeconds, distance);
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
    /// Returned array marks the stop positions after which a break is taken.
    /// </summary>
    private bool[]? SearchBreaks(int vehicle, int[] route, double d)
    {
        var veh = _inst.Vehicles[vehicle];
        var rule = _inst.Breaks;
        var n = route.Length;
        bool[]? best = null;
        double bestTravel = double.PositiveInfinity, bestEnd = double.PositiveInfinity;
        var current = new bool[n];

        void Recurse(int origin, double t, double drive, double travel)
        {
            var cur = t;
            var curDrive = drive;
            var curTravel = travel;
            // Service-end time and travel so far at each stop this level simulates.
            var localEnd = new double[n];
            var localTravel = new double[n];

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
                        Recurse(k, localEnd[k] + rule.BreakSeconds, 0, localTravel[k]);
                        current[k] = false;
                    }
                    return;
                }

                curDrive += dur;
                curTravel += dur;
                cur = arr;
                if (j == n)
                {
                    if (arr > veh.LatestEnd || arr - (veh.WorkStartedAt ?? d) > veh.MaxWorkSeconds) return;
                    // Travel sums of different placements differ by float noise only when they are
                    // "equal", so compare with a tolerance and let the earlier return decide.
                    if (curTravel < bestTravel - 1e-9 || (Math.Abs(curTravel - bestTravel) <= 1e-9 && arr < bestEnd))
                    {
                        bestTravel = curTravel;
                        bestEnd = arr;
                        best = (bool[])current.Clone();
                    }
                    return;
                }

                var stop = _inst.Stops[route[j]];
                var start = Math.Max(arr, stop.WindowStart);
                if (start > stop.WindowEnd) return; // breaks only delay, so no placement helps
                cur = start + stop.ServiceSeconds;
                localEnd[j] = cur;
                localTravel[j] = curTravel;
            }
        }

        Recurse(-1, d, veh.InitialDrivingSeconds, 0);
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
