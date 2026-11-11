using MealRoute.Core.Model;

namespace MealRoute.Tests.Reference;

/// <summary>
/// Exhaustive optimum for small static (Euclidean, speed 1, planar x = Lon, y = Lat) instances
/// (≤ 9 stops, few vehicles). Independent of production code:
/// <list type="bullet">
/// <item>Per vehicle, a DFS enumerates every stop sequence (prefix). Each prefix, closed back to
/// the depot, is a candidate route for exactly that stop subset; the best per subset is kept.</item>
/// <item>Schedule semantics: the departure is a decision (≥ earliest start); the vehicle waits only
/// when it arrives before a window opens; breaks of the given length may be taken after any service
/// and reset accumulated driving, which must never exceed the limit. Work time is return minus
/// departure, so for a fixed sequence and break choice the best departure is the latest one that
/// still returns as early as possible. It is found by a backward pass of latest service starts
/// from the earliest return (an algorithm independent of the production forward-slack one).</item>
/// <item>Per sequence the best schedule minimises travel (constant here) then work time.</item>
/// <item>A DP over vehicles and subsets combines routes; the lexicographic objective is additive.</item>
/// </list>
/// </summary>
internal static class RefExhaustiveSolver
{
    public readonly record struct Objective(int UnservedMandatory, int UnservedPriority, int Vehicles, double Travel, double Work);

    private readonly record struct RouteValue(double Travel, double Work, int[]? Sequence = null)
    {
        public static RouteValue None => new(double.PositiveInfinity, double.PositiveInfinity);
        public bool Exists => !double.IsPositiveInfinity(Travel);
        public bool BetterThan(RouteValue o) => Travel < o.Travel - 1e-9 || (Math.Abs(Travel - o.Travel) <= 1e-9 && Work < o.Work);
    }

    /// <summary>Optimal routes (per vehicle stop sequence) of the last <see cref="SolveWithRoutes"/> call.</summary>
    public static (Objective Objective, int[][] Routes) SolveWithRoutes(ProblemInstance inst)
    {
        var obj = Solve(inst, out var routes);
        return (obj, routes);
    }

    public static Objective Solve(ProblemInstance inst) => Solve(inst, out _);

    private static Objective Solve(ProblemInstance inst, out int[][] routesOut)
    {
        var n = inst.Stops.Count;
        var V = inst.Vehicles.Count;
        if (n > 12) throw new ArgumentException("too many stops for exhaustive search");
        var full = 1 << n;

        var best = new RouteValue[V][];
        for (var v = 0; v < V; v++)
        {
            best[v] = Enumerable.Repeat(RouteValue.None, full).ToArray();
            best[v][0] = new RouteValue(0, 0);
            Enumerate(inst, v, best[v]);
        }

        // DP: f[mask] = best (vehicles, travel, work) serving exactly mask with vehicles 0..v
        var f = new (int Veh, double Travel, double Work)?[full];
        f[0] = (0, 0, 0);
        var choice = new int[V][];
        for (var v = 0; v < V; v++)
        {
            choice[v] = new int[full];
            var g = ((int Veh, double Travel, double Work)?[])f.Clone();
            for (var mask = 1; mask < full; mask++)
            {
                for (var sub = mask; sub > 0; sub = (sub - 1) & mask)
                {
                    var rv = best[v][sub];
                    if (!rv.Exists) continue;
                    var prev = f[mask & ~sub];
                    if (prev is null) continue;
                    var cand = (prev.Value.Veh + 1, prev.Value.Travel + rv.Travel, prev.Value.Work + rv.Work);
                    if (g[mask] is null || Less(cand, g[mask]!.Value)) { g[mask] = cand; choice[v][mask] = sub; }
                }
            }
            f = g;
        }

        Objective? answer = null;
        var answerMask = 0;
        for (var mask = 0; mask < full; mask++)
        {
            if (f[mask] is not { } val) continue;
            int um = 0, up = 0;
            for (var s = 0; s < n; s++)
            {
                if ((mask >> s & 1) == 1) continue;
                if (inst.Stops[s].Mandatory) um++; else up += inst.Stops[s].Priority;
            }
            var obj = new Objective(um, up, val.Veh, val.Travel, val.Work);
            if (answer is null || Less(obj, answer.Value)) { answer = obj; answerMask = mask; }
        }

        routesOut = new int[V][];
        var rest = answerMask;
        for (var v = V - 1; v >= 0; v--)
        {
            var sub = choice[v][rest];
            // choice is only meaningful if vehicle v was used for this mask in the DP
            routesOut[v] = sub != 0 && (sub & ~rest) == 0 && best[v][sub].Exists ? best[v][sub].Sequence! : Array.Empty<int>();
            rest &= ~(routesOut[v].Length == 0 ? 0 : sub);
        }
        return answer!.Value;
    }

    private static bool Less((int Veh, double Travel, double Work) a, (int Veh, double Travel, double Work) b)
    {
        if (a.Veh != b.Veh) return a.Veh < b.Veh;
        if (Math.Abs(a.Travel - b.Travel) > 1e-9) return a.Travel < b.Travel;
        return a.Work < b.Work;
    }

    public static bool Less(Objective a, Objective b)
    {
        if (a.UnservedMandatory != b.UnservedMandatory) return a.UnservedMandatory < b.UnservedMandatory;
        if (a.UnservedPriority != b.UnservedPriority) return a.UnservedPriority < b.UnservedPriority;
        if (a.Vehicles != b.Vehicles) return a.Vehicles < b.Vehicles;
        if (Math.Abs(a.Travel - b.Travel) > 1e-9) return a.Travel < b.Travel;
        return a.Work < b.Work;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void Enumerate(ProblemInstance inst, int v, RouteValue[] best)
    {
        var veh = inst.Vehicles[v];
        var n = inst.Stops.Count;
        var pts = inst.Stops.Select(s => (s.Location.Lon, s.Location.Lat)).ToArray();
        var start = (veh.StartDepot.Lon, veh.StartDepot.Lat);
        var end = (veh.EndDepot.Lon, veh.EndDepot.Lat);
        var limit = inst.Breaks.MaxDrivingSeconds;
        var brk = inst.Breaks.BreakSeconds;

        // Breaks can only matter if some route could drive longer than the limit.
        double maxLeg = 0;
        for (var i = 0; i < n; i++)
        {
            maxLeg = Math.Max(maxLeg, Math.Max(Dist(start, pts[i]), Dist(pts[i], end)));
            for (var j = 0; j < n; j++) maxLeg = Math.Max(maxLeg, Dist(pts[i], pts[j]));
        }
        var breaksPossible = maxLeg * (n + 1) > limit;

        // state: position (stop or -1), time after service (+break), drive since break, travel, mask, meals, boxes.
        // The forward times are the earliest possible (departing at the earliest start).
        var seq = new List<int>();
        var brkAfter = new List<bool>();
        void Dfs(int at, double t, double drive, double travel, int mask, int meals, int boxes)
        {
            // close the route if non-empty
            if (mask != 0)
            {
                var back = Dist(pts[at], end);
                if (drive + back <= limit)
                {
                    var endT = t + back;
                    if (endT <= veh.LatestEnd)
                    {
                        var work = endT - LatestDeparture(endT);
                        if (work <= veh.MaxWorkSeconds)
                        {
                            var rv = new RouteValue(travel + back, work, seq.ToArray());
                            if (rv.BetterThan(best[mask])) best[mask] = rv;
                        }
                    }
                }
            }

            for (var s = 0; s < n; s++)
            {
                if ((mask >> s & 1) == 1) continue;
                var stop = inst.Stops[s];
                if (meals + stop.Meals > veh.MealCapacity || boxes + stop.Boxes > veh.BoxCapacity) continue;

                (double X, double Y) fromPt = at < 0 ? start : pts[at];
                var legFrom = Dist(fromPt, pts[s]);
                if (drive + legFrom > limit) continue;
                var arr = t + legFrom;
                var startService = Math.Max(arr, stop.WindowStart);
                if (startService > stop.WindowEnd) continue;
                var after = startService + stop.ServiceSeconds;
                var nm = mask | 1 << s;

                seq.Add(s);
                brkAfter.Add(false);
                Dfs(s, after, drive + legFrom, travel + legFrom, nm, meals + stop.Meals, boxes + stop.Boxes);
                if (breaksPossible)
                {
                    brkAfter[^1] = true;
                    Dfs(s, after + brk, 0, travel + legFrom, nm, meals + stop.Meals, boxes + stop.Boxes);
                }
                seq.RemoveAt(seq.Count - 1);
                brkAfter.RemoveAt(brkAfter.Count - 1);
            }
        }

        // Latest departure whose schedule (same sequence and breaks) still returns by endT: walk back
        // from the return, each service starting no later than its window end nor than the next
        // leg allows. Static travel, so arriving no later than that start keeps everything after.
        double LatestDeparture(double endT)
        {
            var latest = endT;
            var next = end;
            for (var i = seq.Count - 1; i >= 0; i--)
            {
                var stop = inst.Stops[seq[i]];
                var leaveBy = latest - Dist(pts[seq[i]], next);
                var startBy = leaveBy - stop.ServiceSeconds - (brkAfter[i] ? brk : 0);
                latest = Math.Min(stop.WindowEnd, startBy);
                next = pts[seq[i]];
            }
            return Math.Max(veh.EarliestStart, latest - Dist(start, next));
        }

        Dfs(-1, veh.EarliestStart, 0, 0, 0, 0, 0);
    }
}
