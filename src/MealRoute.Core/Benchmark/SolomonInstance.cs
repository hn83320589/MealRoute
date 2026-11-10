using System.Globalization;
using System.Text.Json;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Benchmark;

public sealed record SolomonCustomer(int Number, double X, double Y, int Demand, double ReadyTime, double DueDate, double ServiceTime);

/// <summary>
/// A Solomon VRPTW instance (standard text format). Static mode: Euclidean distances in double
/// precision (not rounded), travel time = distance, one homogeneous fleet with the file's
/// vehicle count and capacity, all customers mandatory, no driver breaks or work-time limit.
/// </summary>
public sealed class SolomonInstance
{
    public required string Name { get; init; }
    public required int VehicleCount { get; init; }
    public required int Capacity { get; init; }
    public required SolomonCustomer Depot { get; init; }
    public required IReadOnlyList<SolomonCustomer> Customers { get; init; }

    public static SolomonInstance Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, Path.GetFileNameWithoutExtension(path));
    }

    public static SolomonInstance Parse(TextReader reader, string fallbackName)
    {
        var lines = new List<string>();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        if (lines.Count == 0) throw new FormatException("Solomon 檔案是空的。");

        var name = lines[0];
        var vehicleHeader = lines.FindIndex(l => l.StartsWith("VEHICLE", StringComparison.OrdinalIgnoreCase));
        var customerHeader = lines.FindIndex(l => l.StartsWith("CUSTOMER", StringComparison.OrdinalIgnoreCase));
        if (vehicleHeader < 0 || customerHeader < 0 || vehicleHeader + 2 >= lines.Count)
            throw new FormatException("Solomon 檔案缺少 VEHICLE 或 CUSTOMER 區段。");

        var fleet = Numbers(lines[vehicleHeader + 2], vehicleHeader + 2);
        if (fleet.Length < 2) throw new FormatException($"第 {vehicleHeader + 3} 個非空行應為「車輛數 容量」。");

        var customers = new List<SolomonCustomer>();
        for (var i = customerHeader + 2; i < lines.Count; i++)
        {
            var n = Numbers(lines[i], i);
            if (n.Length < 7) throw new FormatException($"第 {i + 1} 個非空行應有 7 個數值，實際為 {n.Length} 個：「{lines[i]}」");
            customers.Add(new SolomonCustomer((int)n[0], n[1], n[2], (int)n[3], n[4], n[5], n[6]));
        }
        if (customers.Count < 2) throw new FormatException("Solomon 檔案至少需要據點與一位顧客。");
        if (customers[0].Number != 0) throw new FormatException("第一筆資料必須是編號 0 的據點。");

        return new SolomonInstance
        {
            Name = string.IsNullOrWhiteSpace(name) ? fallbackName : name,
            VehicleCount = (int)fleet[0],
            Capacity = (int)fleet[1],
            Depot = customers[0],
            Customers = customers.Skip(1).ToArray(),
        };
    }

    private static double[] Numbers(string line, int index)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                throw new FormatException($"第 {index + 1} 個非空行含有非數值「{parts[i]}」。");
        }
        return result;
    }

    /// <summary>Planar coordinates are stored as GeoPoint(Lat = y, Lon = x).</summary>
    public ProblemInstance ToProblemInstance()
    {
        var stops = Customers.Select(c => new Stop(
            c.Number.ToString(CultureInfo.InvariantCulture), $"顧客 {c.Number}", new GeoPoint(c.Y, c.X), "", "",
            c.Demand, 0, c.ServiceTime, c.ReadyTime, c.DueDate, Mandatory: true, Priority: 1)).ToArray();
        var depot = new GeoPoint(Depot.Y, Depot.X);
        var vehicles = Enumerable.Range(0, VehicleCount).Select(v => new Vehicle(
            $"V{v + 1}", $"車 {v + 1}", depot, depot, Depot.ReadyTime, Depot.DueDate, Capacity, int.MaxValue, double.PositiveInfinity)).ToArray();
        return new ProblemInstance(stops, vehicles, BreakRule.None);
    }

    /// <summary>Euclidean oracle (speed 1) in the instance's location order.</summary>
    public static EuclideanOracle Oracle(ProblemInstance instance) =>
        new(instance.LocationPoints().Select(p => (p.Lon, p.Lat)).ToArray());
}

/// <summary>Best known solutions (vehicles, distance) with their source.</summary>
public sealed record BestKnown(int Vehicles, double Distance);

public static class BestKnownTable
{
    public static (string Source, string Convention, IReadOnlyDictionary<string, BestKnown> Values) Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var values = new Dictionary<string, BestKnown>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.GetProperty("instances").EnumerateObject())
            values[p.Name] = new BestKnown(p.Value[0].GetInt32(), p.Value[1].GetDouble());
        return (root.GetProperty("source").GetString() ?? "", root.GetProperty("convention").GetString() ?? "", values);
    }
}
