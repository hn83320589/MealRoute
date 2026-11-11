using MealRoute.Core.Geo;

namespace MealRoute.Core.Model;

/// <summary>
/// Stops, vehicles and rules for one planning run. Travel-time oracles index locations as:
/// stops [0, S), vehicle start depots [S, S+V), vehicle end depots [S+V, S+2V).
/// </summary>
public sealed class ProblemInstance
{
    public ProblemInstance(IReadOnlyList<Stop> stops, IReadOnlyList<Vehicle> vehicles, BreakRule breaks)
    {
        Stops = stops;
        Vehicles = vehicles;
        Breaks = breaks;
        StopIndex = stops.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        if (StopIndex.Count != stops.Count) throw new ArgumentException("Stop ids must be unique.", nameof(stops));
    }

    public IReadOnlyList<Stop> Stops { get; }
    public IReadOnlyList<Vehicle> Vehicles { get; }
    public BreakRule Breaks { get; }
    public IReadOnlyDictionary<string, int> StopIndex { get; }

    public int LocationCount => Stops.Count + 2 * Vehicles.Count;
    public int StopLocation(int stop) => stop;
    public int VehicleStartLocation(int vehicle) => Stops.Count + vehicle;
    public int VehicleEndLocation(int vehicle) => Stops.Count + Vehicles.Count + vehicle;

    /// <summary>User-facing name of a location (stop name and id, or the vehicle whose depot it is), for warnings.</summary>
    public string DescribeLocation(int location) =>
        location < Stops.Count
            ? $"站點「{Stops[location].Name}」（{Stops[location].Id}）"
            : $"車輛 {Vehicles[(location - Stops.Count) % Vehicles.Count].Id} 的據點";

    /// <summary>Coordinates of all locations in oracle order.</summary>
    public IReadOnlyList<GeoPoint> LocationPoints() =>
        Stops.Select(s => s.Location)
            .Concat(Vehicles.Select(v => v.StartDepot))
            .Concat(Vehicles.Select(v => v.EndDepot))
            .ToArray();
}
