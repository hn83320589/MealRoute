namespace MealRoute.Tests.Reference;

/// <summary>
/// Independent reference for the speed model. Uses <see cref="decimal"/> arithmetic and a
/// distance-first formulation ("how far can I get before the next slot boundary"), so it shares
/// neither code nor floating-point behaviour with the production implementation.
/// speedsKmh[class][slot], 96 slots of 900 s, cyclic per day.
/// </summary>
internal static class RefTdIntegrator
{
    public static double Arrival(double[][] speedsKmh, int roadClass, double lengthM, double departure)
    {
        var t = (decimal)departure;
        var remaining = (decimal)lengthM;
        if (remaining == 0) return departure;

        while (true)
        {
            var slotStart = Math.Floor(t / 900m) * 900m;
            var slot = (int)((slotStart / 900m) % 96m);
            if (slot < 0) slot += 96;
            var v = (decimal)speedsKmh[roadClass][slot] / 3.6m; // m/s
            var slotEnd = slotStart + 900m;
            var reachable = v * (slotEnd - t);
            if (reachable >= remaining) return (double)(t + remaining / v);
            remaining -= reachable;
            t = slotEnd;
        }
    }

    /// <summary>Arrival time after traversing consecutive edges (class, length) starting at departure.</summary>
    public static double PathArrival(double[][] speedsKmh, IEnumerable<(int Class, double Length)> edges, double departure)
    {
        var t = departure;
        foreach (var (c, len) in edges) t = Arrival(speedsKmh, c, len, t);
        return t;
    }
}
