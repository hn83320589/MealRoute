using System.Diagnostics;
using System.Text;
using MealRoute.Core.Benchmark;
using MealRoute.Core.Optimization;

namespace MealRoute.Cli;

internal static class SolomonCommand
{
    public static int Run(CliArgs args)
    {
        var dir = args.Get("dir", Path.Combine("benchmarks", "solomon"));
        var only = args.Get("only", "");
        var seconds = args.GetDouble("seconds", 60);
        var iterations = args.GetInt("iterations", 1_000_000);
        var runs = args.GetInt("runs", 4);
        var seed = (ulong)args.GetInt("seed", 1);
        var (source, convention, bks) = BestKnownTable.Load(Path.Combine(dir, "bks.json"));

        var files = Directory.GetFiles(dir, "*.txt")
            .Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(only, StringComparison.OrdinalIgnoreCase))
            .Where(f => !(only.Equals("r1", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).StartsWith("rc", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new ArgumentException($"在 {dir} 找不到符合 '{only}' 的實例。");

        var table = new StringBuilder();
        table.AppendLine($"| 實例 | 車輛 | 距離 | BKS 車輛 | BKS 距離 | 距離比 | 判定 | 時間 (s) | 迭代 |");
        table.AppendLine("|---|---:|---:|---:|---:|---:|:---:|---:|---:|");
        var passed = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var inst = SolomonInstance.Load(file).ToProblemInstance();
            var oracle = SolomonInstance.Oracle(inst);
            var sw = Stopwatch.StartNew();
            var res = new Optimizer(inst, oracle, oracle).Run(null, LockSet.Empty, new OptimizerOptions
            {
                Seed = seed,
                MaxIterations = iterations,
                Runs = runs,
                TimeLimit = TimeSpan.FromSeconds(seconds),
            }, null, default);
            sw.Stop();

            var vehicles = res.Objective.VehiclesUsed;
            var distance = res.Solution.Routes.Where(r => !r.IsEmpty).Sum(r => DistanceOf(oracle, inst, r));
            var best = bks[name];
            var ratio = distance / best.Distance;
            var ok = res.Objective.UnservedMandatory == 0 && vehicles <= best.Vehicles + 1 && ratio <= 1.05;
            if (ok) passed++;
            var line = $"| {name} | {vehicles} | {distance:F2} | {best.Vehicles} | {best.Distance:F2} | {ratio:P1} | {(ok ? "✅" : "❌")} | {sw.Elapsed.TotalSeconds:F0} | {res.Iterations} |";
            table.AppendLine(line);
            Console.WriteLine(line);
        }

        var summary = $"{passed}/{files.Length} 個實例符合「車輛數 ≤ BKS + 1 且距離 ≤ BKS × 105%」。每個實例時間上限 {seconds:F0} 秒、{runs} 次獨立搜尋、種子 {seed}、{Environment.ProcessorCount} 個邏輯核心。";
        Console.WriteLine(summary);
        if (args.Get("out") is { } outFile)
        {
            File.WriteAllText(outFile,
                $"# Solomon 基準結果\n\n{summary}\n\nBKS 來源：{source}\n\n取整慣例：{convention}\n\n{table}", new UTF8Encoding(false));
            Console.WriteLine($"已寫入 {outFile}");
        }
        return passed == files.Length ? 0 : 3;
    }

    private static double DistanceOf(Core.Travel.EuclideanOracle oracle, Core.Model.ProblemInstance inst, Core.Model.RoutePlan r)
    {
        var d = oracle.Distance(inst.VehicleStartLocation(r.Vehicle), r.Stops[0]);
        for (var i = 1; i < r.Stops.Count; i++) d += oracle.Distance(r.Stops[i - 1], r.Stops[i]);
        return d + oracle.Distance(r.Stops[^1], inst.VehicleEndLocation(r.Vehicle));
    }
}
