using CommunityToolkit.Mvvm.ComponentModel;
using MealRoute.Core.Editing;
using MealRoute.Core.Evaluation;

namespace MealRoute.App.ViewModels;

public enum SegmentKind { Drive, Wait, Service, Break }

public sealed record TimelineSegment(SegmentKind Kind, double Start, double End, int? Stop);

public sealed record TimelineRow(int Vehicle, string Label, uint Color, IReadOnlyList<TimelineSegment> Segments, bool IsLocked, bool IsFeasible, string? Problem);

/// <summary>One Gantt row per vehicle: driving, waiting, service and breaks (exact times).</summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    private readonly SelectionService _selection;

    public TimelineViewModel(SelectionService selection)
    {
        _selection = selection;
        _selection.PropertyChanged += (_, _) => Version++;
    }

    public SelectionService Selection => _selection;
    public IReadOnlyList<TimelineRow> Rows { get; private set; } = Array.Empty<TimelineRow>();

    [ObservableProperty] private double _startTime = 9 * 3600;
    [ObservableProperty] private double _endTime = 15 * 3600;
    [ObservableProperty] private int _version;

    public void Refresh(PlanEditor? editor)
    {
        if (editor is null)
        {
            Rows = Array.Empty<TimelineRow>();
            Version++;
            return;
        }
        var inst = editor.Instance;
        var rows = new List<TimelineRow>();
        for (var v = 0; v < inst.Vehicles.Count; v++)
        {
            var e = editor.CurrentEvaluation(v);
            var veh = inst.Vehicles[v];
            rows.Add(new TimelineRow(v, $"{veh.DriverName}（{veh.Id}）", VehiclePalette.ColorOf(v), Segments(editor, e),
                editor.IsRouteLocked(v), e.IsFeasible, e.FirstViolation?.Message));
        }
        Rows = rows;
        var used = rows.SelectMany(r => r.Segments).ToList();
        StartTime = used.Count == 0 ? inst.Vehicles.DefaultIfEmpty().Min(x => x?.EarliestStart ?? 9 * 3600) : Math.Floor((used.Min(s => s.Start) - 900) / 1800) * 1800;
        EndTime = used.Count == 0 ? StartTime + 6 * 3600 : Math.Ceiling((used.Max(s => s.End) + 900) / 1800) * 1800;
        Version++;
    }

    private static List<TimelineSegment> Segments(PlanEditor editor, RouteEvaluation e)
    {
        var list = new List<TimelineSegment>();
        if (e.IsEmpty) return list;
        var inst = editor.Instance;
        var prev = e.Departure;
        foreach (var v in e.Visits)
        {
            if (double.IsNaN(v.Arrival)) break;
            list.Add(new TimelineSegment(SegmentKind.Drive, prev, v.Arrival, v.Stop));
            if (v.ServiceStart > v.Arrival) list.Add(new TimelineSegment(SegmentKind.Wait, v.Arrival, v.ServiceStart, v.Stop));
            var serviceEnd = v.ServiceStart + inst.Stops[v.Stop].ServiceSeconds;
            list.Add(new TimelineSegment(SegmentKind.Service, v.ServiceStart, serviceEnd, v.Stop));
            if (v.BreakAfter) list.Add(new TimelineSegment(SegmentKind.Break, serviceEnd, v.Departure, v.Stop));
            prev = v.Departure;
        }
        if (!double.IsInfinity(e.EndArrival)) list.Add(new TimelineSegment(SegmentKind.Drive, prev, e.EndArrival, null));
        return list;
    }

    /// <summary>Stop whose wait/service/break segment (or the drive to it) contains the time on that row.</summary>
    public (int Vehicle, int? Stop)? HitTest(int row, double time)
    {
        if (row < 0 || row >= Rows.Count) return null;
        var r = Rows[row];
        var seg = r.Segments.FirstOrDefault(s => s.Kind != SegmentKind.Drive && time >= s.Start && time <= s.End)
                  ?? r.Segments.FirstOrDefault(s => time >= s.Start && time <= s.End);
        return (r.Vehicle, seg?.Stop);
    }

    public void Click(int row, double time)
    {
        var hit = HitTest(row, time);
        if (hit is null) _selection.Clear();
        else if (hit.Value.Stop is { } stop) _selection.SelectStop(stop, hit.Value.Vehicle);
        else _selection.SelectVehicle(hit.Value.Vehicle);
    }

    /// <summary>Insert position for a drop at <paramref name="time"/> on a row (before the first stop served later).</summary>
    public int InsertionIndex(int row, double time)
    {
        var services = Rows[row].Segments.Where(s => s.Kind == SegmentKind.Service).ToList();
        var index = services.FindIndex(s => (s.Start + s.End) / 2 > time);
        return index < 0 ? services.Count : index;
    }
}
