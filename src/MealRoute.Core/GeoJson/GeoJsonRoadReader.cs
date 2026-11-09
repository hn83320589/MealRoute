using System.Globalization;
using System.Text;
using System.Text.Json;
using MealRoute.Core.Geo;
using MealRoute.Core.Network;

namespace MealRoute.Core.GeoJson;

/// <summary>
/// Reads a GeoJSON FeatureCollection of LineString roads (properties: id, oneway, class).
/// Document-level problems throw <see cref="GeoJsonFormatException"/>; per-feature problems
/// are reported as <see cref="ImportIssue"/> and the feature is skipped (Error) or defaulted (Warning).
/// </summary>
public static class GeoJsonRoadReader
{
    public static GeoJsonReadResult ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static GeoJsonReadResult Read(Stream utf8Json)
    {
        using var ms = new MemoryStream();
        utf8Json.CopyTo(ms);
        return Read(ms.ToArray());
    }

    public static GeoJsonReadResult Read(string json) => Read(Encoding.UTF8.GetBytes(json));

    private static GeoJsonReadResult Read(byte[] utf8)
    {
        JsonDocument doc;
        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            doc = JsonDocument.Parse(SkipBom(utf8), options);
        }
        catch (JsonException ex)
        {
            var line = (ex.LineNumber ?? 0) + 1;
            var col = (ex.BytePositionInLine ?? 0) + 1;
            throw new GeoJsonFormatException($"GeoJSON 格式錯誤：第 {line} 行第 {col} 個位元組附近無法解析 JSON。", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "FeatureCollection")
            {
                throw new GeoJsonFormatException("GeoJSON 根物件必須是 \"type\": \"FeatureCollection\"。");
            }
            if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                throw new GeoJsonFormatException("FeatureCollection 缺少 \"features\" 陣列。");

            var result = new List<RoadFeature>();
            var issues = new List<ImportIssue>();
            var index = 0;
            foreach (var feature in features.EnumerateArray())
            {
                ReadFeature(feature, index, result, issues);
                index++;
            }
            return new GeoJsonReadResult(result, issues);
        }
    }

    private static ReadOnlyMemory<byte> SkipBom(byte[] utf8) =>
        utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF
            ? utf8.AsMemory(3)
            : utf8.AsMemory();

    private static void ReadFeature(JsonElement feature, int index, List<RoadFeature> result, List<ImportIssue> issues)
    {
        if (feature.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ImportIssue(IssueSeverity.Error, index, null, "Feature 必須是 JSON 物件。"));
            return;
        }

        var props = feature.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var id = ReadId(props) ?? ReadId(feature) ?? $"#{index}";

        void Error(string message) => issues.Add(new ImportIssue(IssueSeverity.Error, index, id, message));

        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            Error("缺少 geometry 或 geometry 為 null。");
            return;
        }
        if (!geometry.TryGetProperty("type", out var gType) || gType.ValueKind != JsonValueKind.String || gType.GetString() != "LineString")
        {
            var actual = gType.ValueKind == JsonValueKind.String ? gType.GetString() : "(缺少)";
            Error($"geometry 類型必須是 LineString，實際為 {actual}。");
            return;
        }
        if (!geometry.TryGetProperty("coordinates", out var coords) || coords.ValueKind != JsonValueKind.Array)
        {
            Error("LineString 缺少 coordinates 陣列。");
            return;
        }

        var points = new List<GeoPoint>(coords.GetArrayLength());
        var i = 0;
        foreach (var pos in coords.EnumerateArray())
        {
            if (!TryReadPosition(pos, out var lon, out var lat))
            {
                Error($"第 {i} 個座標無效：必須是至少含經度、緯度兩個數值的陣列。");
                return;
            }
            if (lat is < -90 or > 90)
            {
                Error($"第 {i} 個座標的緯度 {lat.ToString(CultureInfo.InvariantCulture)} 超出 [-90, 90]。");
                return;
            }
            if (lon is < -180 or > 180)
            {
                Error($"第 {i} 個座標的經度 {lon.ToString(CultureInfo.InvariantCulture)} 超出 [-180, 180]。");
                return;
            }
            points.Add(new GeoPoint(Normalize(lat), Normalize(lon)));
            i++;
        }

        if (points.Count < 2)
        {
            Error($"LineString 至少需要 2 個座標，實際為 {points.Count} 個。");
            return;
        }

        var arr = points.ToArray();
        if (Haversine.PolylineLength(arr) <= 0)
        {
            Error("LineString 長度為 0（所有座標相同）。");
            return;
        }

        var roadClass = ReadClass(props, index, id, issues);
        result.Add(new RoadFeature(index, id, arr, ReadOneway(props), roadClass));
    }

    /// <summary>Collapse -0.0 to 0.0 so endpoint merging by exact equality is well defined.</summary>
    private static double Normalize(double v) => v == 0 ? 0.0 : v;

    private static bool TryReadPosition(JsonElement pos, out double lon, out double lat)
    {
        lon = lat = 0;
        if (pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() < 2) return false;
        var x = pos[0];
        var y = pos[1];
        if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number) return false;
        lon = x.GetDouble();
        lat = y.GetDouble();
        return double.IsFinite(lon) && double.IsFinite(lat);
    }

    private static string? ReadId(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    private static OnewayDirection ReadOneway(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty("oneway", out var v)) return OnewayDirection.Both;
        return v.ValueKind switch
        {
            JsonValueKind.True => OnewayDirection.Forward,
            JsonValueKind.String when v.GetString() == "yes" => OnewayDirection.Forward,
            JsonValueKind.String when v.GetString() == "-1" => OnewayDirection.Backward,
            _ => OnewayDirection.Both,
        };
    }

    private static RoadClass ReadClass(JsonElement props, int index, string id, List<ImportIssue> issues)
    {
        if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty("class", out var v) || v.ValueKind == JsonValueKind.Null)
            return RoadClass.Residential;

        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        switch (s)
        {
            case "arterial": return RoadClass.Arterial;
            case "collector": return RoadClass.Collector;
            case "residential": return RoadClass.Residential;
            default:
                issues.Add(new ImportIssue(IssueSeverity.Warning, index, id, $"未知的道路等級 class=\"{s}\"，視為 residential。"));
                return RoadClass.Residential;
        }
    }
}
