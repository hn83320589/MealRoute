using System.Globalization;
using MealRoute.Core.Network;

namespace MealRoute.Core.Travel;

/// <summary>
/// Per road class, 96 speeds (km/h) for the 15-minute slots of a day; slot k covers
/// [900k, 900(k+1)) seconds and the profile repeats every day.
/// Traversal follows the spec: drive at the current slot's speed and switch at slot
/// boundaries. This model is FIFO: a later entry can never exit earlier.
/// </summary>
public sealed class SpeedProfile
{
    public const int SlotCount = 96;
    public const double SlotSeconds = 900.0;

    private readonly double[][] _kmh;
    private readonly double[][] _mps;
    private readonly bool[] _isConstant;

    public SpeedProfile(double[] arterialKmh, double[] collectorKmh, double[] residentialKmh)
    {
        _kmh = new[] { Validate(arterialKmh, RoadClass.Arterial), Validate(collectorKmh, RoadClass.Collector), Validate(residentialKmh, RoadClass.Residential) };
        _mps = _kmh.Select(a => a.Select(v => v / 3.6).ToArray()).ToArray();
        _isConstant = _mps.Select(a => a.All(v => v == a[0])).ToArray();
        MaxSpeedMps = _mps.Max(a => a.Max());
    }

    /// <summary>Highest speed of any class and slot (m/s); used for admissible A* bounds.</summary>
    public double MaxSpeedMps { get; }

    public double SpeedKmh(RoadClass roadClass, int slot) => _kmh[(int)roadClass][slot];

    /// <summary>Copy of the 96 speeds (km/h) of a class.</summary>
    public double[] SpeedsKmh(RoadClass roadClass) => (double[])_kmh[(int)roadClass].Clone();

    /// <summary>Arrival time after driving <paramref name="lengthM"/> on a road of the class, entering at <paramref name="t"/>.</summary>
    public double ArrivalTime(RoadClass roadClass, double lengthM, double t)
    {
        if (lengthM <= 0) return t;
        var c = (int)roadClass;
        var speeds = _mps[c];
        if (_isConstant[c]) return t + lengthM / speeds[0];

        var remaining = lengthM;
        var k = (long)Math.Floor(t / SlotSeconds);
        while (true)
        {
            var boundary = (k + 1) * SlotSeconds;
            var available = boundary - t;
            var v = speeds[Slot(k)];
            if (available > 0)
            {
                var reachable = v * available;
                if (reachable >= remaining) return t + remaining / v;
                remaining -= reachable;
            }
            t = boundary;
            k++;
        }
    }

    private static int Slot(long k)
    {
        var s = (int)(k % SlotCount);
        return s < 0 ? s + SlotCount : s;
    }

    private static double[] Validate(double[] speeds, RoadClass roadClass)
    {
        ArgumentNullException.ThrowIfNull(speeds);
        if (speeds.Length != SlotCount)
            throw new ArgumentException($"道路等級 {roadClass} 的速度表需有 {SlotCount} 個時段，實際為 {speeds.Length} 個。", nameof(speeds));
        for (var k = 0; k < speeds.Length; k++)
        {
            if (!double.IsFinite(speeds[k]) || speeds[k] <= 0)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"道路等級 {roadClass} 第 {k} 時段的速度 {speeds[k]} 無效，必須為大於 0 的有限數值。"), nameof(speeds));
        }
        return (double[])speeds.Clone();
    }

    /// <summary>
    /// Built-in urban profile: free-flow at night, morning (07:00–09:00), lunch (11:30–13:00)
    /// and evening (17:00–19:00) slowdowns.
    /// </summary>
    public static SpeedProfile Default { get; } = CreateDefault();

    private static SpeedProfile CreateDefault()
    {
        static double[] Make(double free, double day, double lunch, double peak)
        {
            var a = new double[SlotCount];
            for (var k = 0; k < SlotCount; k++)
            {
                var hour = k / 4.0;
                a[k] = hour switch
                {
                    < 6 or >= 22 => free,
                    >= 7 and < 9 => peak,
                    >= 11.5 and < 13 => lunch,
                    >= 17 and < 19 => peak,
                    _ => day,
                };
            }
            return a;
        }
        return new SpeedProfile(Make(60, 45, 32, 22), Make(45, 35, 26, 18), Make(30, 25, 20, 15));
    }
}
