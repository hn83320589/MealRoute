using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Evaluation;

public class ObjectiveValueTests
{
    [Fact]
    public void One_unserved_mandatory_stop_is_worse_than_anything_else()
    {
        var a = new ObjectiveValue(1, 0, 1, 10, 10);
        var b = new ObjectiveValue(0, 1000, 30, 1e9, 1e9);
        Assert.True(b < a);
    }

    [Fact]
    public void Optional_priority_sum_beats_vehicle_count()
    {
        Assert.True(new ObjectiveValue(0, 4, 30, 1e9, 1e9) < new ObjectiveValue(0, 5, 1, 0, 0));
    }

    [Fact]
    public void Vehicle_count_beats_travel_time()
    {
        Assert.True(new ObjectiveValue(0, 0, 2, 1e9, 1e9) < new ObjectiveValue(0, 0, 3, 1, 1));
    }

    [Fact]
    public void Travel_time_beats_work_time_and_equal_values_compare_equal()
    {
        Assert.True(new ObjectiveValue(0, 0, 2, 100, 1e9) < new ObjectiveValue(0, 0, 2, 101, 0));
        Assert.Equal(0, new ObjectiveValue(0, 0, 2, 100, 50).CompareTo(new ObjectiveValue(0, 0, 2, 100, 50)));
        Assert.True(new ObjectiveValue(0, 0, 2, 100, 49) < new ObjectiveValue(0, 0, 2, 100, 50));
    }

    [Fact]
    public void Compute_counts_unserved_by_kind_and_ignores_empty_routes()
    {
        var inst = Instance(new[]
        {
            Stop("m", 100),
            Stop("o3", 200, mandatory: false, priority: 3),
            Stop("o5", 300, mandatory: false, priority: 5),
            Stop("served", 50),
        }, new[] { Vehicle("V1"), Vehicle("V2") });
        var evaluator = new RouteEvaluator(inst, Oracle(inst));
        var route = evaluator.Evaluate(0, new[] { 3 }).ToPlan();
        var obj = ObjectiveValue.Compute(inst, new[] { route, RoutePlan.Empty(1) }, new[] { 0, 1, 2 });
        Assert.Equal(new ObjectiveValue(1, 8, 1, 100, 100), obj);
    }

    [Fact]
    public void Compute_travel_excludes_waiting_service_and_breaks()
    {
        var inst = Instance(new[] { Stop("A", 6000, service: 300), Stop("B", 12000, 20000, 30000) }, new[] { Vehicle() });
        var r = new RouteEvaluator(inst, Oracle(inst)).Evaluate(0, new[] { 0, 1 });
        var obj = ObjectiveValue.Compute(inst, new[] { r.ToPlan() }, Array.Empty<int>());
        Assert.Equal(24_000, obj.TravelSeconds, 6);
        Assert.Equal(r.TravelSeconds, obj.TravelSeconds, 6);
        Assert.Equal(r.WorkSeconds, obj.WorkSeconds, 6);
    }
}
