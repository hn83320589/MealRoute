using System.Globalization;
using System.Text;
using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;

namespace MealRoute.Core.Reports;

/// <summary>
/// Route export as CSV (UTF-8 with BOM for Excel). Readable time columns plus raw seconds
/// (round-trip precision) so that <see cref="Read"/> restores the same solution.
/// Unserved stops are rows without a vehicle, with the reason.
/// </summary>
public static class RouteCsv
{
    private static readonly string[] Header =
    {
        "vehicle_id", "driver", "seq", "stop_id", "stop_name", "address", "note",
        "arrival", "window", "meals", "boxes", "break_after",
        "arrival_s", "service_start_s", "departure_s", "route_departure_s", "route_end_s", "unserved_reason",
    };

    public static void WriteFile(string path, ProblemInstance instance, Solution solution) =>
        File.WriteAllText(path, Write(instance, solution), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static string Write(ProblemInstance instance, Solution solution)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header));
        static string N(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        foreach (var r in solution.Routes)
        {
            if (r.IsEmpty) continue;
            var veh = instance.Vehicles[r.Vehicle];
            for (var i = 0; i < r.Visits.Count; i++)
            {
                var v = r.Visits[i];
                var s = instance.Stops[v.Stop];
                Row(sb, T(veh.Id), T(veh.DriverName), (i + 1).ToString(CultureInfo.InvariantCulture), T(s.Id), T(s.Name), T(s.Address), T(s.Note),
                    TimeOfDay.Format(v.Arrival), $"{TimeOfDay.Format(s.WindowStart)}-{TimeOfDay.Format(s.WindowEnd)}",
                    s.Meals.ToString(CultureInfo.InvariantCulture), s.Boxes.ToString(CultureInfo.InvariantCulture), v.BreakAfter ? "1" : "0",
                    N(v.Arrival), N(v.ServiceStart), N(v.Departure), N(r.Departure), N(r.EndArrival), "");
            }
        }
        foreach (var u in solution.Unserved)
        {
            var s = instance.Stops[u.Stop];
            Row(sb, "", "", "", T(s.Id), T(s.Name), T(s.Address), T(s.Note), "", $"{TimeOfDay.Format(s.WindowStart)}-{TimeOfDay.Format(s.WindowEnd)}",
                s.Meals.ToString(CultureInfo.InvariantCulture), s.Boxes.ToString(CultureInfo.InvariantCulture), "", "", "", "", "", "", T(u.Reason));
        }
        return sb.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> NeedsQuoting = System.Buffers.SearchValues.Create(",\"\n\r");

    private const string FormulaTriggers = "=+-@\t\r'";

    /// <summary>
    /// Text cells that a spreadsheet would treat as a formula (or that already start with the
    /// escape quote) get a leading apostrophe; <see cref="Untext"/> removes it again on import.
    /// </summary>
    private static string T(string text) =>
        text.Length > 0 && FormulaTriggers.Contains(text[0], StringComparison.Ordinal) ? "'" + text : text;

    private static string Untext(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && FormulaTriggers.Contains(cell[1], StringComparison.Ordinal) ? cell[1..] : cell;

    private static void Row(StringBuilder sb, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var c = cells[i];
            if (c.AsSpan().IndexOfAny(NeedsQuoting) >= 0) sb.Append('"').Append(c.Replace("\"", "\"\"")).Append('"');
            else sb.Append(c);
        }
        sb.Append("\r\n");
    }

    public static Solution ReadFile(string path, ProblemInstance instance) => Read(File.ReadAllText(path, Encoding.UTF8), instance);

    public static Solution Read(string csv, ProblemInstance instance)
    {
        var rows = Parse(csv.TrimStart('﻿'));
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header)) throw new FormatException("CSV 標頭不是 MealRoute 路線匯出格式。");
        var vehIndex = instance.Vehicles.Select((v, i) => (v.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        int Col(string name) => Array.IndexOf(Header, name);
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        var visits = Enumerable.Range(0, instance.Vehicles.Count).Select(_ => new List<StopVisit>()).ToArray();
        var departure = new double[instance.Vehicles.Count];
        var end = new double[instance.Vehicles.Count];
        var unserved = new List<UnservedStop>();
        for (var line = 1; line < rows.Count; line++)
        {
            var r = rows[line];
            if (r.Length != Header.Length) throw new FormatException($"CSV 第 {line + 1} 列欄位數為 {r.Length}，應為 {Header.Length}。");
            var stopId = Untext(r[Col("stop_id")]);
            var vehicleId = Untext(r[Col("vehicle_id")]);
            if (!instance.StopIndex.TryGetValue(stopId, out var stop)) throw new FormatException($"CSV 第 {line + 1} 列的站點「{stopId}」不在名單中。");
            if (vehicleId.Length == 0)
            {
                unserved.Add(new UnservedStop(stop, Untext(r[Col("unserved_reason")])));
                continue;
            }
            if (!vehIndex.TryGetValue(vehicleId, out var v)) throw new FormatException($"CSV 第 {line + 1} 列的車輛「{vehicleId}」不存在。");
            visits[v].Add(new StopVisit(stop, D(r[Col("arrival_s")]), D(r[Col("service_start_s")]), D(r[Col("departure_s")]), r[Col("break_after")] == "1"));
            departure[v] = D(r[Col("route_departure_s")]);
            end[v] = D(r[Col("route_end_s")]);
        }
        var routes = Enumerable.Range(0, instance.Vehicles.Count).Select(v => visits[v].Count == 0
            ? RoutePlan.Empty(v)
            : new RoutePlan(v, visits[v].Select(x => x.Stop).ToArray(), departure[v], visits[v].ToArray(), end[v])).ToArray();
        return new Solution(routes, unserved);
    }

    /// <summary>RFC 4180 parser (quoted fields may contain commas, quotes and newlines).</summary>
    private static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row.ToArray()); row.Clear();
                    break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
