using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Core.Optimization;
using MealRoute.Core.Planning;
using MealRoute.Core.Projects;
using MealRoute.Core.Travel;
using MealRoute.Tests.Benchmark;
using MealRoute.Tests.Reference;
using MealRoute.Tests.Travel;

namespace MealRoute.Tests.Integration;

/// <summary>End to end on the shipped small sample: import → snap → L1/L2 → optimise → independent check.</summary>
public class SamplePipelineTests
{
    [Fact]
    public void Small_sample_pipeline_produces_a_solution_confirmed_by_the_reference_td_checker()
    {
        var projectPath = SolomonTests.RepoFile("samples", "small", "project.mealroute.json");
        var project = ProjectSerializer.Load(projectPath);
        var networkPath = Path.Combine(Path.GetDirectoryName(projectPath)!, project.Network!.Path);
        Assert.Equal(project.Network.Sha256, ProjectSerializer.Sha256Of(networkPath));

        var read = GeoJsonRoadReader.ReadFile(networkPath);
        Assert.Empty(read.Issues);
        var net = NetworkBuilder.Build(read.Features);
        var analysis = NetworkAnalysis.Analyze(net);
        var inst = project.ToInstance();
        var (h0, h1) = TravelModel.Horizon(inst);
        var model = TravelModel.Build(net, analysis, project.SpeedProfile, inst.LocationPoints(), h0, h1, new OracleBuildOptions());

        var res = new Optimizer(inst, model.Search, model.Exact)
            .Run(null, LockSet.Empty, new OptimizerOptions { Seed = 1, MaxIterations = 300, Runs = 2 }, null, default);
        Assert.Equal(0, res.Objective.UnservedMandatory);

        // Independent check: reference TD Dijkstra on the snapped graph, reference speed integration.
        var g = model.Snapped.Graph;
        var refGraph = TimeDependentDijkstraTests.ToRef(g);
        var raw = Enum.GetValues<RoadClass>().Select(project.SpeedProfile.SpeedsKmh).ToArray();
        var nodes = model.LocationNodes;
        var cache = new Dictionary<(int, double), double[]>();
        double RefArrival(int from, int to, double t)
        {
            if (nodes[from] == nodes[to]) return t;
            if (!cache.TryGetValue((nodes[from], t), out var arr))
                cache[(nodes[from], t)] = arr = RefDijkstra.TimeDependent(refGraph, raw, nodes[from], t).Arrival;
            return arr[nodes[to]];
        }
        var rep = RefFeasibilityChecker.Check(inst, res.Solution, RefArrival, timeTolerance: 1.0);
        Assert.True(rep.Feasible, rep.ToString());
        Assert.True(rep.MaxTimeDeviation <= 1.0);

        // Route geometry follows the network and starts/ends at the depot's snapped node.
        var route = res.Solution.Routes.First(r => !r.IsEmpty);
        var line = model.RouteGeometry(inst, route);
        Assert.True(line.Count > route.Stops.Count);
        Assert.Equal(g.NodePosition(nodes[inst.VehicleStartLocation(route.Vehicle)]), line[0]);
    }
}
