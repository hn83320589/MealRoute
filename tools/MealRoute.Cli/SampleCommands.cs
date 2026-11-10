using System.Diagnostics;
using System.Text;
using MealRoute.Core.Evaluation;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;
using MealRoute.Core.Optimization;
using MealRoute.Core.Planning;
using MealRoute.Core.Projects;
using MealRoute.Core.Synthetic;
using MealRoute.Core.Travel;

namespace MealRoute.Cli;

internal static class SampleCommands
{
    private static readonly Dictionary<string, (CitySpec City, RosterSpec Roster)> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["small"] = (new CitySpec(32, Seed: 11, RuralRoadKm: 0), new RosterSpec(30, 3, Seed: 11, FarStops: 1)),
        ["medium"] = (new CitySpec(71, Seed: 22, RuralRoadKm: 0), new RosterSpec(100, 7, Seed: 22, FarStops: 2)),
        ["large"] = (new CitySpec(141, Seed: 33, RuralRoadKm: 50), new RosterSpec(200, 15, Seed: 33, FarStops: 3, RemoteStops: 6)),
    };

    /// <summary>generate --size small|medium|large --out dir [--seed n]</summary>
    public static int Generate(CliArgs args)
    {
        var size = args.Get("size", "small");
        if (!Presets.TryGetValue(size, out var preset)) throw new ArgumentException($"--size 必須是 small、medium 或 large：{size}");
        var seed = args.GetInt("seed", preset.City.Seed);
        var outDir = args.Get("out", Path.Combine("samples", size));
        Directory.CreateDirectory(outDir);

        var city = CityGenerator.Generate(preset.City with { Seed = seed });
        var (stops, vehicles) = RosterGenerator.Generate(preset.Roster with { Seed = seed }, city);
        var networkPath = Path.Combine(outDir, "city.geojson");
        File.WriteAllText(networkPath, city.ToGeoJson(), new UTF8Encoding(false));

        var project = new ProjectData($"合成範例（{size}）", new NetworkReference("city.geojson", ProjectSerializer.Sha256Of(networkPath)),
            SpeedProfile.Default, Core.Model.BreakRule.Default, stops, vehicles, null, LockSet.Empty, new OptimizerSettings(Seed: (ulong)seed));
        var projectPath = Path.Combine(outDir, "project.mealroute.json");
        ProjectSerializer.Save(project, projectPath);
        Console.WriteLine($"已產生 {networkPath}（{city.Roads.Count} 條路段）與 {projectPath}（{stops.Length} 站、{vehicles.Length} 車）");
        return 0;
    }

    /// <summary>solve --project file [--seconds n] [--iterations n] [--save] : import, optimise, report metrics.</summary>
    public static int Solve(CliArgs args)
    {
        var projectPath = args.Get("project") ?? throw new ArgumentException("需要 --project");
        var seconds = args.GetDouble("seconds", 60);
        var total = Stopwatch.StartNew();
        var project = ProjectSerializer.Load(projectPath);
        var networkPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, project.Network!.Path);

        var t = Stopwatch.StartNew();
        var read = GeoJsonRoadReader.ReadFile(networkPath);
        var net = NetworkBuilder.Build(read.Features);
        var analysis = NetworkAnalysis.Analyze(net);
        var tNetwork = t.Elapsed.TotalSeconds;

        var inst = project.ToInstance();
        var (h0, h1) = TravelModel.Horizon(inst);
        t.Restart();
        var model = TravelModel.Build(net, analysis, project.SpeedProfile, inst.LocationPoints(), h0, h1, new OracleBuildOptions());
        var tModel = t.Elapsed.TotalSeconds;

        double? firstFeasibleAt = null;
        ObjectiveValue? first = null;
        var progress = new InlineProgress(p =>
        {
            if (firstFeasibleAt is null) { firstFeasibleAt = total.Elapsed.TotalSeconds; first = p.BestObjective; }
        });
        t.Restart();
        var res = new Optimizer(inst, model.Search, model.Exact).Run(null, LockSet.Empty, new OptimizerOptions
        {
            Seed = project.Optimizer.Seed,
            MaxIterations = args.GetInt("iterations", 1_000_000),
            Runs = args.GetInt("runs", project.Optimizer.Runs),
            TimeLimit = TimeSpan.FromSeconds(seconds),
        }, progress, default);
        var tOpt = t.Elapsed.TotalSeconds;
        var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024);

        var breaks = res.Solution.Routes.Sum(r => r.Visits.Count(v => v.BreakAfter));
        var mandatoryTotal = inst.Stops.Count(s => s.Mandatory);
        Console.WriteLine($"專案：{project.Name}");
        Console.WriteLine($"路網：{net.NodeCount} 節點、{net.EdgeCount} 有向邊、{analysis.SmallComponents.Count} 個小分量；匯入 {tNetwork:F2} s");
        Console.WriteLine($"吸附警告：{model.Snapped.Warnings.Count}；L1 快取建立 {tModel:F2} s（{model.Search.SampleTimes.Count} 個取樣時刻）");
        Console.WriteLine($"第一個可行解：匯入起算 {firstFeasibleAt:F2} s，{first}");
        Console.WriteLine($"最佳化 {tOpt:F1} s、{res.Iterations} 次迭代：{res.Objective}");
        Console.WriteLine($"必送 {mandatoryTotal - res.Objective.UnservedMandatory}/{mandatoryTotal} 已服務；未服務 {res.Solution.Unserved.Count} 站；休息 {breaks} 次；記憶體峰值 {peakMb:F0} MB");
        foreach (var u in res.Solution.Unserved)
            Console.WriteLine($"  未服務 {inst.Stops[u.Stop].Id} {inst.Stops[u.Stop].Name}{(inst.Stops[u.Stop].Mandatory ? "（必送）" : "")}：{u.Reason}");

        if (args.Get("save") is not null)
        {
            ProjectSerializer.Save(project with { Solution = res.Solution }, projectPath);
            Console.WriteLine($"已將解寫回 {projectPath}");
        }
        return 0;
    }

    private sealed class InlineProgress(Action<OptimizationProgress> handler) : IProgress<OptimizationProgress>
    {
        private readonly object _gate = new();
        public void Report(OptimizationProgress value) { lock (_gate) handler(value); }
    }
}
