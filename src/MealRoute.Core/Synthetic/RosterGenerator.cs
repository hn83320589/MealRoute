using MealRoute.Core.Geo;
using MealRoute.Core.Model;

namespace MealRoute.Core.Synthetic;

/// <param name="FarStops">Stops placed 160–250 m outside the street grid (snapping warnings).</param>
/// <param name="RemoteStops">Stops in outlying villages (served by a long-day vehicle that must take a break).</param>
public sealed record RosterSpec(int Stops, int Vehicles, int Seed = 1, double OptionalShare = 0.2, int FarStops = 2, int RemoteStops = 0);

/// <summary>Synthetic lunch roster: stops with windows, demands, priorities, and vehicles.</summary>
public static class RosterGenerator
{
    private static readonly string[] Surnames = { "王", "李", "張", "陳", "林", "黃", "吳", "劉", "蔡", "楊", "許", "鄭", "謝", "郭", "洪", "曾", "邱", "廖", "賴", "周" };
    private static readonly string[] Titles = { "奶奶", "爺爺", "阿嬤", "阿公", "伯伯", "阿姨" };
    private static readonly string[] Notes = { "", "", "", "行動不便，請送到門口", "電鈴故障請敲門", "需確認服藥", "家中有狗", "請放在鞋櫃上" };

    private const double MetresPerDegLat = 111_195;

    public static (Stop[] Stops, Vehicle[] Vehicles) Generate(RosterSpec spec, SyntheticCity city)
    {
        var rnd = new Random(spec.Seed);
        var urban = city.Roads.Where(r => !r.Id.StartsWith("rural", StringComparison.Ordinal) && !r.Id.StartsWith("village", StringComparison.Ordinal)
                                          && !r.Id.StartsWith("island", StringComparison.Ordinal) && r.Id != "trap").ToArray();
        var stops = new List<Stop>();
        var urbanCount = spec.Stops - spec.RemoteStops;
        for (var i = 0; i < urbanCount; i++)
        {
            var road = urban[rnd.Next(urban.Length)];
            var t = 0.15 + rnd.NextDouble() * 0.7;
            var a = road.Coordinates[0];
            var b = road.Coordinates[^1];
            var onRoad = new GeoPoint(a.Lat + t * (b.Lat - a.Lat), a.Lon + t * (b.Lon - a.Lon));
            // Inside the 100 m street grid every point is within ~50 m of a road, so "far" stops
            // (snapping warnings) are placed 160–250 m south of the city's southern edge.
            var p = i < spec.FarStops
                ? new GeoPoint(city.GridBounds.SouthLat - (160 + rnd.NextDouble() * 90) / MetresPerDegLat,
                    city.GridBounds.WestLon + (0.3 + 0.4 * rnd.NextDouble()) * (city.GridBounds.EastLon - city.GridBounds.WestLon))
                : Offset(onRoad, 8 + rnd.NextDouble() * 40, rnd.NextDouble() * 2 * Math.PI);

            var start = (10.5 + rnd.NextDouble() * 2.0) * 3600; // 10:30–12:30
            var width = (45 + rnd.Next(0, 4) * 15) * 60.0;     // 45–90 min
            stops.Add(MakeStop(rnd, i, p, city, start, start + width, spec.OptionalShare));
        }
        for (var i = 0; i < spec.RemoteStops; i++)
        {
            var v = city.Villages[i % city.Villages.Count];
            var p = Offset(v, 10 + rnd.NextDouble() * 30, rnd.NextDouble() * 2 * Math.PI);
            stops.Add(MakeStop(rnd, urbanCount + i, p, city, 10 * 3600, 17 * 3600, 0) with { Mandatory = true });
        }

        var vehicles = new List<Vehicle>();
        for (var v = 0; v < spec.Vehicles; v++)
        {
            var longDay = spec.RemoteStops > 0 && v == 0;
            vehicles.Add(new Vehicle(
                $"V{v + 1:00}", $"{Surnames[(v * 7 + 3) % Surnames.Length]}{(v % 2 == 0 ? "志明" : "淑芬")}",
                city.Depot, city.Depot,
                longDay ? 9 * 3600 : 10 * 3600,
                longDay ? 18 * 3600 : 14.5 * 3600,
                longDay ? 20 : 26, longDay ? 8 : 10,
                longDay ? 9 * 3600 : 4.5 * 3600));
        }
        return (stops.ToArray(), vehicles.ToArray());
    }

    private static Stop MakeStop(Random rnd, int i, GeoPoint p, SyntheticCity city, double a, double b, double optionalShare)
    {
        var name = Surnames[rnd.Next(Surnames.Length)] + Titles[rnd.Next(Titles.Length)];
        var mandatory = rnd.NextDouble() >= optionalShare;
        return new Stop(
            $"S{i + 1:000}", name, p,
            $"{city.StreetNames[rnd.Next(city.StreetNames.Count)]} {rnd.Next(1, 300)} 號",
            Notes[rnd.Next(Notes.Length)],
            rnd.NextDouble() < 0.8 ? 1 : 2,
            rnd.NextDouble() < 0.3 ? 1 : 0,
            (2 + rnd.Next(0, 4)) * 60.0,
            a, b, mandatory, mandatory ? 3 : rnd.Next(1, 6));
    }

    private static GeoPoint Offset(GeoPoint p, double metres, double bearing)
    {
        var dLat = metres * Math.Cos(bearing) / MetresPerDegLat;
        var dLon = metres * Math.Sin(bearing) / (MetresPerDegLat * Math.Cos(p.Lat * Math.PI / 180));
        return new GeoPoint(p.Lat + dLat, p.Lon + dLon);
    }
}
