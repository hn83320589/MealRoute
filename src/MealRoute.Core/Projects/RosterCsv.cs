using System.Globalization;
using System.Text;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Reports;

namespace MealRoute.Core.Projects;

/// <summary>
/// Daily roster import/export as CSV (UTF-8, BOM optional). Times are HH:mm (or HH:mm:ss).
/// Stops: id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority
/// Vehicles: id,driver,start_lat,start_lon,end_lat,end_lon,earliest_start,latest_end,meal_capacity,box_capacity,max_work_hours
/// </summary>
public static class RosterCsv
{
    public static readonly string[] StopHeader =
        { "id", "name", "lat", "lon", "address", "note", "meals", "boxes", "service_min", "window_start", "window_end", "mandatory", "priority" };

    public static readonly string[] VehicleHeader =
        { "id", "driver", "start_lat", "start_lon", "end_lat", "end_lon", "earliest_start", "latest_end", "meal_capacity", "box_capacity", "max_work_hours" };

    public static IReadOnlyList<Stop> ReadStops(string csv)
    {
        var rows = Rows(csv, StopHeader, "站點");
        var result = new List<Stop>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2;
            var priority = Int(r[12], line, "priority");
            if (priority is < 1 or > 5) throw new FormatException($"站點名單第 {line} 行：priority 必須介於 1 到 5。");
            var start = Time(r[9], line, "window_start");
            var end = Time(r[10], line, "window_end");
            if (end < start) throw new FormatException($"站點名單第 {line} 行：時間窗結束早於開始。");
            result.Add(new Stop(U(r[0]), U(r[1]), new GeoPoint(Num(r[2], line, "lat"), Num(r[3], line, "lon")), U(r[4]), U(r[5]),
                Int(r[6], line, "meals"), Int(r[7], line, "boxes"), Num(r[8], line, "service_min") * 60, start, end,
                Bool(r[11], line, "mandatory"), priority));
        }
        if (result.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new FormatException("站點名單中有重複的 id。");
        return result;
    }

    public static IReadOnlyList<Vehicle> ReadVehicles(string csv)
    {
        var rows = Rows(csv, VehicleHeader, "車輛");
        return rows.Select((r, i) =>
        {
            var line = i + 2;
            return new Vehicle(U(r[0]), U(r[1]), new GeoPoint(Num(r[2], line, "start_lat"), Num(r[3], line, "start_lon")),
                new GeoPoint(Num(r[4], line, "end_lat"), Num(r[5], line, "end_lon")),
                Time(r[6], line, "earliest_start"), Time(r[7], line, "latest_end"),
                Int(r[8], line, "meal_capacity"), Int(r[9], line, "box_capacity"), Num(r[10], line, "max_work_hours") * 3600);
        }).ToArray();
    }

    public static string WriteStops(IEnumerable<Stop> stops)
    {
        var sb = new StringBuilder().AppendLine(string.Join(",", StopHeader));
        foreach (var s in stops)
            sb.AppendLine(string.Join(",", Q(s.Id), Q(s.Name), F(s.Location.Lat), F(s.Location.Lon), Q(s.Address), Q(s.Note),
                s.Meals, s.Boxes, F(s.ServiceSeconds / 60), Hm(s.WindowStart), Hm(s.WindowEnd), s.Mandatory ? "1" : "0", s.Priority));
        return sb.ToString();
    }

    public static string WriteVehicles(IEnumerable<Vehicle> vehicles)
    {
        var sb = new StringBuilder().AppendLine(string.Join(",", VehicleHeader));
        foreach (var v in vehicles)
            sb.AppendLine(string.Join(",", Q(v.Id), Q(v.DriverName), F(v.StartDepot.Lat), F(v.StartDepot.Lon), F(v.EndDepot.Lat), F(v.EndDepot.Lon),
                Hm(v.EarliestStart), Hm(v.LatestEnd), v.MealCapacity, v.BoxCapacity, F(v.MaxWorkSeconds / 3600)));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Hm(double seconds)
    {
        var t = (long)Math.Round(seconds);
        return t % 60 == 0 ? $"{t / 3600:00}:{t % 3600 / 60:00}" : $"{t / 3600:00}:{t % 3600 / 60:00}:{t % 60:00}";
    }

    private static readonly System.Buffers.SearchValues<char> NeedsQuoting = System.Buffers.SearchValues.Create(",\"\n\r");

    /// <summary>Quoted, spreadsheet-safe text cell (see <see cref="CsvText"/>).</summary>
    private static string Q(string text)
    {
        var s = CsvText.Escape(text);
        return s.AsSpan().IndexOfAny(NeedsQuoting) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    private static string U(string cell) => CsvText.Unescape(cell);

    private static List<string[]> Rows(string csv, string[] header, string what)
    {
        var all = Parse(csv.TrimStart('﻿'));
        if (all.Count == 0) throw new FormatException($"{what}名單是空的。");
        var h = all[0].Select(x => x.Trim().ToLowerInvariant()).ToArray();
        if (!h.SequenceEqual(header)) throw new FormatException($"{what}名單標頭應為：{string.Join(",", header)}");
        var rows = all.Skip(1).Where(r => !(r.Length == 1 && r[0].Length == 0)).ToList();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Length != header.Length) throw new FormatException($"{what}名單第 {i + 2} 行有 {rows[i].Length} 欄，應為 {header.Length} 欄。");
        return rows;
    }

    private static double Num(string s, int line, string col) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d : throw new FormatException($"第 {line} 行欄位 {col} 不是數值：「{s}」");

    private static int Int(string s, int line, string col) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0
            ? i : throw new FormatException($"第 {line} 行欄位 {col} 必須是非負整數：「{s}」");

    private static bool Bool(string s, int line, string col) => s.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "是" or "必送" => true,
        "0" or "false" or "no" or "否" or "選送" => false,
        _ => throw new FormatException($"第 {line} 行欄位 {col} 必須是 1/0：「{s}」"),
    };

    private static double Time(string s, int line, string col)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var m = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var sec = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            if (h is >= 0 and <= 47 && m is >= 0 and < 60 && sec is >= 0 and < 60) return h * 3600 + m * 60 + sec;
        }
        throw new FormatException($"第 {line} 行欄位 {col} 應為 HH:mm 時刻：「{s}」");
    }

    private static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
