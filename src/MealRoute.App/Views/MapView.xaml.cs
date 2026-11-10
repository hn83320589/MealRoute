using System.Numerics;
using MealRoute.App.ViewModels;
using MealRoute.Core.Network;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MealRoute.App.Views;

/// <summary>
/// Win2D map. Roads are pre-built into per-tile, per-class geometries (rebuilt only when the
/// network changes), drawn with a world→screen transform and pixel-fixed stroke widths; tiles
/// outside the view are skipped and residential streets are hidden when zoomed far out.
/// </summary>
public sealed partial class MapView : UserControl
{
    private const double TileSize = 1000; // world units (metres)
    private MainViewModel? _vm;
    private readonly Dictionary<(int Tx, int Ty, RoadClass Class), CanvasGeometry> _tiles = new();
    private CanvasGeometry? _smallComponents;
    private int _builtRoadVersion = -1;
    private readonly CanvasStrokeStyle _fixed = new() { TransformBehavior = CanvasStrokeTransformBehavior.Fixed, LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    private readonly CanvasStrokeStyle _dashed = new() { DashStyle = CanvasDashStyle.Dash, LineJoin = CanvasLineJoin.Round };

    private Point _pressPoint, _lastPoint;
    private bool _pressed, _panning, _dragging;
    private int? _pressedStop;
    private Point _cursor;

    public MapView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && _dragging) { _vm?.Drag.Cancel(); _dragging = false; UpdateBadge(); Canvas.Invalidate(); }
        };
    }

    /// <summary>When true the next click picks the vehicle position for rescheduling.</summary>
    public bool PickingPosition { get; set; }

    public event EventHandler? PositionPicked;

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        vm.Map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.Version) or nameof(MapViewModel.RoadVersion)) Canvas.Invalidate();
        };
    }

    // ------------------------------------------------------------------ drawing

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_vm is null) return;
        var map = _vm.Map;
        var ds = args.DrawingSession;
        if (_builtRoadVersion != map.RoadVersion) BuildRoadGeometry(sender, map);

        var world = Matrix3x2.CreateTranslation((float)-map.CenterX, (float)-map.CenterY)
                    * Matrix3x2.CreateScale((float)map.Scale)
                    * Matrix3x2.CreateTranslation((float)(map.ViewWidth / 2), (float)(map.ViewHeight / 2));

        // Roads (world space, pixel-fixed widths)
        ds.Transform = world;
        var (x0, y0) = map.ScreenToWorld(0, 0);
        var (x1, y1) = map.ScreenToWorld(map.ViewWidth, map.ViewHeight);
        int tx0 = (int)Math.Floor(x0 / TileSize) - 1, tx1 = (int)Math.Floor(x1 / TileSize) + 1;
        int ty0 = (int)Math.Floor(y0 / TileSize) - 1, ty1 = (int)Math.Floor(y1 / TileSize) + 1;
        var showResidential = map.Scale > 0.08; // > 0.08 px per metre
        foreach (var cls in new[] { RoadClass.Residential, RoadClass.Collector, RoadClass.Arterial })
        {
            if (cls == RoadClass.Residential && !showResidential) continue;
            var (color, width) = cls switch
            {
                RoadClass.Arterial => (Color.FromArgb(255, 160, 168, 180), 3f),
                RoadClass.Collector => (Color.FromArgb(255, 185, 192, 202), 2f),
                _ => (Color.FromArgb(255, 214, 218, 224), 1f),
            };
            for (var tx = tx0; tx <= tx1; tx++)
                for (var ty = ty0; ty <= ty1; ty++)
                    if (_tiles.TryGetValue((tx, ty, cls), out var g)) ds.DrawGeometry(g, color, width, _fixed);
        }
        if (_smallComponents is not null) ds.DrawGeometry(_smallComponents, Color.FromArgb(255, 229, 115, 115), 2.5f, _fixed);

        // Routes, arrows, stops (screen space)
        ds.Transform = Matrix3x2.Identity;
        var sel = _vm.Selection;
        foreach (var r in map.Routes)
        {
            var c = ToColor(r.Color);
            var pts = ToScreen(map, r.Xy);
            var width = sel.SelectedVehicle == r.Vehicle ? 5f : 3f;
            for (var i = 1; i < pts.Length; i++)
                ds.DrawLine(pts[i - 1], pts[i], c, width, r.IsLocked ? _dashed : null);
            DrawArrows(ds, pts, c);
        }

        foreach (var d in map.Depots)
        {
            var (sx, sy) = map.WorldToScreen(d.X, d.Y);
            ds.FillRectangle((float)sx - 7, (float)sy - 7, 14, 14, Colors.Black);
            ds.DrawText("據點", (float)sx + 9, (float)sy - 9, Colors.Black, new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 12 });
        }

        foreach (var s in map.Stops)
        {
            var (sx, sy) = map.WorldToScreen(s.X, s.Y);
            var p = new Vector2((float)sx, (float)sy);
            var fill = s.Vehicle is { } v ? ToColor(VehiclePalette.ColorOf(v)) : Colors.White;
            var radius = s.IsMandatory ? 5.5f : 4.5f;
            ds.FillCircle(p, radius, fill);
            var stroke = s.Kind switch
            {
                StopMarkerKind.Unserved => Colors.Red,
                StopMarkerKind.SnapWarning => Colors.DarkOrange,
                _ => Colors.Black,
            };
            ds.DrawCircle(p, radius, stroke, s.Kind == StopMarkerKind.Served ? 1f : 2f);
            if (s.IsLocked) ds.FillRectangle(p.X + 4, p.Y - 9, 6, 6, Colors.Black);
            if (sel.SelectedStop == s.Stop) ds.DrawCircle(p, 10, Colors.Gold, 3);
        }

        // Drag feedback line
        if (_dragging && _vm.Drag.Stop is { } dragged)
        {
            var m = map.Stops[dragged];
            var (sx, sy) = map.WorldToScreen(m.X, m.Y);
            var color = !_vm.Drag.IsAllowed ? Colors.Gray : _vm.Drag.IsFeasible ? Colors.SeaGreen : Colors.Crimson;
            ds.DrawLine((float)sx, (float)sy, (float)_cursor.X, (float)_cursor.Y, color, 2, _dashed);
            ds.DrawCircle((float)_cursor.X, (float)_cursor.Y, 7, color, 2);
        }
    }

    private void BuildRoadGeometry(ICanvasResourceCreator device, MapViewModel map)
    {
        foreach (var g in _tiles.Values) g.Dispose();
        _tiles.Clear();
        _smallComponents?.Dispose();
        _smallComponents = null;
        var builders = new Dictionary<(int, int, RoadClass), CanvasPathBuilder>();
        using var small = new CanvasPathBuilder(device);
        var anySmall = false;
        foreach (var road in map.Roads)
        {
            CanvasPathBuilder b;
            if (road.IsSmallComponent) { b = small; anySmall = true; }
            else
            {
                var key = ((int)Math.Floor(road.Xy[0] / TileSize), (int)Math.Floor(road.Xy[1] / TileSize), road.Class);
                if (!builders.TryGetValue(key, out b!)) builders[key] = b = new CanvasPathBuilder(device);
            }
            b.BeginFigure((float)road.Xy[0], (float)road.Xy[1]);
            for (var k = 2; k < road.Xy.Length; k += 2) b.AddLine((float)road.Xy[k], (float)road.Xy[k + 1]);
            b.EndFigure(CanvasFigureLoop.Open);
        }
        foreach (var (key, b) in builders)
        {
            _tiles[key] = CanvasGeometry.CreatePath(b);
            b.Dispose();
        }
        if (anySmall) _smallComponents = CanvasGeometry.CreatePath(small);
        _builtRoadVersion = map.RoadVersion;
    }

    private static Vector2[] ToScreen(MapViewModel map, double[] xy)
    {
        var pts = new Vector2[xy.Length / 2];
        for (var i = 0; i < pts.Length; i++)
        {
            var (x, y) = map.WorldToScreen(xy[2 * i], xy[2 * i + 1]);
            pts[i] = new Vector2((float)x, (float)y);
        }
        return pts;
    }

    /// <summary>An arrowhead roughly every 90 px along the route, pointing in driving direction.</summary>
    private static void DrawArrows(CanvasDrawingSession ds, Vector2[] pts, Color color)
    {
        var acc = 45f;
        for (var i = 1; i < pts.Length; i++)
        {
            var seg = pts[i] - pts[i - 1];
            var len = seg.Length();
            if (len < 1e-3) continue;
            var dir = seg / len;
            while (acc < len)
            {
                var tip = pts[i - 1] + dir * acc;
                var normal = new Vector2(-dir.Y, dir.X);
                ds.FillGeometry(CanvasGeometry.CreatePolygon(ds, new[] { tip + dir * 6, tip - dir * 4 + normal * 5, tip - dir * 4 - normal * 5 }), color);
                acc += 90;
            }
            acc -= len;
        }
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // ------------------------------------------------------------------ interaction

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm is null) return;
        var first = _vm.Map.ViewWidth == 800 && _vm.Map.ViewHeight == 600;
        _vm.Map.Resize(e.NewSize.Width, e.NewSize.Height);
        if (first) _vm.Map.FitAll();
    }

    private void OnFitAll(object sender, RoutedEventArgs e) => _vm?.Map.FitAll();

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        var pt = e.GetCurrentPoint(Canvas);
        var delta = pt.Properties.MouseWheelDelta;
        _vm.Map.ZoomAt(pt.Position.X, pt.Position.Y, delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        var pt = e.GetCurrentPoint(Canvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Canvas.CapturePointer(e.Pointer);
        Focus(FocusState.Programmatic);
        _pressed = true;
        _pressPoint = _lastPoint = pt.Position;
        _pressedStop = PickingPosition ? null : _vm.Map.HitTestStop(pt.Position.X, pt.Position.Y);
        _panning = _dragging = false;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || !_pressed) return;
        var p = e.GetCurrentPoint(Canvas).Position;
        _cursor = p;
        var moved = Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) > 4;
        if (_pressedStop is { } stop && (moved || _dragging) && _vm.Workspace?.Editor is { } editor)
        {
            if (!_dragging) { _vm.Drag.Begin(stop); _dragging = true; }
            var target = _vm.Map.HitTestStop(p.X, p.Y, 25);
            if (target is { } t && t != stop && _vm.Map.Stops[t].Vehicle is { } v)
            {
                var without = editor.Routes[v].Where(x => x != stop).ToList();
                _vm.Drag.UpdateTarget(v, without.IndexOf(t) + 1);
            }
            UpdateBadge();
            Canvas.Invalidate();
        }
        else if (_pressedStop is null && (moved || _panning))
        {
            _panning = true;
            _vm.Map.PanBy(p.X - _lastPoint.X, p.Y - _lastPoint.Y);
        }
        _lastPoint = p;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || !_pressed) return;
        _pressed = false;
        Canvas.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(Canvas).Position;
        if (_dragging)
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (_vm.Drag.TargetVehicle is not null) _vm.Drag.Commit(force: shift);
            else _vm.Drag.Cancel();
            _dragging = false;
            UpdateBadge();
        }
        else if (!_panning)
        {
            if (PickingPosition)
            {
                var (wx, wy) = _vm.Map.ScreenToWorld(p.X, p.Y);
                _vm.Reschedule.SetPositionFromMap(_vm.Map.WorldToGeo(wx, wy));
                PickingPosition = false;
                PositionPicked?.Invoke(this, EventArgs.Empty);
            }
            else _vm.Map.Click(p.X, p.Y);
        }
        _panning = false;
        Canvas.Invalidate();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Canvas);
        _vm.Map.Click(p.X, p.Y);
        if (_vm.Selection.SelectedStop is null) return;
        var menu = new MenuFlyout();
        var lockStop = new MenuFlyoutItem { Text = _vm.Workspace?.Editor?.IsStopLocked(_vm.Selection.SelectedStop.Value) == true ? "解除鎖定此站" : "鎖定此站（固定車輛與順序）" };
        lockStop.Click += (_, _) => _vm.ToggleStopLockCommand.Execute(null);
        menu.Items.Add(lockStop);
        if (_vm.Selection.SelectedVehicle is { } v)
        {
            var lockRoute = new MenuFlyoutItem { Text = _vm.Workspace?.Editor?.IsRouteLocked(v) == true ? "解除鎖定此路線" : "鎖定整條路線" };
            lockRoute.Click += (_, _) => _vm.ToggleRouteLockCommand.Execute(null);
            menu.Items.Add(lockRoute);
            var unassign = new MenuFlyoutItem { Text = "移出路線（不服務）" };
            unassign.Click += (_, _) =>
            {
                _vm.Drag.Begin(_vm.Selection.SelectedStop!.Value);
                _vm.Drag.UpdateTarget(null, 0);
                _vm.Drag.Commit();
            };
            menu.Items.Add(unassign);
        }
        menu.ShowAt(Canvas, p);
    }

    private void UpdateBadge()
    {
        if (_vm is null || !_dragging || _vm.Drag.TargetVehicle is null)
        {
            DragBadge.Visibility = _dragging ? Visibility.Visible : Visibility.Collapsed;
            DragMessage.Text = _dragging ? "拖到另一條路線的站點旁以預覽" : "";
            DragDelta.Text = "";
            return;
        }
        DragBadge.Visibility = Visibility.Visible;
        DragMessage.Text = (_vm.Drag.IsFeasible ? "✔ " : "✖ ") + _vm.Drag.Message;
        DragMessage.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(_vm.Drag.IsFeasible ? Colors.SeaGreen : Colors.Crimson);
        DragDelta.Text = $"{_vm.Drag.DeltaText}（{_vm.Drag.LatencyMs:F1} ms）";
    }
}
