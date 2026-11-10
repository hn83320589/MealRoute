using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;

namespace MealRoute.Core.Optimization;

public sealed class OptimizerOptions
{
    public ulong Seed { get; init; } = 1;

    /// <summary>ALNS iterations per run. Results are reproducible when the search ends on this limit.</summary>
    public int MaxIterations { get; init; } = 5_000;

    /// <summary>Optional wall-clock limit (safety stop; makes results machine dependent).</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Independent ALNS runs (fixed count; the result does not depend on thread count).</summary>
    public int Runs { get; init; } = 4;

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Share of iterations spent trying to remove whole routes (vehicle minimisation).</summary>
    public double VehicleReductionShare { get; init; } = 0.3;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <param name="Fraction">Monotone non-decreasing progress in [0, 1].</param>
public sealed record OptimizationProgress(double Fraction, long Iterations, ObjectiveValue BestObjective, TimeSpan Elapsed);

/// <param name="Cancelled">The run was cancelled; <see cref="Solution"/> is still the best complete feasible solution known.</param>
public sealed record OptimizationResult(Solution Solution, ObjectiveValue Objective, long Iterations, bool Cancelled, TimeSpan Elapsed);

/// <summary>
/// Locks kept by (re-)optimisation. A locked route stays exactly as is. A locked stop stays on its
/// vehicle at its absolute position; to guarantee this, all positions up to the last locked
/// position of that vehicle are kept fixed (free changes happen after it).
/// </summary>
public sealed class LockSet
{
    public static LockSet Empty { get; } = new(new HashSet<int>(), new Dictionary<int, (int, int)>());

    public LockSet(IReadOnlySet<int> lockedVehicles, IReadOnlyDictionary<int, (int Vehicle, int Position)> lockedStops)
    {
        LockedVehicles = lockedVehicles;
        LockedStops = lockedStops;
    }

    public IReadOnlySet<int> LockedVehicles { get; }
    public IReadOnlyDictionary<int, (int Vehicle, int Position)> LockedStops { get; }

    /// <summary>Number of leading positions of a vehicle's route that must not change.</summary>
    public int FrozenPrefixLength(int vehicle, int routeLength)
    {
        if (LockedVehicles.Contains(vehicle)) return routeLength;
        var max = -1;
        foreach (var (_, (v, pos)) in LockedStops)
            if (v == vehicle && pos > max) max = pos;
        return Math.Min(routeLength, max + 1);
    }

    /// <summary>Throws when the locks do not match the given solution.</summary>
    public void Validate(Solution solution)
    {
        foreach (var (stop, (v, pos)) in LockedStops)
        {
            var route = solution.Routes[v].Stops;
            if (pos >= route.Count || route[pos] != stop)
                throw new InvalidOperationException($"鎖定的站 {stop} 不在車輛 {v} 的第 {pos + 1} 站。");
        }
    }
}
