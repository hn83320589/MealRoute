using MealRoute.Core.Evaluation;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Rescheduling;

/// <summary>A driver's report during the day.</summary>
/// <param name="Position">Where the vehicle is (snapped to the network by the travel model).</param>
/// <param name="CompletedStops">Stops already served.</param>
/// <param name="DelayMinutes">The vehicle can continue only after report time + delay.</param>
/// <param name="BrokenDown">The vehicle cannot continue; meals on board are not transferred.</param>
public sealed record VehicleReport(int Vehicle, double ReportTime, GeoPoint Position, IReadOnlyList<int> CompletedStops,
    double DelayMinutes = 0, bool BrokenDown = false);

/// <param name="Now">Current time (seconds from midnight, from the caller's TimeProvider); used for vehicles without a report.</param>
public sealed record RescheduleRequest(Solution CurrentPlan, double Now, IReadOnlyList<VehicleReport> Reports, LockSet Locks);

/// <summary>Builds the travel-time oracles (search, exact) for a set of location points.</summary>
public delegate (ITravelTimeOracle Search, ITravelTimeOracle Exact) TravelModelFactory(IReadOnlyList<GeoPoint> locationPoints, CancellationToken cancellationToken);

public enum VehicleState
{
    NotDeparted,
    EnRouteReported,
    /// <summary>En route without a report: the stop it is heading to (or serving) is committed.</summary>
    EnRouteCommitted,
    BrokenDown,
    Finished,
}

/// <param name="FixedStops">Completed and committed stops (unchanged, original times).</param>
/// <param name="ResumeTime">When the vehicle continues (en route), or its earliest start.</param>
public sealed record VehicleStatus(int Vehicle, VehicleState State, IReadOnlyList<int> FixedStops, double ResumeTime);

public sealed record RescheduleResult(Solution Solution, IReadOnlyList<VehicleStatus> Statuses, ObjectiveValue Objective, bool Cancelled);

/// <summary>
/// Same-day rescheduling. Only stops nobody has set off to serve are re-planned:
/// <list type="bullet">
/// <item>Reporting vehicle: continues from the reported position at report time + delay with the
/// meals still on board (loaded − completed), serving only its own remaining stops (no restocking).
/// Accumulated driving and the work-time limit carry over from the original departure.</item>
/// <item>Departed vehicle without a report: the stop it is heading to or serving (per plan at
/// <c>Now</c>) is committed; it continues from there at the planned departure, own stops only.</item>
/// <item>Broken-down vehicle: its remaining stops are unservable (meals on board are not
/// transferred); if it had not departed yet, its stops are released to other vehicles.</item>
/// <item>Vehicles not yet departed: may serve any released or unassigned stop.</item>
/// </list>
/// Completed and committed stops keep their original times.
/// </summary>
public sealed class Rescheduler
{
    private readonly ProblemInstance _original;
    private readonly TravelModelFactory _travel;

    public Rescheduler(ProblemInstance original, TravelModelFactory travel)
    {
        _original = original;
        _travel = travel;
    }

    public Task<RescheduleResult> RunAsync(RescheduleRequest request, OptimizerOptions options,
        IProgress<OptimizationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(request, options, progress, cancellationToken), CancellationToken.None);

    public RescheduleResult Run(RescheduleRequest request, OptimizerOptions options,
        IProgress<OptimizationProgress>? progress, CancellationToken cancellationToken)
    {
        var V = _original.Vehicles.Count;
        var S = _original.Stops.Count;
        var plan = request.CurrentPlan;
        var reports = request.Reports.ToDictionary(r => r.Vehicle);

        var statuses = new VehicleStatus[V];
        var vehicles = new Vehicle[V];
        var exclusive = new HashSet<int>?[V]; // en-route vehicles: own remaining stops
        var free = new HashSet<int>(plan.Unserved.Select(u => u.Stop));
        var brokenUnserved = new List<UnservedStop>();
        var initialRoutes = new List<int>[V];

        for (var v = 0; v < V; v++)
        {
            var veh = _original.Vehicles[v];
            var route = plan.Routes[v];
            initialRoutes[v] = new List<int>();
            vehicles[v] = veh;

            if (reports.TryGetValue(v, out var rep))
            {
                var completed = route.Stops.Where(rep.CompletedStops.Contains).ToArray();
                var remaining = route.Stops.Where(s => !rep.CompletedStops.Contains(s)).ToArray();
                var departed = completed.Length > 0 || (!route.IsEmpty && route.Departure <= rep.ReportTime);
                if (rep.BrokenDown)
                {
                    statuses[v] = new VehicleStatus(v, VehicleState.BrokenDown, completed, rep.ReportTime);
                    if (departed)
                        brokenUnserved.AddRange(remaining.Select(s => new UnservedStop(s, $"{veh.DriverName} 的車輛故障，車上餐盒不轉運，無法服務")));
                    else
                        free.UnionWith(remaining);
                    continue;
                }

                var resume = rep.ReportTime + rep.DelayMinutes * 60;
                // Conservative: since leaving the last completed stop (or the depot) it has been driving.
                double drive;
                if (completed.Length == 0) drive = Math.Max(0, rep.ReportTime - route.Departure);
                else
                {
                    var last = route.Stops.ToList().IndexOf(completed[^1]);
                    var visit = route.Visits[last];
                    // The planned break at the last completed stop only counts if it is over by the report time.
                    var breakTaken = visit.BreakAfter && rep.ReportTime >= visit.Departure;
                    drive = DrivingAfter(route, last, countBreakAtLast: breakTaken)
                            + Math.Max(0, rep.ReportTime - (breakTaken ? visit.Departure : visit.Departure - (visit.BreakAfter ? _original.Breaks.BreakSeconds : 0)));
                }
                vehicles[v] = EnRoute(veh, rep.Position, resume, drive, route.IsEmpty ? rep.ReportTime : Math.Min(route.Departure, rep.ReportTime), route.Stops, completed);
                exclusive[v] = remaining.ToHashSet();
                initialRoutes[v].AddRange(remaining);
                statuses[v] = new VehicleStatus(v, VehicleState.EnRouteReported, completed, resume);
                continue;
            }

            if (route.IsEmpty || route.Departure > request.Now)
            {
                vehicles[v] = veh with { EarliestStart = Math.Max(veh.EarliestStart, request.Now) };
                free.UnionWith(route.Stops);
                initialRoutes[v].AddRange(route.Stops);
                statuses[v] = new VehicleStatus(v, VehicleState.NotDeparted, Array.Empty<int>(), vehicles[v].EarliestStart);
                continue;
            }

            // En route without a report: commit up to the first stop it has not left yet.
            var k = Enumerable.Range(0, route.Visits.Count).FirstOrDefault(i => route.Visits[i].Departure > request.Now, -1);
            if (k < 0)
            {
                statuses[v] = new VehicleStatus(v, VehicleState.Finished, route.Stops, route.EndArrival);
                continue;
            }
            var fixedStops = route.Stops.Take(k + 1).ToArray();
            var rest = route.Stops.Skip(k + 1).ToArray();
            var at = _original.Stops[route.Stops[k]].Location;
            vehicles[v] = EnRoute(veh, at, route.Visits[k].Departure, DrivingAfter(route, k), route.Departure, route.Stops, fixedStops);
            exclusive[v] = rest.ToHashSet();
            initialRoutes[v].AddRange(rest);
            statuses[v] = new VehicleStatus(v, VehicleState.EnRouteCommitted, fixedStops, route.Visits[k].Departure);
        }

        var sub = new ProblemInstance(_original.Stops, vehicles, _original.Breaks);
        bool CanServe(int v, int s) => statuses[v].State switch
        {
            VehicleState.NotDeparted => free.Contains(s),
            VehicleState.EnRouteReported or VehicleState.EnRouteCommitted => exclusive[v]!.Contains(s),
            _ => false,
        };

        var (search, exact) = _travel(sub.LocationPoints(), cancellationToken);
        var exactEval = new RouteEvaluator(sub, exact);
        var initial = new Solution(
            initialRoutes.Select((r, v) => new RoutePlan(v, r, 0, Array.Empty<StopVisit>(), 0)).ToArray(),
            Array.Empty<UnservedStop>());
        var locks = TranslateLocks(request.Locks, statuses, initialRoutes);

        var optimizer = new Optimizer(sub, search, exact, CanServe);
        var res = optimizer.Run(initial, locks, options, progress, cancellationToken);

        // Merge fixed prefixes (original times) with the re-planned remainder.
        var merged = new RoutePlan[V];
        for (var v = 0; v < V; v++)
        {
            var original = plan.Routes[v];
            var st = statuses[v];
            var tail = res.Solution.Routes[v];
            var fixedVisits = original.Visits.Where(x => st.FixedStops.Contains(x.Stop)).ToList();
            switch (st.State)
            {
                case VehicleState.NotDeparted:
                    merged[v] = tail;
                    break;
                case VehicleState.Finished:
                    merged[v] = original;
                    break;
                case VehicleState.BrokenDown:
                    merged[v] = fixedVisits.Count == 0 ? RoutePlan.Empty(v)
                        : new RoutePlan(v, fixedVisits.Select(x => x.Stop).ToArray(), original.Departure, fixedVisits, st.ResumeTime);
                    break;
                default:
                {
                    var end = tail.IsEmpty
                        ? exactEval.Oracle.ArrivalTime(sub.VehicleStartLocation(v), sub.VehicleEndLocation(v), st.ResumeTime)
                        : tail.EndArrival;
                    var visits = fixedVisits.Concat(tail.Visits).ToArray();
                    merged[v] = new RoutePlan(v, visits.Select(x => x.Stop).ToArray(), original.IsEmpty ? st.ResumeTime : original.Departure, visits, end);
                    break;
                }
            }
        }

        var excluded = new HashSet<int>(statuses.SelectMany(s => s.FixedStops));
        excluded.UnionWith(brokenUnserved.Select(u => u.Stop));
        var unserved = res.Solution.Unserved
            .Where(u => !excluded.Contains(u.Stop))
            .Concat(brokenUnserved)
            .OrderBy(u => u.Stop)
            .ToArray();
        var solution = new Solution(merged, unserved);
        return new RescheduleResult(solution, statuses, ObjectiveValue.Compute(_original, solution), res.Cancelled);
    }

    private Vehicle EnRoute(Vehicle veh, GeoPoint position, double resume, double driving, double workStart,
        IReadOnlyList<int> routeStops, IReadOnlyList<int> done)
    {
        int meals = 0, boxes = 0;
        foreach (var s in routeStops)
        {
            if (done.Contains(s)) continue;
            meals += _original.Stops[s].Meals;
            boxes += _original.Stops[s].Boxes;
        }
        return veh with
        {
            StartDepot = position,
            EarliestStart = resume,
            FixedDeparture = true,
            InitialDrivingSeconds = driving,
            WorkStartedAt = workStart,
            MealCapacity = meals,
            BoxCapacity = boxes,
        };
    }

    /// <summary>Driving accumulated since the last break when leaving stop <paramref name="k"/> of a planned route.</summary>
    private static double DrivingAfter(RoutePlan route, int k, bool countBreakAtLast = true)
    {
        double drive = 0;
        var prev = route.Departure;
        for (var i = 0; i <= k; i++)
        {
            var v = route.Visits[i];
            drive += v.Arrival - prev;
            if (v.BreakAfter && (i < k || countBreakAtLast)) drive = 0;
            prev = v.Departure;
        }
        return drive;
    }

    private static LockSet TranslateLocks(LockSet locks, VehicleStatus[] statuses, List<int>[] routes)
    {
        var stopLocks = new Dictionary<int, (int, int)>();
        foreach (var (stop, (v, _)) in locks.LockedStops)
        {
            var p = routes[v].IndexOf(stop);
            if (p >= 0) stopLocks[stop] = (v, p);
        }
        var routeLocks = new HashSet<int>(locks.LockedVehicles.Where(v => statuses[v].State is VehicleState.NotDeparted or VehicleState.EnRouteReported or VehicleState.EnRouteCommitted));
        return new LockSet(routeLocks, stopLocks);
    }
}
