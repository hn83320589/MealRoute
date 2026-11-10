namespace MealRoute.Core.Model;

/// <summary>Timing of one stop in a route.</summary>
/// <param name="BreakAfter">The driver takes the break here after service (departure includes it).</param>
public sealed record StopVisit(int Stop, double Arrival, double ServiceStart, double Departure, bool BreakAfter);

/// <summary>Plain-data route: what the solution reports, stores and exports.</summary>
/// <param name="Departure">Departure from the start depot.</param>
/// <param name="EndArrival">Arrival at the end depot (0 for an unused vehicle).</param>
public sealed record RoutePlan(int Vehicle, IReadOnlyList<int> Stops, double Departure, IReadOnlyList<StopVisit> Visits, double EndArrival)
{
    public bool IsEmpty => Stops.Count == 0;

    public static RoutePlan Empty(int vehicle) => new(vehicle, Array.Empty<int>(), 0, Array.Empty<StopVisit>(), 0);
}

public sealed record UnservedStop(int Stop, string Reason);

/// <summary>A complete solution: one plan per vehicle (index = vehicle) and the unserved stops.</summary>
public sealed record Solution(IReadOnlyList<RoutePlan> Routes, IReadOnlyList<UnservedStop> Unserved);
