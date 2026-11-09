namespace MealRoute.Core.Network;

public enum RoadClass : byte
{
    Arterial = 0,
    Collector = 1,
    Residential = 2,
}

public enum OnewayDirection : byte
{
    /// <summary>Two-way road.</summary>
    Both = 0,
    /// <summary>One-way along coordinate order (oneway = true / "yes").</summary>
    Forward = 1,
    /// <summary>One-way against coordinate order (oneway = "-1").</summary>
    Backward = 2,
}
