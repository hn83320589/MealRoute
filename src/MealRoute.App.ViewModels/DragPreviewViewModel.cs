using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MealRoute.Core.Editing;
using MealRoute.Core.Evaluation;

namespace MealRoute.App.ViewModels;

/// <summary>
/// Drag feedback: while a stop is dragged over a route/position the move is previewed (fast L1
/// pass, then the exact L2 pass for the two affected routes) and the result — feasible or the
/// first violated condition, plus metric changes — is exposed for the view.
/// </summary>
public sealed partial class DragPreviewViewModel : ObservableObject
{
    private readonly Func<PlanEditor?> _editor;
    private readonly Action _applied;
    private Move? _move;

    public DragPreviewViewModel(Func<PlanEditor?> editor, Action applied)
    {
        _editor = editor;
        _applied = applied;
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int? _stop;
    [ObservableProperty] private int? _targetVehicle;
    [ObservableProperty] private int _targetPosition;
    [ObservableProperty] private bool _isAllowed;
    [ObservableProperty] private bool _isFeasible;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _deltaText = "";
    [ObservableProperty] private double _latencyMs;
    [ObservableProperty] private Violation? _firstViolation;

    public void Begin(int stop)
    {
        Stop = stop;
        IsActive = true;
        _move = null;
        Message = "";
        DeltaText = "";
    }

    /// <summary>Previews dropping the dragged stop on <paramref name="vehicle"/> (null = take it off all routes).</summary>
    public MovePreview? UpdateTarget(int? vehicle, int position)
    {
        if (!IsActive || Stop is not { } stop || _editor() is not { } editor) return null;
        var sw = Stopwatch.StartNew();
        TargetVehicle = vehicle;
        TargetPosition = position;
        _move = new MoveStop(stop, vehicle ?? -1, position);

        var fast = editor.Preview(_move);
        var exact = fast.IsAllowed ? editor.PreviewExact(_move) : fast;
        sw.Stop();
        LatencyMs = sw.Elapsed.TotalMilliseconds;

        IsAllowed = exact.IsAllowed;
        IsFeasible = exact.IsAllowed && exact.Feasible;
        FirstViolation = exact.FirstViolation;
        Message = !exact.IsAllowed ? exact.RejectReason ?? "不允許的移動"
            : exact.Feasible ? "可行"
            : exact.FirstViolation?.Message ?? "不可行";
        DeltaText = exact.IsAllowed ? Delta(exact.Before, exact.After) : "";
        return exact;
    }

    /// <summary>Applies the previewed move (infeasible only when <paramref name="force"/>).</summary>
    public bool Commit(bool force = false)
    {
        if (_move is null || _editor() is not { } editor) { Cancel(); return false; }
        var result = editor.Apply(_move, allowInfeasible: force);
        Cancel();
        if (result.Applied) _applied();
        return result.Applied;
    }

    public void Cancel()
    {
        IsActive = false;
        Stop = null;
        TargetVehicle = null;
        _move = null;
    }

    public static string Delta(ObjectiveValue before, ObjectiveValue after)
    {
        static string Min(double seconds) => $"{(seconds >= 0 ? "+" : "−")}{Math.Abs(seconds) / 60:F0} 分";
        var parts = new List<string>();
        var dm = after.UnservedMandatory - before.UnservedMandatory;
        if (dm != 0) parts.Add($"未送必送站 {dm:+0;−0}");
        var dp = after.UnservedOptionalPriority - before.UnservedOptionalPriority;
        if (dp != 0) parts.Add($"未送選送優先權 {dp:+0;−0}");
        parts.Add($"車輛 {after.VehiclesUsed - before.VehiclesUsed:+0;−0;±0}");
        parts.Add($"行車 {Min(after.TravelSeconds - before.TravelSeconds)}");
        parts.Add($"工時 {Min(after.WorkSeconds - before.WorkSeconds)}");
        return string.Join("｜", parts);
    }
}
