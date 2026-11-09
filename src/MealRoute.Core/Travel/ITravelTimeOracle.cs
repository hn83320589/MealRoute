namespace MealRoute.Core.Travel;

/// <summary>Arrival time and driven distance of one leg.</summary>
public readonly record struct LegResult(double Arrival, double DistanceM);

/// <summary>
/// Travel times between indexed locations (depots, stops, ...). Every implementation is FIFO:
/// departing later never arrives earlier. Unreachable pairs return +∞.
/// </summary>
public interface ITravelTimeOracle
{
    int LocationCount { get; }

    double ArrivalTime(int from, int to, double departure);

    LegResult Leg(int from, int to, double departure);
}
