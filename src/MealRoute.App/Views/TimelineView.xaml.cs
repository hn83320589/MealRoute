using System.Numerics;
using MealRoute.App.ViewModels;
using MealRoute.Core.Evaluation;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MealRoute.App.Views;

/// <summary>Gantt timeline: one row per vehicle with drive / wait / service / break bars.</summary>
public sealed partial class TimelineView : UserControl
{
    private const float LabelWidth = 170, RowHeight = 30, AxisHeight = 24;
    private MainViewModel? _vm;
    private readonly CanvasTextFormat _label = new() { FontSize = 12, VerticalAlignment = CanvasVerticalAlignment.Center };
    private readonly CanvasTextFormat _tick = new() { FontSize = 11 };
    private bool _pressed, _dragging;
    private int? _pressedStop;
    private Point _press, _cursor;

    public TimelineView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateHeight();
    }

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        vm.Timeline.PropertyChanged += (_, _) => { UpdateHeight(); Canvas.Invalidate(); };
    }

    private void UpdateHeight()
    {
        var rows = _vm?.Timeline.Rows.Count ?? 0;
        Canvas.Height = Math.Max(ActualHeight, AxisHeight + rows * RowHeight + 8);
        Canvas.Width = Math.Max(1, ActualWidth);
    }

    private float X(double time)
    {
        var tl = _vm!.Timeline;
        var w = (float)Canvas.ActualWidth - LabelWidth - 10;
        return LabelWidth + (float)((time - tl.StartTime) / Math.Max(1, tl.EndTime - tl.StartTime)) * w;
    }

    private double Time(double x)
    {
        var tl = _vm!.Timeline;
        var w = Canvas.ActualWidth - LabelWidth - 10;
        return tl.StartTime + (x - LabelWidth) / Math.Max(1, w) * (tl.EndTime - tl.StartTime);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_vm is null) return;
        var ds = args.DrawingSession;
        var tl = _vm.Timeline;
        var sel = _vm.Selection;
        var height = (float)sender.ActualHeight;

        for (var t = Math.Ceiling(tl.StartTime / 1800) * 1800; t <= tl.EndTime; t += 1800)
        {
            var x = X(t);
            ds.DrawLine(x, AxisHeight - 4, x, height, Color.FromArgb(255, 235, 235, 235));
            ds.DrawText(TimeOfDay.Format(t), x + 2, 2, Colors.Gray, _tick);
        }

        for (var i = 0; i < tl.Rows.Count; i++)
        {
            var row = tl.Rows[i];
            var y = AxisHeight + i * RowHeight;
            if (sel.SelectedVehicle == row.Vehicle) ds.FillRectangle(0, y, (float)sender.ActualWidth, RowHeight, Color.FromArgb(255, 255, 249, 219));
            var color = ToColor(row.Color);
            ds.FillRectangle(4, y + 9, 12, 12, color);
            ds.DrawText((row.IsLocked ? "🔒 " : "") + row.Label, new Rect(22, y, LabelWidth - 24, RowHeight),
                row.IsFeasible ? Colors.Black : Colors.Crimson, _label);
            foreach (var s in row.Segments)
            {
                float x0 = X(s.Start), x1 = Math.Max(X(s.End), x0 + 1);
                var (fill, top, h) = s.Kind switch
                {
                    SegmentKind.Drive => (Color.FromArgb(110, color.R, color.G, color.B), 11f, 8f),
                    SegmentKind.Wait => (Color.FromArgb(255, 210, 210, 210), 12f, 6f),
                    SegmentKind.Service => (color, 6f, 18f),
                    _ => (Color.FromArgb(255, 123, 65, 115), 6f, 18f),
                };
                ds.FillRectangle(x0, y + top, x1 - x0, h, fill);
                if (s.Kind == SegmentKind.Service && s.Stop == sel.SelectedStop) ds.DrawRectangle(x0 - 1, y + top - 1, x1 - x0 + 2, h + 2, Colors.Gold, 2);
            }
        }

        if (_dragging && _vm.Drag.TargetVehicle is { } tv)
        {
            var c = _vm.Drag.IsFeasible ? Colors.SeaGreen : Colors.Crimson;
            var rowIndex = tl.Rows.ToList().FindIndex(r => r.Vehicle == tv);
            ds.DrawLine((float)_cursor.X, AxisHeight + rowIndex * RowHeight, (float)_cursor.X, AxisHeight + (rowIndex + 1) * RowHeight, c, 3);
            ds.DrawText(_vm.Drag.Message, (float)_cursor.X + 6, AxisHeight + rowIndex * RowHeight + 2, c, _tick);
        }
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private int RowAt(double y) => (int)Math.Floor((y - AxisHeight) / RowHeight);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetCurrentPoint(Canvas).Position;
        _pressed = true;
        _press = p;
        _dragging = false;
        var hit = _vm.Timeline.HitTest(RowAt(p.Y), Time(p.X));
        _pressedStop = hit?.Stop;
        Canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || !_pressed || _pressedStop is not { } stop || _vm.Workspace?.Editor is not { } editor) return;
        var p = e.GetCurrentPoint(Canvas).Position;
        _cursor = p;
        if (!_dragging && Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) < 5) return;
        if (!_dragging) { _vm.Drag.Begin(stop); _dragging = true; }
        var row = RowAt(p.Y);
        if (row >= 0 && row < _vm.Timeline.Rows.Count)
        {
            var vehicle = _vm.Timeline.Rows[row].Vehicle;
            var index = _vm.Timeline.InsertionIndex(row, Time(p.X));
            var current = editor.Routes[vehicle].ToList().IndexOf(stop);
            if (current >= 0 && current < index) index--;
            _vm.Drag.UpdateTarget(vehicle, index);
        }
        Canvas.Invalidate();
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
        }
        else _vm.Timeline.Click(RowAt(p.Y), Time(p.X));
        Canvas.Invalidate();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Canvas);
        _vm.Timeline.Click(RowAt(p.Y), Time(p.X));
        if (_vm.Selection.SelectedVehicle is not { } v) return;
        var menu = new MenuFlyout();
        if (_vm.Selection.SelectedStop is { } stop)
        {
            var lockStop = new MenuFlyoutItem { Text = _vm.Workspace?.Editor?.IsStopLocked(stop) == true ? "解除鎖定此站" : "鎖定此站（固定車輛與順序）" };
            lockStop.Click += (_, _) => _vm.ToggleStopLockCommand.Execute(null);
            menu.Items.Add(lockStop);
        }
        var lockRoute = new MenuFlyoutItem { Text = _vm.Workspace?.Editor?.IsRouteLocked(v) == true ? "解除鎖定此路線" : "鎖定整條路線" };
        lockRoute.Click += (_, _) => _vm.ToggleRouteLockCommand.Execute(null);
        menu.Items.Add(lockRoute);
        menu.ShowAt(Canvas, p);
    }
}
