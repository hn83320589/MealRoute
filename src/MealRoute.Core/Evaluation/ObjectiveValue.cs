using MealRoute.Core.Model;

namespace MealRoute.Core.Evaluation;

/// <summary>
/// Lexicographic objective (smaller is better): unserved mandatory stops, then the priority sum of
/// unserved optional stops, vehicles used, total travel time, total work time.
/// In Solomon static mode travel time equals distance.
/// </summary>
public readonly record struct ObjectiveValue(
    int UnservedMandatory,
    int UnservedOptionalPriority,
    int VehiclesUsed,
    double TravelSeconds,
    double WorkSeconds) : IComparable<ObjectiveValue>
{
    public int CompareTo(ObjectiveValue other)
    {
        var c = UnservedMandatory.CompareTo(other.UnservedMandatory);
        if (c != 0) return c;
        c = UnservedOptionalPriority.CompareTo(other.UnservedOptionalPriority);
        if (c != 0) return c;
        c = VehiclesUsed.CompareTo(other.VehiclesUsed);
        if (c != 0) return c;
        // Travel sums of equivalent solutions differ by floating-point noise (different summation
        // order); compare on a microsecond grid so the work-time tier can decide real ties.
        c = Quantize(TravelSeconds).CompareTo(Quantize(other.TravelSeconds));
        return c != 0 ? c : WorkSeconds.CompareTo(other.WorkSeconds);
    }

    private static double Quantize(double seconds) => Math.Round(seconds * 1e6);

    public static bool operator <(ObjectiveValue a, ObjectiveValue b) => a.CompareTo(b) < 0;
    public static bool operator >(ObjectiveValue a, ObjectiveValue b) => a.CompareTo(b) > 0;
    public static bool operator <=(ObjectiveValue a, ObjectiveValue b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ObjectiveValue a, ObjectiveValue b) => a.CompareTo(b) >= 0;

    /// <summary>Objective of a set of routes plus the stops left unserved.</summary>
    public static ObjectiveValue Compute(ProblemInstance instance, IEnumerable<RoutePlan> routes, IEnumerable<int> unservedStops)
    {
        int mandatory = 0, priority = 0, used = 0;
        double travel = 0, work = 0;
        foreach (var s in unservedStops)
        {
            var stop = instance.Stops[s];
            if (stop.Mandatory) mandatory++;
            else priority += stop.Priority;
        }
        foreach (var r in routes)
        {
            if (r.IsEmpty) continue;
            used++;
            work += r.EndArrival - r.Departure;
            for (var i = 0; i <= r.Visits.Count; i++)
            {
                var legStart = i == 0 ? r.Departure : r.Visits[i - 1].Departure;
                var legEnd = i == r.Visits.Count ? r.EndArrival : r.Visits[i].Arrival;
                travel += legEnd - legStart;
            }
        }
        return new ObjectiveValue(mandatory, priority, used, travel, work);
    }

    public static ObjectiveValue Compute(ProblemInstance instance, Solution solution) =>
        Compute(instance, solution.Routes, solution.Unserved.Select(u => u.Stop));

    public override string ToString() =>
        $"必送未服務 {UnservedMandatory}｜選送優先權 {UnservedOptionalPriority}｜車輛 {VehiclesUsed}｜行車 {TimeOfDay.FormatDuration(TravelSeconds)}｜工時 {TimeOfDay.FormatDuration(WorkSeconds)}";
}
