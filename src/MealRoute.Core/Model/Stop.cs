using MealRoute.Core.Geo;

namespace MealRoute.Core.Model;

/// <summary>A delivery stop (one elderly recipient).</summary>
/// <param name="WindowStart">Earliest service start (seconds from midnight); earlier arrivals wait.</param>
/// <param name="WindowEnd">Latest service start (hard).</param>
/// <param name="Priority">1..5 for optional stops (higher = more important); ignored for mandatory stops.</param>
public sealed record Stop(
    string Id,
    string Name,
    GeoPoint Location,
    string Address,
    string Note,
    int Meals,
    int Boxes,
    double ServiceSeconds,
    double WindowStart,
    double WindowEnd,
    bool Mandatory,
    int Priority);

/// <summary>A vehicle with its driver.</summary>
/// <param name="EarliestStart">Earliest departure from the start depot.</param>
/// <param name="LatestEnd">Latest arrival at the end depot.</param>
/// <param name="MaxWorkSeconds">Departure to return, including waiting, service and breaks.</param>
/// <param name="InitialDrivingSeconds">Driving already accumulated since the last break (vehicle already en route).</param>
/// <param name="WorkStartedAt">When the working day started, if earlier than this route's departure (en route); used for the work-time limit.</param>
/// <param name="FixedDeparture">Depart exactly at <paramref name="EarliestStart"/> (no departure delay), e.g. a vehicle resuming en route.</param>
public sealed record Vehicle(
    string Id,
    string DriverName,
    GeoPoint StartDepot,
    GeoPoint EndDepot,
    double EarliestStart,
    double LatestEnd,
    int MealCapacity,
    int BoxCapacity,
    double MaxWorkSeconds,
    double InitialDrivingSeconds = 0,
    double? WorkStartedAt = null,
    bool FixedDeparture = false);

/// <summary>Driver break rule: before accumulated driving exceeds the limit, rest at a stop after service.</summary>
public sealed record BreakRule(double MaxDrivingSeconds, double BreakSeconds)
{
    /// <summary>4.5 h driving, 30 min break.</summary>
    public static BreakRule Default { get; } = new(4.5 * 3600, 30 * 60);

    /// <summary>No break requirement (Solomon static mode).</summary>
    public static BreakRule None { get; } = new(double.PositiveInfinity, 0);
}
