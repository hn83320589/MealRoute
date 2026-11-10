using System.Text;
using MealRoute.Core.Evaluation;
using MealRoute.Core.Model;

namespace MealRoute.Core.Reports;

public sealed record RouteSheetRow(
    int Sequence, string StopId, string Name, string Address, string Note,
    double Arrival, double ServiceStart, double Departure, double WindowStart, double WindowEnd,
    int Meals, int Boxes, bool BreakAfter);

/// <summary>One driver's printable route sheet. Times come straight from the solution (exact oracle).</summary>
public sealed record RouteSheet(
    int Vehicle, string VehicleId, string Driver, double Departure, double EndArrival,
    IReadOnlyList<RouteSheetRow> Rows, int TotalMeals, int TotalBoxes, double TravelSeconds, double WorkSeconds)
{
    /// <summary>Plain-text rendering (also used for printing fallbacks and tests).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"路線單　{Driver}（{VehicleId}）");
        sb.AppendLine($"出發 {TimeOfDay.Format(Departure)}　返回 {TimeOfDay.Format(EndArrival)}　餐盒 {TotalMeals}　保溫箱 {TotalBoxes}　行車 {TimeOfDay.FormatDuration(TravelSeconds)}　工時 {TimeOfDay.FormatDuration(WorkSeconds)}");
        sb.AppendLine("站序｜姓名｜預計到達｜時間窗｜餐盒/保溫箱｜地址｜備註");
        foreach (var r in Rows)
        {
            sb.Append($"{r.Sequence}｜{r.Name}｜{TimeOfDay.Format(r.Arrival)}｜{TimeOfDay.Format(r.WindowStart)}–{TimeOfDay.Format(r.WindowEnd)}｜{r.Meals}/{r.Boxes}｜{r.Address}｜{r.Note}");
            if (r.BreakAfter) sb.Append("｜服務後休息 30 分鐘");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public static class RouteSheetBuilder
{
    public static IReadOnlyList<RouteSheet> Build(ProblemInstance instance, Solution solution)
    {
        var sheets = new List<RouteSheet>();
        foreach (var r in solution.Routes)
        {
            if (r.IsEmpty) continue;
            var veh = instance.Vehicles[r.Vehicle];
            var rows = r.Visits.Select((v, i) =>
            {
                var s = instance.Stops[v.Stop];
                return new RouteSheetRow(i + 1, s.Id, s.Name, s.Address, s.Note, v.Arrival, v.ServiceStart, v.Departure,
                    s.WindowStart, s.WindowEnd, s.Meals, s.Boxes, v.BreakAfter);
            }).ToArray();
            double travel = 0;
            for (var i = 0; i <= r.Visits.Count; i++)
            {
                var from = i == 0 ? r.Departure : r.Visits[i - 1].Departure;
                var to = i == r.Visits.Count ? r.EndArrival : r.Visits[i].Arrival;
                travel += to - from;
            }
            sheets.Add(new RouteSheet(r.Vehicle, veh.Id, veh.DriverName, r.Departure, r.EndArrival, rows,
                rows.Sum(x => x.Meals), rows.Sum(x => x.Boxes), travel, r.EndArrival - r.Departure));
        }
        return sheets;
    }
}
