using MealRoute.Core.Model;
using MealRoute.Core.Network;
using MealRoute.Core.Planning;

namespace MealRoute.Tests.Planning;

/// <summary>Snapping warnings name the stop or depot (as the app does), not the oracle location index.</summary>
public class LocationLabelTests
{
    private static ProblemInstance Instance() => new(
        [
            new Stop("s1", "王奶奶", default, "", "", 1, 0, 0, 36_000, 39_600, true, 1),
            new Stop("s2", "李爺爺", default, "", "", 1, 0, 0, 36_000, 39_600, false, 1),
        ],
        [
            new Vehicle("V01", "甲", default, default, 34_000, 50_000, 6, 3, 14_400),
            new Vehicle("V02", "乙", default, default, 34_000, 50_000, 6, 3, 14_400),
        ],
        BreakRule.Default);

    [Fact]
    public void Stop_locations_are_described_by_name_and_id()
    {
        var inst = Instance();
        Assert.Equal("站點「李爺爺」（s2）", inst.DescribeLocation(inst.StopLocation(1)));
    }

    [Fact]
    public void Start_and_end_depots_are_described_by_vehicle()
    {
        var inst = Instance();
        Assert.Equal("車輛 V02 的據點", inst.DescribeLocation(inst.VehicleStartLocation(1)));
        Assert.Equal("車輛 V01 的據點", inst.DescribeLocation(inst.VehicleEndLocation(0)));
    }

    [Fact]
    public void Snap_warning_maps_back_to_the_location_index_used_by_the_model()
    {
        Assert.Equal(0, TravelModel.LocationOf(new SnapWarning("L0", "距離道路 900 m")));
        Assert.Equal(13, TravelModel.LocationOf(new SnapWarning("L13", "距離道路 900 m")));
    }
}
