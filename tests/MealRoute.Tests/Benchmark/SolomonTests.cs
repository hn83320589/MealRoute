using MealRoute.Core.Benchmark;
using MealRoute.Core.Optimization;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Benchmark;

public class SolomonTests
{
    internal static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MealRoute.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(new[] { dir ?? throw new InvalidOperationException("repo root not found") }.Concat(parts).ToArray());
    }

    [Fact]
    public void Reads_c101_header_depot_and_customers()
    {
        var inst = SolomonInstance.Load(RepoFile("benchmarks", "solomon", "c101.txt"));
        Assert.Equal("C101", inst.Name);
        Assert.Equal(25, inst.VehicleCount);
        Assert.Equal(200, inst.Capacity);
        Assert.Equal(100, inst.Customers.Count);
        Assert.Equal(new SolomonCustomer(0, 40, 50, 0, 0, 1236, 0), inst.Depot);
        Assert.Equal(new SolomonCustomer(1, 45, 68, 10, 912, 967, 90), inst.Customers[0]);
    }

    [Fact]
    public void All_29_class_1_instances_and_their_best_known_values_load()
    {
        var (source, _, bks) = BestKnownTable.Load(RepoFile("benchmarks", "solomon", "bks.json"));
        Assert.Contains("sintef", source, StringComparison.OrdinalIgnoreCase);
        var files = Directory.GetFiles(RepoFile("benchmarks", "solomon"), "*.txt");
        Assert.Equal(29, files.Length);
        foreach (var f in files)
        {
            var inst = SolomonInstance.Load(f);
            Assert.Equal(100, inst.Customers.Count);
            Assert.True(bks.ContainsKey(Path.GetFileNameWithoutExtension(f)), f);
        }
    }

    [Fact]
    public void Conversion_uses_euclidean_static_mode_without_breaks()
    {
        var inst = SolomonInstance.Load(RepoFile("benchmarks", "solomon", "r101.txt")).ToProblemInstance();
        var oracle = SolomonInstance.Oracle(inst);
        Assert.Equal(100, inst.Stops.Count);
        Assert.Equal(25, inst.Vehicles.Count);
        Assert.True(double.IsPositiveInfinity(inst.Breaks.MaxDrivingSeconds));
        // depot (35,35) -> customer 1 (41,49): sqrt(36+196), not rounded
        Assert.Equal(Math.Sqrt(36 + 196), oracle.Leg(inst.VehicleStartLocation(0), 0, 0).DistanceM, 12);
        Assert.All(inst.Stops, s => Assert.True(s.Mandatory));
    }

    [Theory]
    [InlineData("C101\nVEHICLE\nNUMBER CAPACITY\n25 200\n", "CUSTOMER")]
    [InlineData("C101\nVEHICLE\nNUMBER CAPACITY\n25 200\nCUSTOMER\nhdr\n0 40 50 0 0 1236\n1 2 3 4 5 6 7\n", "7 個數值")]
    [InlineData("C101\nVEHICLE\nNUMBER CAPACITY\n25 x\nCUSTOMER\nhdr\n0 40 50 0 0 1236 0\n1 2 3 4 5 6 7\n", "非數值")]
    [InlineData("", "空的")]
    public void Malformed_files_report_clear_errors(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => SolomonInstance.Parse(new StringReader(text), "x"));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_optimization_of_c101_is_feasible_per_reference_checker()
    {
        var inst = SolomonInstance.Load(RepoFile("benchmarks", "solomon", "c101.txt")).ToProblemInstance();
        var oracle = SolomonInstance.Oracle(inst);
        var res = new Optimizer(inst, oracle, oracle).Run(null, LockSet.Empty, new OptimizerOptions { MaxIterations = 200, Runs = 1, Seed = 1 }, null, default);
        var rep = RefFeasibilityChecker.Check(inst, res.Solution, oracle.ArrivalTime, 1e-6);
        Assert.True(rep.Feasible, rep.ToString());
        Assert.Equal(0, res.Objective.UnservedMandatory);
    }
}
