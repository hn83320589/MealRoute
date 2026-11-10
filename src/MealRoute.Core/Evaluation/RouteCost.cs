namespace MealRoute.Core.Evaluation;

/// <summary>Scalar result of a route evaluation (no visits, no diagnostics).</summary>
public readonly record struct RouteCost(bool Feasible, double Departure, double End, double Travel, double Distance)
{
    public static RouteCost Empty { get; } = new(true, 0, 0, 0, 0);
    public static RouteCost Infeasible { get; } = new(false, double.NaN, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

    public double Work => End - Departure;
}
