using MealRoute.Core.Model;

namespace MealRoute.Tests.Reference;

/// <summary>
/// Independent feasibility checker. Reads only the instance data (stops, vehicles, break rule) and
/// the solution's plain data (stop order, departure, break flags, reported times). It recomputes
/// every time with the supplied travel function (a reference implementation) and never calls the
/// production evaluator.
/// </summary>
internal static class RefFeasibilityChecker
{
    /// <param name="Arrival">arrival(fromLocation, toLocation, departure) from a reference implementation.</param>
    public delegate double ArrivalFunc(int fromLocation, int toLocation, double departure);

    public sealed class Report
    {
        public List<string> Errors { get; } = new();
        public bool Feasible => Errors.Count == 0;
        public double MaxTimeDeviation { get; set; }
        public double TotalTravel { get; set; }
        public double TotalWork { get; set; }
        public int VehiclesUsed { get; set; }
        public override string ToString() => Feasible ? "feasible" : string.Join("; ", Errors);
    }

    public static Report Check(ProblemInstance inst, Solution sol, ArrivalFunc arrival,
        double timeTolerance = 1.0, bool requireAllMandatory = true)
    {
        var rep = new Report();
        int S = inst.Stops.Count, V = inst.Vehicles.Count;
        var seen = new int[S];

        if (sol.Routes.Count != V) rep.Errors.Add($"expected {V} routes, got {sol.Routes.Count}");
        foreach (var u in sol.Unserved) seen[u.Stop]++;

        for (var v = 0; v < sol.Routes.Count; v++)
        {
            var r = sol.Routes[v];
            if (r.Vehicle != v) rep.Errors.Add($"route {v} belongs to vehicle {r.Vehicle}");
            foreach (var s in r.Stops) seen[s]++;
            if (r.Stops.Count == 0) continue;
            CheckRoute(inst, v, r, arrival, timeTolerance, rep);
        }

        for (var s = 0; s < S; s++)
        {
            if (seen[s] != 1) rep.Errors.Add($"stop {inst.Stops[s].Id} appears {seen[s]} times (routes + unserved)");
        }
        if (requireAllMandatory)
        {
            foreach (var u in sol.Unserved)
                if (inst.Stops[u.Stop].Mandatory) rep.Errors.Add($"mandatory stop {inst.Stops[u.Stop].Id} unserved");
        }
        return rep;
    }

    private static void CheckRoute(ProblemInstance inst, int v, RoutePlan r, ArrivalFunc arrival, double tol, Report rep)
    {
        var veh = inst.Vehicles[v];
        int S = inst.Stops.Count, V = inst.Vehicles.Count;
        int startLoc = S + v, endLoc = S + V + v;
        var tag = $"vehicle {veh.Id}";
        rep.VehiclesUsed++;

        if (r.Visits.Count != r.Stops.Count) { rep.Errors.Add($"{tag}: visits/stops length mismatch"); return; }
        if (r.Departure < veh.EarliestStart - 1e-9) rep.Errors.Add($"{tag}: departs {r.Departure} before {veh.EarliestStart}");

        int meals = 0, boxes = 0;
        foreach (var s in r.Stops) { meals += inst.Stops[s].Meals; boxes += inst.Stops[s].Boxes; }
        if (meals > veh.MealCapacity) rep.Errors.Add($"{tag}: meals {meals} > {veh.MealCapacity}");
        if (boxes > veh.BoxCapacity) rep.Errors.Add($"{tag}: boxes {boxes} > {veh.BoxCapacity}");

        double t = r.Departure, drive = 0, travel = 0;
        var loc = startLoc;
        for (var i = 0; i < r.Stops.Count; i++)
        {
            var s = r.Stops[i];
            var stop = inst.Stops[s];
            var arr = arrival(loc, s, t);
            if (double.IsInfinity(arr)) { rep.Errors.Add($"{tag}: {stop.Id} unreachable"); return; }
            drive += arr - t;
            travel += arr - t;
            if (drive > inst.Breaks.MaxDrivingSeconds + 1e-6) rep.Errors.Add($"{tag}: driving {drive:F1}s without break before {stop.Id}");
            var start = arr < stop.WindowStart ? stop.WindowStart : arr;
            if (start > stop.WindowEnd + 1e-6) rep.Errors.Add($"{tag}: {stop.Id} starts {start:F1} after window end {stop.WindowEnd}");
            var dep = start + stop.ServiceSeconds;
            if (r.Visits[i].BreakAfter) { dep += inst.Breaks.BreakSeconds; drive = 0; }

            var vis = r.Visits[i];
            if (vis.Stop != s) rep.Errors.Add($"{tag}: visit {i} reports stop {vis.Stop}, expected {s}");
            Deviation(rep, tag, stop.Id + " arrival", vis.Arrival, arr, tol);
            Deviation(rep, tag, stop.Id + " start", vis.ServiceStart, start, tol);
            Deviation(rep, tag, stop.Id + " departure", vis.Departure, dep, tol);
            t = dep;
            loc = s;
        }

        var end = arrival(loc, endLoc, t);
        if (double.IsInfinity(end)) { rep.Errors.Add($"{tag}: end depot unreachable"); return; }
        drive += end - t;
        travel += end - t;
        if (drive > inst.Breaks.MaxDrivingSeconds + 1e-6) rep.Errors.Add($"{tag}: driving {drive:F1}s without break before returning");
        if (end > veh.LatestEnd + 1e-6) rep.Errors.Add($"{tag}: returns {end:F1} after {veh.LatestEnd}");
        if (end - r.Departure > veh.MaxWorkSeconds + 1e-6) rep.Errors.Add($"{tag}: work {end - r.Departure:F1}s > {veh.MaxWorkSeconds}");
        Deviation(rep, tag, "end", r.EndArrival, end, tol);
        rep.TotalTravel += travel;
        rep.TotalWork += end - r.Departure;
    }

    private static void Deviation(Report rep, string tag, string what, double reported, double expected, double tol)
    {
        var dev = Math.Abs(reported - expected);
        if (double.IsNaN(dev)) dev = double.PositiveInfinity;
        rep.MaxTimeDeviation = Math.Max(rep.MaxTimeDeviation, dev);
        if (dev > tol) rep.Errors.Add($"{tag}: {what} reported {reported:F3}, recomputed {expected:F3}");
    }
}
