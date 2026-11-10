namespace MealRoute.Core.Evaluation;

public enum ViolationKind
{
    MealCapacity,
    BoxCapacity,
    Unreachable,
    TimeWindow,
    DrivingLimit,
    LatestReturn,
    MaxWork,
}

/// <param name="Stop">Stop index, or -1 for route-level (depot) violations.</param>
/// <param name="Time">When the violation happens (used to find the first one).</param>
/// <param name="Message">Human readable, e.g. "王奶奶 12:10 到，時間窗 11:30–12:00".</param>
public sealed record Violation(ViolationKind Kind, int Stop, double Time, string Message);

public static class TimeOfDay
{
    /// <summary>HH:mm, or HH:mm:ss when there are leftover seconds; hours may exceed 24.</summary>
    public static string Format(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "--:--";
        var total = (long)Math.Floor(seconds + 1e-6);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return s == 0 ? $"{h:00}:{m:00}" : $"{h:00}:{m:00}:{s:00}";
    }

    /// <summary>Duration as H:mm.</summary>
    public static string FormatDuration(double seconds)
    {
        var total = (long)Math.Round(seconds / 60);
        return $"{total / 60}:{total % 60:00}";
    }
}
