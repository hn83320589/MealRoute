using System.Globalization;
using System.Text;
using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;

namespace MealRoute.Core.Synthetic;

/// <param name="GridSize">Grid of GridSize × GridSize intersections (≈ node count = GridSize²).</param>
/// <param name="OneWayShare">Share of residential/collector streets that are one-way.</param>
/// <param name="MissingShare">Share of non-arterial street segments left out (irregular blocks).</param>
/// <param name="RuralRoadKm">Length of a slow road to outlying villages (0 = none).</param>
public sealed record CitySpec(int GridSize, int Seed = 1, double OneWayShare = 0.15, double MissingShare = 0.08, double RuralRoadKm = 0);

/// <summary>Synthetic city as GeoJSON-ready road features (deterministic for a seed).</summary>
public sealed class SyntheticCity
{
    public required IReadOnlyList<RoadFeature> Roads { get; init; }
    public required GeoPoint Depot { get; init; }
    /// <summary>Points in outlying villages (empty without a rural road).</summary>
    public required IReadOnlyList<GeoPoint> Villages { get; init; }
    public required IReadOnlyList<string> StreetNames { get; init; }
    /// <summary>Southern edge latitude and west/east longitudes of the street grid.</summary>
    public required (double SouthLat, double WestLon, double EastLon) GridBounds { get; init; }

    public string ToGeoJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"FeatureCollection\",\"features\":[\n");
        for (var i = 0; i < Roads.Count; i++)
        {
            var r = Roads[i];
            if (i > 0) sb.Append(",\n");
            sb.Append("{\"type\":\"Feature\",\"properties\":{\"id\":\"").Append(r.Id).Append("\",\"class\":\"")
              .Append(r.Class.ToString().ToLowerInvariant()).Append('"');
            if (r.Oneway == OnewayDirection.Forward) sb.Append(",\"oneway\":\"yes\"");
            else if (r.Oneway == OnewayDirection.Backward) sb.Append(",\"oneway\":\"-1\"");
            sb.Append("},\"geometry\":{\"type\":\"LineString\",\"coordinates\":[");
            for (var k = 0; k < r.Coordinates.Length; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('[').Append(r.Coordinates[k].Lon.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Coordinates[k].Lat.ToString("R", CultureInfo.InvariantCulture)).Append(']');
            }
            sb.Append("]}}");
        }
        sb.Append("\n]}\n");
        return sb.ToString();
    }
}

public static class CityGenerator
{
    private const double Lat0 = 25.0, Lon0 = 121.45, DLat = 0.0009, DLon = 0.001; // ≈ 100 m blocks

    private static readonly string[] StreetNameStock =
    {
        "中山路", "中正路", "民生路", "民權路", "和平路", "信義路", "仁愛路", "復興路", "光明街", "成功路",
        "文化路", "建國路", "公園路", "自強街", "忠孝路", "三民路", "福德街", "永安街", "長安路", "新生路",
    };

    public static SyntheticCity Generate(CitySpec spec)
    {
        var rnd = new Random(spec.Seed);
        var n = spec.GridSize;
        var pos = new GeoPoint[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var jitter = i % 5 == 0 || j % 5 == 0 ? 0 : 0.00012;
                pos[i, j] = new GeoPoint(Lat0 + i * DLat + (rnd.NextDouble() - 0.5) * jitter, Lon0 + j * DLon + (rnd.NextDouble() - 0.5) * jitter);
            }

        var roads = new List<RoadFeature>();
        void Add(string id, GeoPoint[] pts, RoadClass cls, OnewayDirection ow) =>
            roads.Add(new RoadFeature(roads.Count, id, pts, ow, cls));

        RoadClass ClassOf(int line) => line % 10 == 0 ? RoadClass.Arterial : line % 5 == 0 ? RoadClass.Collector : RoadClass.Residential;

        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                foreach (var (di, dj, line) in new[] { (0, 1, i), (1, 0, j) })
                {
                    int i2 = i + di, j2 = j + dj;
                    if (i2 >= n || j2 >= n) continue;
                    var cls = ClassOf(line);
                    if (cls != RoadClass.Arterial && rnd.NextDouble() < spec.MissingShare) continue;
                    var a = pos[i, j];
                    var b = pos[i2, j2];
                    var pts = cls == RoadClass.Residential && rnd.NextDouble() < 0.2
                        ? new[] { a, new GeoPoint((a.Lat + b.Lat) / 2 + (rnd.NextDouble() - 0.5) * 0.0002, (a.Lon + b.Lon) / 2 + (rnd.NextDouble() - 0.5) * 0.0002), b }
                        : new[] { a, b };
                    var ow = cls != RoadClass.Arterial && rnd.NextDouble() < spec.OneWayShare
                        ? rnd.NextDouble() < 0.5 ? OnewayDirection.Forward : OnewayDirection.Backward
                        : OnewayDirection.Both;
                    Add($"{(di == 0 ? "h" : "v")}-{i}-{j}", pts, cls, ow);
                }
            }

        // A one-way dead end out of the city (its far end is a separate SCC) and a small island.
        var corner = pos[n - 1, n - 1];
        Add("trap", new[] { corner, new GeoPoint(corner.Lat + 0.0012, corner.Lon + 0.0012) }, RoadClass.Residential, OnewayDirection.Forward);
        var isl = new GeoPoint(Lat0 - 0.004, Lon0 - 0.004);
        var islB = new GeoPoint(isl.Lat, isl.Lon + 0.001);
        var islC = new GeoPoint(isl.Lat + 0.0009, isl.Lon + 0.001);
        Add("island-1", new[] { isl, islB }, RoadClass.Residential, OnewayDirection.Both);
        Add("island-2", new[] { islB, islC }, RoadClass.Residential, OnewayDirection.Both);

        // Rural road east from the middle of the city with small village loops along it.
        var villages = new List<GeoPoint>();
        if (spec.RuralRoadKm > 0)
        {
            var start = pos[n / 2, n - 1];
            var stepKm = 2.5;
            var steps = (int)Math.Ceiling(spec.RuralRoadKm / stepKm);
            var prev = start;
            for (var k = 1; k <= steps; k++)
            {
                // ≈ 2.5 km east with a slight wiggle; 1 deg lon at 25° ≈ 100.9 km
                var next = new GeoPoint(start.Lat + Math.Sin(k * 0.7) * 0.004, start.Lon + k * stepKm / 100.9);
                var mid = new GeoPoint((prev.Lat + next.Lat) / 2 + 0.001, (prev.Lon + next.Lon) / 2);
                Add($"rural-{k}", new[] { prev, mid, next }, RoadClass.Residential, OnewayDirection.Both);
                if (k % 4 == 0 || k == steps)
                {
                    var a = new GeoPoint(next.Lat + 0.0009, next.Lon);
                    var b = new GeoPoint(next.Lat + 0.0009, next.Lon + 0.001);
                    var c = new GeoPoint(next.Lat, next.Lon + 0.001);
                    Add($"village-{k}-a", new[] { next, a }, RoadClass.Residential, OnewayDirection.Both);
                    Add($"village-{k}-b", new[] { a, b }, RoadClass.Residential, OnewayDirection.Both);
                    Add($"village-{k}-c", new[] { b, c }, RoadClass.Residential, OnewayDirection.Both);
                    Add($"village-{k}-d", new[] { c, next }, RoadClass.Residential, OnewayDirection.Both);
                    villages.Add(b);
                }
                prev = next;
            }
        }

        return new SyntheticCity
        {
            Roads = roads,
            Depot = pos[n / 2, n / 2],
            Villages = villages,
            StreetNames = StreetNameStock,
            GridBounds = (Lat0, Lon0, Lon0 + (n - 1) * DLon),
        };
    }
}
