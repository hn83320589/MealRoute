using MealRoute.Core.Network;
using MealRoute.Core.Travel;
using MealRoute.Tests.Infrastructure;
using MealRoute.Tests.Reference;

namespace MealRoute.Tests.Travel;

public class SpeedModelTests
{
    internal static double[] Flat(double kmh) => Enumerable.Repeat(kmh, 96).ToArray();

    internal static SpeedProfile RandomProfile(Random r, out double[][] raw)
    {
        raw = new double[3][];
        for (var c = 0; c < 3; c++)
        {
            raw[c] = new double[96];
            for (var k = 0; k < 96; k++) raw[c][k] = 5 + r.NextDouble() * 55;
        }
        return new SpeedProfile(raw[0], raw[1], raw[2]);
    }

    [Fact]
    public void Constant_speed_traversal_is_length_over_speed()
    {
        var p = new SpeedProfile(Flat(36), Flat(36), Flat(36)); // 10 m/s
        Assert.Equal(1000 + 50.0, p.ArrivalTime(RoadClass.Arterial, 500, 1000), 9);
    }

    [Fact]
    public void Crossing_a_slot_boundary_switches_to_the_new_speed()
    {
        var s = Flat(36);
        s[41] = 18; // 5 m/s from 10:15 (36900 s)
        var p = new SpeedProfile(s, Flat(30), Flat(30));
        // 10 s at 10 m/s = 100 m, remaining 100 m at 5 m/s = 20 s
        Assert.Equal(36900 + 20.0, p.ArrivalTime(RoadClass.Arterial, 200, 36890), 9);
    }

    [Fact]
    public void Starting_exactly_on_a_boundary_uses_the_new_slot()
    {
        var s = Flat(36);
        s[41] = 18;
        var p = new SpeedProfile(s, Flat(30), Flat(30));
        Assert.Equal(36900 + 20.0, p.ArrivalTime(RoadClass.Arterial, 100, 36900), 9);
    }

    [Fact]
    public void Traversal_can_span_several_slots()
    {
        var s = Flat(3.6); // 1 m/s
        s[2] = 7.2;        // 2 m/s in [1800, 2700)
        var p = new SpeedProfile(s, Flat(30), Flat(30));
        // start 1700: 100 m by 1800, 1800 m by 2700, then 100 m more at 1 m/s
        Assert.Equal(2800.0, p.ArrivalTime(RoadClass.Arterial, 2000, 1700), 9);
    }

    [Fact]
    public void Profile_is_cyclic_after_midnight()
    {
        var s = Flat(36);
        s[0] = 18;
        var p = new SpeedProfile(s, Flat(30), Flat(30));
        // 86390: 10 s at 10 m/s (slot 95), then slot 0 of next day at 5 m/s
        Assert.Equal(86400 + 20.0, p.ArrivalTime(RoadClass.Arterial, 200, 86390), 9);
    }

    [Fact]
    public void Zero_length_takes_no_time()
        => Assert.Equal(123.0, SpeedProfile.Default.ArrivalTime(RoadClass.Residential, 0, 123));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_positive_or_non_finite_speeds_are_rejected(double bad)
    {
        var s = Flat(30);
        s[17] = bad;
        var ex = Assert.Throws<ArgumentException>(() => new SpeedProfile(s, Flat(30), Flat(30)));
        Assert.Contains("17", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_slot_count_is_rejected()
        => Assert.Throws<ArgumentException>(() => new SpeedProfile(new double[95], Flat(30), Flat(30)));

    [Fact]
    public void Max_speed_is_the_global_maximum()
    {
        var s = Flat(30);
        s[50] = 72;
        Assert.Equal(20.0, new SpeedProfile(Flat(10), s, Flat(10)).MaxSpeedMps, 12);
    }

    [Fact]
    public void Property_edge_traversal_matches_reference_integration()
    {
        Property.ForAll(10_000, 500, (r, _) =>
        {
            var p = RandomProfile(r, out var raw);
            var cls = r.Next(3);
            var len = r.NextDouble() < 0.1 ? r.NextDouble() * 50_000 : r.NextDouble() * 2_000;
            var t = r.NextDouble() < 0.2 ? 900 * r.Next(0, 100) : r.NextDouble() * 90_000;
            Assert.Equal(RefTdIntegrator.Arrival(raw, cls, len, t), p.ArrivalTime((RoadClass)cls, len, t), 1e-6);
        });
    }

    [Fact]
    public void Property_any_path_is_fifo()
    {
        Property.ForAll(11_000, 300, (r, _) =>
        {
            var p = RandomProfile(r, out var unusedRaw);
            var path = Enumerable.Range(0, r.Next(1, 30)).Select(_ => ((RoadClass)r.Next(3), r.NextDouble() * 3000)).ToArray();
            double Arrive(double t) { foreach (var (c, l) in path) t = p.ArrivalTime(c, l, t); return t; }

            var t1 = r.NextDouble() * 86_400;
            var t2 = t1 + (r.NextDouble() < 0.3 ? r.NextDouble() * 1e-6 : r.NextDouble() * 3_600);
            Assert.True(Arrive(t1) <= Arrive(t2) + 1e-9, $"FIFO violated: {t1} -> {Arrive(t1)}, {t2} -> {Arrive(t2)}");
        });
    }
}
