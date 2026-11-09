using MealRoute.Core.GeoJson;
using MealRoute.Core.Network;

namespace MealRoute.Tests.GeoJson;

public class GeoJsonRoadReaderTests
{
    internal static string Fc(params string[] features) =>
        "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(",", features) + "]}";

    internal static string Line(string props, string coords) =>
        "{\"type\":\"Feature\",\"properties\":{" + props + "},\"geometry\":{\"type\":\"LineString\",\"coordinates\":" + coords + "}}";

    private const string Coords2 = "[[121.5,25.0],[121.501,25.0]]";

    [Theory]
    [InlineData("\"oneway\":true", OnewayDirection.Forward)]
    [InlineData("\"oneway\":\"yes\"", OnewayDirection.Forward)]
    [InlineData("\"oneway\":\"-1\"", OnewayDirection.Backward)]
    [InlineData("\"oneway\":false", OnewayDirection.Both)]
    [InlineData("\"oneway\":\"no\"", OnewayDirection.Both)]
    [InlineData("\"oneway\":null", OnewayDirection.Both)]
    [InlineData("\"oneway\":1", OnewayDirection.Both)]
    [InlineData("\"x\":1", OnewayDirection.Both)]
    public void Oneway_values_are_interpreted_per_spec(string props, OnewayDirection expected)
    {
        var r = GeoJsonRoadReader.Read(Fc(Line("\"id\":\"a\"," + props, Coords2)));
        Assert.Equal(expected, Assert.Single(r.Features).Oneway);
    }

    [Theory]
    [InlineData("\"class\":\"arterial\"", RoadClass.Arterial)]
    [InlineData("\"class\":\"collector\"", RoadClass.Collector)]
    [InlineData("\"class\":\"residential\"", RoadClass.Residential)]
    [InlineData("\"x\":1", RoadClass.Residential)]
    public void Road_class_is_parsed_and_defaults_to_residential(string props, RoadClass expected)
    {
        var r = GeoJsonRoadReader.Read(Fc(Line("\"id\":\"a\"," + props, Coords2)));
        Assert.Equal(expected, Assert.Single(r.Features).Class);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Unknown_road_class_warns_and_falls_back_to_residential()
    {
        var r = GeoJsonRoadReader.Read(Fc(Line("\"id\":\"a\",\"class\":\"motorway\"", Coords2)));
        Assert.Equal(RoadClass.Residential, Assert.Single(r.Features).Class);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("motorway", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_can_be_numeric_or_come_from_feature_level()
    {
        var r = GeoJsonRoadReader.Read(Fc(
            Line("\"id\":42", Coords2),
            "{\"type\":\"Feature\",\"id\":\"top\",\"properties\":{},\"geometry\":{\"type\":\"LineString\",\"coordinates\":" + Coords2 + "}}"));
        Assert.Equal("42", r.Features[0].Id);
        Assert.Equal("top", r.Features[1].Id);
    }

    [Fact]
    public void Coordinates_are_read_as_lon_lat()
    {
        var f = Assert.Single(GeoJsonRoadReader.Read(Fc(Line("\"id\":\"a\"", Coords2))).Features);
        Assert.Equal(25.0, f.Coordinates[0].Lat);
        Assert.Equal(121.5, f.Coordinates[0].Lon);
    }

    [Theory]
    [InlineData("{\"type\":\"Point\",\"coordinates\":[121.5,25.0]}", "LineString")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[121.5,25.0]]}", "至少需要 2 個座標")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[121.5,25.0],[\"x\",25.0]]}", "座標")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[121.5,25.0],[121.5]]}", "座標")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[121.5,95.0],[121.5,25.0]]}", "緯度")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[190.0,25.0],[121.5,25.0]]}", "經度")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[121.5,25.0],[121.5,25.0]]}", "長度為 0")]
    [InlineData("null", "geometry")]
    [InlineData("{\"type\":\"LineString\"}", "coordinates")]
    public void Invalid_geometry_is_reported_with_feature_index_and_id_and_skipped(string geometry, string expectedFragment)
    {
        var json = Fc(
            Line("\"id\":\"ok\"", Coords2),
            "{\"type\":\"Feature\",\"properties\":{\"id\":\"R-305\"},\"geometry\":" + geometry + "}");
        var r = GeoJsonRoadReader.Read(json);

        Assert.Single(r.Features);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(1, issue.FeatureIndex);
        Assert.Equal("R-305", issue.FeatureId);
        Assert.Contains(expectedFragment, issue.Message, StringComparison.Ordinal);
        Assert.StartsWith("Feature #1 (id=R-305): ", issue.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_throws_format_exception_with_position()
    {
        var ex = Assert.Throws<GeoJsonFormatException>(() => GeoJsonRoadReader.Read("{\"type\":\"FeatureCollection\",\n\"features\":[ {,]}"));
        Assert.Contains("行", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"type\":\"Feature\"}")]
    [InlineData("{\"type\":\"FeatureCollection\"}")]
    public void Non_feature_collection_throws(string json)
        => Assert.Throws<GeoJsonFormatException>(() => GeoJsonRoadReader.Read(json));

    [Fact]
    public void Read_from_stream_equals_read_from_string()
    {
        var json = Fc(Line("\"id\":\"a\",\"oneway\":\"yes\"", Coords2));
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var a = GeoJsonRoadReader.Read(ms);
        var b = GeoJsonRoadReader.Read(json);
        Assert.Equal(b.Features[0].Id, a.Features[0].Id);
        Assert.Equal(b.Features[0].Oneway, a.Features[0].Oneway);
    }
}
