using System.Text;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Projects;
using MealRoute.Core.Reports;
using MealRoute.Core.Travel;
using MealRoute.Tests.Editing;
using MealRoute.Tests.Optimization;
using MealRoute.Tests.Reference;
using static MealRoute.Tests.Evaluation.TestInstances;

namespace MealRoute.Tests.Projects;

public class ProjectAndExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mealroute-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectAndExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort cleanup of temp files */ }
        GC.SuppressFinalize(this);
    }

    internal static void AssertSameSolution(Solution expected, Solution actual)
    {
        Assert.Equal(expected.Routes.Count, actual.Routes.Count);
        for (var v = 0; v < expected.Routes.Count; v++)
        {
            var e = expected.Routes[v];
            var a = actual.Routes[v];
            Assert.Equal(e.Vehicle, a.Vehicle);
            Assert.Equal(e.Stops, a.Stops);
            if (e.IsEmpty) continue;
            Assert.Equal(e.Departure, a.Departure);
            Assert.Equal(e.EndArrival, a.EndArrival);
            Assert.Equal(e.Visits, a.Visits);
        }
        Assert.Equal(expected.Unserved.OrderBy(u => u.Stop), actual.Unserved.OrderBy(u => u.Stop));
    }

    /// <summary>A solved instance with tricky names and some unserved stops.</summary>
    private static (ProblemInstance Inst, Solution Sol) Sample()
    {
        var (inst, sol) = PlanEditorTests.Solved(11, stops: 25, vehicles: 3);
        var tricky = inst.Stops.Select((s, i) => i switch
        {
            0 => s with { Name = "王奶奶, \"阿嬤\"", Address = "中正路 1 段 2 號\n後棟", Note = "電鈴壞了，請敲門" },
            1 => s with { Name = "Lee, John" },
            _ => s,
        }).ToArray();
        return (new ProblemInstance(tricky, inst.Vehicles, inst.Breaks), sol);
    }

    // ------------------------------------------------------------------ project file (#20)

    private ProjectData Project(ProblemInstance inst, Solution sol)
    {
        var v = Enumerable.Range(0, inst.Vehicles.Count).First(x => sol.Routes[x].Stops.Count >= 2);
        var locks = new LockSet(new HashSet<int> { v }, new Dictionary<int, (int, int)> { [sol.Routes[v].Stops[1]] = (v, 1) });
        var flat = Enumerable.Range(0, 96).Select(k => 20.0 + k % 7).ToArray();
        return new ProjectData("測試專案", new NetworkReference("city.geojson", "ABCDEF"), new SpeedProfile(flat, flat.Select(x => x - 5).ToArray(), flat.Select(x => x - 10).ToArray()),
            BreakRule.Default, inst.Stops, inst.Vehicles.Select((x, i) => i == 0 ? x with { MaxWorkSeconds = double.PositiveInfinity } : x).ToArray(),
            sol, locks, new OptimizerSettings(42, 30, 1234, 3));
    }

    [Fact]
    public void Project_save_and_load_is_lossless()
    {
        var (inst, sol) = Sample();
        var p = Project(inst, sol);
        var path = Path.Combine(_dir, "day.mealroute.json");
        ProjectSerializer.Save(p, path);
        var q = ProjectSerializer.Load(path);

        Assert.Equal(p.Name, q.Name);
        Assert.Equal(p.Network, q.Network);
        Assert.Equal(p.Breaks, q.Breaks);
        Assert.Equal(p.Stops, q.Stops);
        Assert.Equal(p.Vehicles, q.Vehicles);
        Assert.Equal(p.Optimizer, q.Optimizer);
        foreach (var c in Enum.GetValues<Core.Network.RoadClass>())
            Assert.Equal(p.SpeedProfile.SpeedsKmh(c), q.SpeedProfile.SpeedsKmh(c));
        AssertSameSolution(p.Solution!, q.Solution!);
        Assert.Equal(p.Locks.LockedVehicles, q.Locks.LockedVehicles);
        Assert.Equal(p.Locks.LockedStops, q.Locks.LockedStops);
        Assert.False(File.Exists(path + ".tmp"));
        // saving the loaded project again gives the identical file
        Assert.Equal(ProjectSerializer.Serialize(p), ProjectSerializer.Serialize(q));
    }

    [Fact]
    public void Project_without_solution_round_trips()
    {
        var (inst, _) = Sample();
        var p = new ProjectData("空", null, SpeedProfile.Default, BreakRule.None, inst.Stops, inst.Vehicles, null, LockSet.Empty, new OptimizerSettings());
        var q = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.Null(q.Solution);
        Assert.Null(q.Network);
        Assert.Equal(BreakRule.None, q.Breaks);
    }

    [Fact]
    public void Newer_project_version_gives_a_clear_message()
    {
        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.Deserialize("{\"schemaVersion\": 7, \"whatever\": []}"));
        Assert.Contains("較新版本", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\": 1, \"stops\": [ {\"id\": ", "損毀")]
    [InlineData("not json at all", "損毀")]
    [InlineData("[1,2,3]", "schemaVersion")]
    [InlineData("{\"schemaVersion\": 1}", "stops")]
    [InlineData("{\"schemaVersion\": 1, \"stops\": [], \"vehicles\": [], \"solution\": {\"routes\": [], \"unserved\": [{\"stop\": \"ghost\", \"reason\": \"\"}]}}", "ghost")]
    [InlineData("{\"schemaVersion\": 1, \"stops\": [], \"vehicles\": [], \"speedProfile\": {\"arterial\": [1], \"collector\": [1], \"residential\": [1]}}", "速度表")]
    public void Corrupt_or_inconsistent_projects_give_clear_messages(string json, string fragment)
    {
        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_project_file_gives_a_clear_message()
    {
        var ex = Assert.Throws<ProjectFileException>(() => ProjectSerializer.Load(Path.Combine(_dir, "nope.json")));
        Assert.Contains("找不到", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_hash_detects_changes()
    {
        var path = Path.Combine(_dir, "net.geojson");
        File.WriteAllText(path, "{}");
        var a = ProjectSerializer.Sha256Of(path);
        File.WriteAllText(path, "{ }");
        Assert.NotEqual(a, ProjectSerializer.Sha256Of(path));
    }

    // ------------------------------------------------------------------ exports (#19)

    [Fact]
    public void Csv_has_utf8_bom_and_round_trips_including_unserved_and_tricky_text()
    {
        var (inst, sol) = Sample();
        var path = Path.Combine(_dir, "routes.csv");
        RouteCsv.WriteFile(path, inst, sol);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Contains("\"王奶奶, \"\"阿嬤\"\"\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        AssertSameSolution(sol, RouteCsv.ReadFile(path, inst));
    }

    [Fact]
    public void Csv_with_unserved_reasons_round_trips()
    {
        var inst = Instance(new[] { Stop("a", 100), new Stop("b", "遠方, 的站", new(0, 1e6), "", "", 1, 0, 0, 0, 10, true, 1) }, new[] { Vehicle() });
        var sol = OptimizerTests.Optimize(inst).Solution;
        Assert.Single(sol.Unserved);
        AssertSameSolution(sol, RouteCsv.Read(RouteCsv.Write(inst, sol), inst));
    }

    [Fact]
    public void Csv_with_wrong_header_is_rejected()
    {
        var (inst, _) = Sample();
        var ex = Assert.Throws<FormatException>(() => RouteCsv.Read("a,b,c\r\n1,2,3\r\n", inst));
        Assert.Contains("標頭", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeoJson_export_round_trips_and_uses_the_route_geometry_provider()
    {
        var (inst, sol) = Sample();
        var calls = 0;
        var json = RouteGeoJson.Write(inst, sol, r => { calls++; return new[] { new GeoPoint(1, 2), new GeoPoint(3, 4) }; });
        Assert.Equal(sol.Routes.Count(r => !r.IsEmpty), calls);
        Assert.Contains("\"LineString\"", json, StringComparison.Ordinal);
        AssertSameSolution(sol, RouteGeoJson.Read(json, inst));
        // re-import → export again gives identical content
        Assert.Equal(json, RouteGeoJson.Write(inst, RouteGeoJson.Read(json, inst), r => new[] { new GeoPoint(1, 2), new GeoPoint(3, 4) }));
    }

    [Fact]
    public void GeoJson_referring_to_unknown_stops_is_rejected()
    {
        var (inst, _) = Sample();
        const string json = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":null,\"properties\":{\"kind\":\"unserved\",\"stopId\":\"zzz\",\"reason\":\"\"}}]}";
        var ex = Assert.Throws<FormatException>(() => RouteGeoJson.Read(json, inst));
        Assert.Contains("zzz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_sheet_times_match_the_solution_and_the_reference_checker()
    {
        var (inst, sol) = Sample();
        var sheets = RouteSheetBuilder.Build(inst, sol);
        Assert.Equal(sol.Routes.Count(r => !r.IsEmpty), sheets.Count);
        var rep = RefFeasibilityChecker.Check(inst, sol, Oracle(inst).ArrivalTime, timeTolerance: 1.0, requireAllMandatory: false);
        Assert.True(rep.Feasible, rep.ToString());
        foreach (var sheet in sheets)
        {
            var route = sol.Routes[sheet.Vehicle];
            Assert.Equal(route.Visits.Select(v => v.Arrival), sheet.Rows.Select(r => r.Arrival));
            Assert.Equal(Enumerable.Range(1, route.Stops.Count), sheet.Rows.Select(r => r.Sequence));
            Assert.Equal(route.Stops.Sum(s => inst.Stops[s].Meals), sheet.TotalMeals);
            Assert.Contains(sheet.Driver, sheet.ToText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Route_sheet_text_marks_breaks()
    {
        var inst = Instance(new[] { Stop("A", 6000), Stop("B", 12000) }, new[] { Vehicle() });
        var sol = OptimizerTests.Optimize(inst).Solution;
        var text = RouteSheetBuilder.Build(inst, sol).Single().ToText();
        Assert.Contains("休息 30 分鐘", text, StringComparison.Ordinal);
    }
}
