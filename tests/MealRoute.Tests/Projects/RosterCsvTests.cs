using MealRoute.Core.Projects;
using MealRoute.Tests.Benchmark;

namespace MealRoute.Tests.Projects;

public class RosterCsvTests
{
    [Fact]
    public void Stops_and_vehicles_round_trip_through_csv()
    {
        var project = ProjectSerializer.Load(SolomonTests.RepoFile("samples", "small", "project.mealroute.json"));
        var stops = RosterCsv.ReadStops(RosterCsv.WriteStops(project.Stops));
        var vehicles = RosterCsv.ReadVehicles(RosterCsv.WriteVehicles(project.Vehicles));
        Assert.Equal(project.Stops.Count, stops.Count);
        for (var i = 0; i < stops.Count; i++)
        {
            var a = project.Stops[i];
            var b = stops[i];
            Assert.Equal(a with { ServiceSeconds = 0, WindowStart = 0, WindowEnd = 0 }, b with { ServiceSeconds = 0, WindowStart = 0, WindowEnd = 0 });
            Assert.Equal(a.ServiceSeconds, b.ServiceSeconds, 6);
            Assert.Equal(Math.Round(a.WindowStart), b.WindowStart);
            Assert.Equal(Math.Round(a.WindowEnd), b.WindowEnd);
        }
        Assert.Equal(project.Vehicles, vehicles);
    }

    [Fact]
    public void Stop_csv_with_bom_quotes_and_chinese_values_is_read()
    {
        const string csv = "﻿id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\n" +
                           "A1,\"王奶奶, 二樓\",25.01,121.5,中山路 1 號,\"請敲門\"\"兩下\"\"\",1,1,3,11:30,12:00,必送,3\r\n" +
                           "A2,李爺爺,25.02,121.51,,,2,0,2.5,11:00:30,13:00,0,5\r\n";
        var stops = RosterCsv.ReadStops(csv);
        Assert.Equal("王奶奶, 二樓", stops[0].Name);
        Assert.Equal("請敲門\"兩下\"", stops[0].Note);
        Assert.Equal(11.5 * 3600, stops[0].WindowStart);
        Assert.Equal(180, stops[0].ServiceSeconds);
        Assert.True(stops[0].Mandatory);
        Assert.False(stops[1].Mandatory);
        Assert.Equal(11 * 3600 + 30, stops[1].WindowStart);
    }

    [Theory]
    [InlineData("id,name\r\nA,B\r\n", "標頭")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,x,1,,,1,0,2,11:00,12:00,1,3\r\n", "lat")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,1,1,,,1,0,2,25:99,12:00,1,3\r\n", "HH:mm")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,1,1,,,1,0,2,12:00,11:00,1,3\r\n", "時間窗")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,1,1,,,1,0,2,11:00,12:00,1,9\r\n", "priority")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,1,1,,,1,0,2,11:00,12:00,1,3\r\nA,C,1,1,,,1,0,2,11:00,12:00,1,3\r\n", "重複")]
    [InlineData("id,name,lat,lon,address,note,meals,boxes,service_min,window_start,window_end,mandatory,priority\r\nA,B,1,1\r\n", "欄")]
    public void Invalid_stop_csv_reports_the_line_and_problem(string csv, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => RosterCsv.ReadStops(csv));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }
}
