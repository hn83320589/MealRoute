using MealRoute.Core.Geo;
using MealRoute.Core.Network;

namespace MealRoute.Core.GeoJson;

/// <summary>A validated LineString road feature.</summary>
/// <param name="Index">Position of the feature in the source FeatureCollection.</param>
public sealed record RoadFeature(int Index, string Id, GeoPoint[] Coordinates, OnewayDirection Oneway, RoadClass Class);

public enum IssueSeverity
{
    Warning,
    /// <summary>The feature was skipped.</summary>
    Error,
}

public sealed record ImportIssue(IssueSeverity Severity, int FeatureIndex, string? FeatureId, string Message)
{
    public override string ToString() =>
        FeatureId is null
            ? $"Feature #{FeatureIndex}: {Message}"
            : $"Feature #{FeatureIndex} (id={FeatureId}): {Message}";
}

public sealed record GeoJsonReadResult(IReadOnlyList<RoadFeature> Features, IReadOnlyList<ImportIssue> Issues);

/// <summary>The document as a whole is not a readable GeoJSON FeatureCollection.</summary>
public sealed class GeoJsonFormatException : Exception
{
    public GeoJsonFormatException(string message) : base(message) { }
    public GeoJsonFormatException(string message, Exception inner) : base(message, inner) { }
    public GeoJsonFormatException() { }
}
