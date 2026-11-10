using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Projects;

/// <summary>Reference to the road network file (path relative to the project file when possible).</summary>
public sealed record NetworkReference(string Path, string Sha256);

public sealed record OptimizerSettings(ulong Seed = 1, double TimeLimitSeconds = 60, int MaxIterations = 20_000, int Runs = 4);

/// <summary>Everything a project file stores.</summary>
public sealed record ProjectData(
    string Name,
    NetworkReference? Network,
    SpeedProfile SpeedProfile,
    BreakRule Breaks,
    IReadOnlyList<Stop> Stops,
    IReadOnlyList<Vehicle> Vehicles,
    Solution? Solution,
    LockSet Locks,
    OptimizerSettings Optimizer)
{
    public ProblemInstance ToInstance() => new(Stops, Vehicles, Breaks);
}

/// <summary>The project file cannot be used; the message is meant for the user.</summary>
public sealed class ProjectFileException : Exception
{
    public ProjectFileException(string message) : base(message) { }
    public ProjectFileException(string message, Exception inner) : base(message, inner) { }
    public ProjectFileException() { }
}
