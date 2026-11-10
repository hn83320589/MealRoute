using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;

namespace MealRoute.Core.Optimization;

/// <summary>Mutable working solution of one ALNS run (always a complete, evaluated solution).</summary>
internal sealed class SearchState
{
    public SearchState(int vehicles)
    {
        Routes = new List<int>[vehicles];
        Costs = new RouteCost[vehicles];
        Departures = new double[vehicles][];
        for (var v = 0; v < vehicles; v++)
        {
            Routes[v] = new List<int>();
            Costs[v] = RouteCost.Empty;
            Departures[v] = Array.Empty<double>();
        }
    }

    public List<int>[] Routes { get; }
    public RouteCost[] Costs { get; }
    /// <summary>Departure time from each stop of the route (for insertion pre-checks).</summary>
    public double[][] Departures { get; }
    /// <summary>Unassigned stops, kept sorted ascending for determinism.</summary>
    public List<int> Unassigned { get; } = new();

    public SearchState Clone()
    {
        var c = new SearchState(Routes.Length);
        for (var v = 0; v < Routes.Length; v++)
        {
            c.Routes[v].AddRange(Routes[v]);
            c.Costs[v] = Costs[v];
            c.Departures[v] = Departures[v];
        }
        c.Unassigned.AddRange(Unassigned);
        return c;
    }

    public bool AllRoutesFeasible()
    {
        foreach (var c in Costs) if (!c.Feasible) return false;
        return true;
    }

    public ObjectiveValue Objective(ProblemInstance inst)
    {
        int mandatory = 0, priority = 0, used = 0;
        double travel = 0, work = 0;
        foreach (var s in Unassigned)
        {
            if (inst.Stops[s].Mandatory) mandatory++;
            else priority += inst.Stops[s].Priority;
        }
        for (var v = 0; v < Routes.Length; v++)
        {
            if (Routes[v].Count == 0) continue;
            used++;
            travel += Costs[v].Travel;
            work += Costs[v].Work;
        }
        return new ObjectiveValue(mandatory, priority, used, travel, work);
    }

    public void AddUnassigned(int stop)
    {
        var i = Unassigned.BinarySearch(stop);
        if (i < 0) Unassigned.Insert(~i, stop);
    }
}
