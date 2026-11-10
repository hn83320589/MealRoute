namespace MealRoute.Core.Travel;

/// <summary>
/// Static mode (Solomon benchmarks): Euclidean distance on planar coordinates, not rounded;
/// travel time = distance / speed.
/// </summary>
public sealed class EuclideanOracle : ITravelTimeOracle
{
    private readonly (double X, double Y)[] _points;
    private readonly double _speed;

    public EuclideanOracle(IReadOnlyList<(double X, double Y)> points, double speed = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);
        _points = points.ToArray();
        _speed = speed;
    }

    public int LocationCount => _points.Length;

    public double Distance(int from, int to)
    {
        var dx = _points[from].X - _points[to].X;
        var dy = _points[from].Y - _points[to].Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double ArrivalTime(int from, int to, double departure) => departure + Distance(from, to) / _speed;

    public LegResult Leg(int from, int to, double departure)
    {
        var d = Distance(from, to);
        return new LegResult(departure + d / _speed, d);
    }
}
