using System.Text.Json;
using System.Text.Json.Nodes;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;

namespace MealRoute.Core.Reports;

/// <summary>
/// Route export as GeoJSON: a LineString per route (along the driven path when a geometry
/// provider is given, else straight segments), a Point per visit and per unserved stop.
/// <see cref="Read"/> restores the same solution from the properties.
/// </summary>
public static class RouteGeoJson
{
    /// <param name="routeGeometry">Path coordinates of a route; null = straight lines depot → stops → depot.</param>
    public static string Write(ProblemInstance instance, Solution solution, Func<RoutePlan, IReadOnlyList<GeoPoint>>? routeGeometry = null)
    {
        var features = new JsonArray();
        foreach (var r in solution.Routes)
        {
            if (r.IsEmpty) continue;
            var veh = instance.Vehicles[r.Vehicle];
            var line = routeGeometry?.Invoke(r) ?? StraightLine(instance, r);
            features.Add(Feature("LineString", new JsonArray(line.Select(Position).ToArray()), new JsonObject
            {
                ["kind"] = "route",
                ["vehicleId"] = veh.Id,
                ["driver"] = veh.DriverName,
                ["departure"] = r.Departure,
                ["endArrival"] = r.EndArrival,
                ["stops"] = new JsonArray(r.Stops.Select(s => (JsonNode)JsonValue.Create(instance.Stops[s].Id)!).ToArray()),
            }));
            for (var i = 0; i < r.Visits.Count; i++)
            {
                var v = r.Visits[i];
                var s = instance.Stops[v.Stop];
                features.Add(Feature("Point", Position(s.Location), new JsonObject
                {
                    ["kind"] = "stop",
                    ["stopId"] = s.Id,
                    ["name"] = s.Name,
                    ["vehicleId"] = veh.Id,
                    ["seq"] = i + 1,
                    ["arrival"] = v.Arrival,
                    ["serviceStart"] = v.ServiceStart,
                    ["departure"] = v.Departure,
                    ["breakAfter"] = v.BreakAfter,
                }));
            }
        }
        foreach (var u in solution.Unserved)
        {
            var s = instance.Stops[u.Stop];
            features.Add(Feature("Point", Position(s.Location), new JsonObject
            {
                ["kind"] = "unserved", ["stopId"] = s.Id, ["name"] = s.Name, ["reason"] = u.Reason,
            }));
        }
        var root = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static GeoPoint[] StraightLine(ProblemInstance inst, RoutePlan r) =>
        new[] { inst.Vehicles[r.Vehicle].StartDepot }
            .Concat(r.Stops.Select(s => inst.Stops[s].Location))
            .Append(inst.Vehicles[r.Vehicle].EndDepot).ToArray();

    private static JsonNode Position(GeoPoint p) => new JsonArray(p.Lon, p.Lat);

    private static JsonObject Feature(string type, JsonNode coordinates, JsonObject properties) => new()
    {
        ["type"] = "Feature",
        ["geometry"] = new JsonObject { ["type"] = type, ["coordinates"] = coordinates },
        ["properties"] = properties,
    };

    public static Solution Read(string json, ProblemInstance instance)
    {
        JsonNode root;
        try { root = JsonNode.Parse(json) ?? throw new FormatException("GeoJSON 是空的。"); }
        catch (JsonException ex) { throw new FormatException($"GeoJSON 格式錯誤：{ex.Message}", ex); }
        var vehIndex = instance.Vehicles.Select((v, i) => (v.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        int Stop(string id) => instance.StopIndex.TryGetValue(id, out var i) ? i : throw new FormatException($"GeoJSON 的站點「{id}」不在名單中。");
        int Veh(string id) => vehIndex.TryGetValue(id, out var i) ? i : throw new FormatException($"GeoJSON 的車輛「{id}」不存在。");

        var routes = Enumerable.Range(0, instance.Vehicles.Count).Select(RoutePlan.Empty).ToArray();
        var visits = new Dictionary<(int Vehicle, int Seq), StopVisit>();
        var unserved = new List<UnservedStop>();
        foreach (var f in root["features"]?.AsArray() ?? throw new FormatException("GeoJSON 缺少 features。"))
        {
            var p = f?["properties"] ?? throw new FormatException("Feature 缺少 properties。");
            switch ((string?)p["kind"])
            {
                case "route":
                {
                    var v = Veh((string)p["vehicleId"]!);
                    var stops = p["stops"]!.AsArray().Select(x => Stop((string)x!)).ToArray();
                    routes[v] = new RoutePlan(v, stops, (double)p["departure"]!, Array.Empty<StopVisit>(), (double)p["endArrival"]!);
                    break;
                }
                case "stop":
                    visits[(Veh((string)p["vehicleId"]!), (int)p["seq"]!)] = new StopVisit(Stop((string)p["stopId"]!),
                        (double)p["arrival"]!, (double)p["serviceStart"]!, (double)p["departure"]!, (bool)p["breakAfter"]!);
                    break;
                case "unserved":
                    unserved.Add(new UnservedStop(Stop((string)p["stopId"]!), (string?)p["reason"] ?? ""));
                    break;
            }
        }
        for (var v = 0; v < routes.Length; v++)
        {
            if (routes[v].IsEmpty) continue;
            var vs = Enumerable.Range(1, routes[v].Stops.Count).Select(seq =>
                visits.TryGetValue((v, seq), out var x) ? x : throw new FormatException($"GeoJSON 缺少車輛 {instance.Vehicles[v].Id} 第 {seq} 站的資料。")).ToArray();
            routes[v] = routes[v] with { Visits = vs };
        }
        return new Solution(routes, unserved);
    }
}
