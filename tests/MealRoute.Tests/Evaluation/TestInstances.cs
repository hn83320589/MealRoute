using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Travel;

namespace MealRoute.Tests.Evaluation;

/// <summary>Builders for small static (Euclidean, 1 m/s) instances with readable numbers.</summary>
internal static class TestInstances
{
    public static Stop Stop(string id, double x, double a = 0, double b = 86_400, double service = 0,
        int meals = 1, int boxes = 0, bool mandatory = true, int priority = 1, double y = 0) =>
        new(id, id, new GeoPoint(y, x), "", "", meals, boxes, service, a, b, mandatory, priority);

    public static Vehicle Vehicle(string id = "V1", double start = 0, double end = 86_400, int meals = 100, int boxes = 100,
        double maxWork = 86_400, double depotX = 0) =>
        new(id, id, new GeoPoint(0, depotX), new GeoPoint(0, depotX), start, end, meals, boxes, maxWork);

    /// <summary>Static oracle on planar coordinates (x = Lon, y = Lat) at <paramref name="speed"/> m/s.</summary>
    public static EuclideanOracle Oracle(ProblemInstance inst, double speed = 1.0) =>
        new(inst.LocationPoints().Select(p => (p.Lon, p.Lat)).ToArray(), speed);

    public static ProblemInstance Instance(IReadOnlyList<Stop> stops, IReadOnlyList<Vehicle> vehicles, BreakRule? breaks = null) =>
        new(stops, vehicles, breaks ?? BreakRule.Default);
}
