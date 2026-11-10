using CommunityToolkit.Mvvm.ComponentModel;
using MealRoute.Core.Geo;
using MealRoute.Core.Network;

namespace MealRoute.App.ViewModels;

public enum StopMarkerKind { Served, Unserved, SnapWarning }

/// <summary>Flat world-coordinate polyline [x0, y0, x1, y1, …] (y grows downwards like the screen).</summary>
public sealed record MapPolyline(double[] Xy, RoadClass Class, bool IsSmallComponent);

public sealed record MapStopMarker(int Stop, double X, double Y, string Label, StopMarkerKind Kind, int? Vehicle, bool IsLocked, bool IsMandatory);

public sealed record MapRoute(int Vehicle, uint Color, double[] Xy, bool IsLocked);

public sealed record MapDepot(double X, double Y, string Label);

/// <summary>
/// Map state for Win2D drawing: layers in world coordinates (metres on an equirectangular plane
/// around the city centre; raw coordinates in static mode), the viewport and hit testing.
/// </summary>
public sealed partial class MapViewModel : ObservableObject
{
    private const double MetresPerDegree = Haversine.EarthRadiusM * Math.PI / 180.0;
    private readonly SelectionService _selection;
    private PlanningWorkspace? _ws;
    private double _lat0, _lon0, _kx = 1, _ky = 1;
    private bool _geographic;

    public MapViewModel(SelectionService selection)
    {
        _selection = selection;
        _selection.PropertyChanged += (_, _) => Version++;
    }

    public SelectionService Selection => _selection;
    public IReadOnlyList<MapPolyline> Roads { get; private set; } = Array.Empty<MapPolyline>();
    public IReadOnlyList<MapStopMarker> Stops { get; private set; } = Array.Empty<MapStopMarker>();
    public IReadOnlyList<MapRoute> Routes { get; private set; } = Array.Empty<MapRoute>();
    public IReadOnlyList<MapDepot> Depots { get; private set; } = Array.Empty<MapDepot>();

    /// <summary>Incremented whenever anything visible changes (the view redraws).</summary>
    [ObservableProperty]
    private int _version;

    /// <summary>Incremented only when the road layer changes (the view rebuilds cached geometry).</summary>
    [ObservableProperty]
    private int _roadVersion;

    [ObservableProperty] private double _scale = 1;       // pixels per world unit
    [ObservableProperty] private double _centerX;         // world point at the view centre
    [ObservableProperty] private double _centerY;
    [ObservableProperty] private double _viewWidth = 800;
    [ObservableProperty] private double _viewHeight = 600;

    // ------------------------------------------------------------------ loading

    public void Load(PlanningWorkspace ws)
    {
        _ws = ws;
        _geographic = !ws.IsStaticMode;
        var pts = new List<GeoPoint>();
        if (ws.Network is { } net) for (var v = 0; v < net.NodeCount; v++) pts.Add(net.NodePosition(v));
        pts.AddRange(ws.Instance.LocationPoints());
        if (pts.Count == 0) pts.Add(new GeoPoint(25.03, 121.55));
        _lat0 = (pts.Min(p => p.Lat) + pts.Max(p => p.Lat)) / 2;
        _lon0 = (pts.Min(p => p.Lon) + pts.Max(p => p.Lon)) / 2;
        _kx = _geographic ? MetresPerDegree * Math.Cos(_lat0 * Math.PI / 180) : 1;
        _ky = _geographic ? MetresPerDegree : 1;

        Roads = BuildRoads(ws);
        RoadVersion++;
        Depots = ws.Instance.Vehicles.Select(v => v.StartDepot).Distinct()
            .Select(p => { var (x, y) = GeoToWorld(p); return new MapDepot(x, y, "據點"); }).ToArray();
        RefreshPlan();
        FitAll();
    }

    private MapPolyline[] BuildRoads(PlanningWorkspace ws)
    {
        if (ws.Network is not { } net || ws.Analysis is not { } analysis) return Array.Empty<MapPolyline>();
        var small = new bool[net.Features.Count];
        for (var e = 0; e < net.EdgeCount; e++)
            if (!analysis.IsEdgeInLargestComponent(net, e)) small[net.EdgeFeature(e)] = true;
        return net.Features.Select((f, i) =>
        {
            var xy = new double[f.Coordinates.Length * 2];
            for (var k = 0; k < f.Coordinates.Length; k++) (xy[2 * k], xy[2 * k + 1]) = GeoToWorld(f.Coordinates[k]);
            return new MapPolyline(xy, f.Class, small[i]);
        }).ToArray();
    }

    /// <summary>Rebuilds stops and routes from the current plan.</summary>
    public void RefreshPlan()
    {
        if (_ws is null) return;
        var inst = _ws.Instance;
        var editor = _ws.Editor;
        var vehicleOf = new int?[inst.Stops.Count];
        var routes = new List<MapRoute>();
        if (editor is not null)
        {
            var sol = editor.ToSolution();
            foreach (var r in sol.Routes)
            {
                foreach (var s in r.Stops) vehicleOf[s] = r.Vehicle;
                if (r.IsEmpty) continue;
                var line = _ws.RouteGeometry(r);
                var xy = new double[line.Count * 2];
                for (var k = 0; k < line.Count; k++) (xy[2 * k], xy[2 * k + 1]) = GeoToWorld(line[k]);
                routes.Add(new MapRoute(r.Vehicle, VehiclePalette.ColorOf(r.Vehicle), xy, editor.IsRouteLocked(r.Vehicle)));
            }
        }
        var warned = _ws.Warnings.Where(w => w.StopIndex is not null).Select(w => w.StopIndex!.Value).ToHashSet();
        Stops = inst.Stops.Select((s, i) =>
        {
            var (x, y) = GeoToWorld(s.Location);
            var kind = warned.Contains(i) ? StopMarkerKind.SnapWarning : vehicleOf[i] is null ? StopMarkerKind.Unserved : StopMarkerKind.Served;
            return new MapStopMarker(i, x, y, s.Name, kind, vehicleOf[i], editor?.IsStopLocked(i) ?? false, s.Mandatory);
        }).ToArray();
        Routes = routes;
        Version++;
    }

    // ------------------------------------------------------------------ coordinates

    public (double X, double Y) GeoToWorld(GeoPoint p) => ((p.Lon - _lon0) * _kx, -(p.Lat - _lat0) * _ky);

    public GeoPoint WorldToGeo(double x, double y) => new(_lat0 - y / _ky, _lon0 + x / _kx);

    public (double X, double Y) WorldToScreen(double x, double y) =>
        ((x - CenterX) * Scale + ViewWidth / 2, (y - CenterY) * Scale + ViewHeight / 2);

    public (double X, double Y) ScreenToWorld(double sx, double sy) =>
        ((sx - ViewWidth / 2) / Scale + CenterX, (sy - ViewHeight / 2) / Scale + CenterY);

    // ------------------------------------------------------------------ viewport

    public void Resize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        ViewWidth = width;
        ViewHeight = height;
        Version++;
    }

    /// <summary>Zooms keeping the world point under (sx, sy) fixed on screen.</summary>
    public void ZoomAt(double sx, double sy, double factor)
    {
        var (wx, wy) = ScreenToWorld(sx, sy);
        Scale = Math.Clamp(Scale * factor, 1e-6, 1e6);
        CenterX = wx - (sx - ViewWidth / 2) / Scale;
        CenterY = wy - (sy - ViewHeight / 2) / Scale;
        Version++;
    }

    public void PanBy(double dxPixels, double dyPixels)
    {
        CenterX -= dxPixels / Scale;
        CenterY -= dyPixels / Scale;
        Version++;
    }

    public void FitAll()
    {
        // Fit the stops and depots (the planning area); fall back to the roads when there are none.
        var xs = Stops.Select(s => s.X).Concat(Depots.Select(d => d.X)).ToList();
        var ys = Stops.Select(s => s.Y).Concat(Depots.Select(d => d.Y)).ToList();
        if (xs.Count == 0)
        {
            xs = Roads.SelectMany(r => r.Xy.Where((_, i) => i % 2 == 0)).ToList();
            ys = Roads.SelectMany(r => r.Xy.Where((_, i) => i % 2 == 1)).ToList();
        }
        if (xs.Count == 0) return;
        double minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max();
        CenterX = (minX + maxX) / 2;
        CenterY = (minY + maxY) / 2;
        var w = Math.Max(maxX - minX, 1e-9);
        var h = Math.Max(maxY - minY, 1e-9);
        Scale = 0.9 * Math.Min(ViewWidth / w, ViewHeight / h);
        Version++;
    }

    // ------------------------------------------------------------------ interaction

    /// <summary>Nearest stop marker within <paramref name="radiusPixels"/> of a screen point.</summary>
    public int? HitTestStop(double sx, double sy, double radiusPixels = 10)
    {
        int? best = null;
        var bestD = radiusPixels * radiusPixels;
        foreach (var s in Stops)
        {
            var (x, y) = WorldToScreen(s.X, s.Y);
            var d = (x - sx) * (x - sx) + (y - sy) * (y - sy);
            if (d <= bestD) { bestD = d; best = s.Stop; }
        }
        return best;
    }

    /// <summary>Click on the map: selects the stop under the cursor (and its vehicle), else clears.</summary>
    public void Click(double sx, double sy)
    {
        var hit = HitTestStop(sx, sy);
        if (hit is { } stop) _selection.SelectStop(stop, Stops[stop].Vehicle);
        else _selection.Clear();
    }
}
