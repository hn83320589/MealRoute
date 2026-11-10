using System.ComponentModel;
using System.Diagnostics;
using MealRoute.App.ViewModels;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Geo;
using MealRoute.Core.Projects;
using MealRoute.Core.Reports;
using MealRoute.Tests.Benchmark;
using MealRoute.Tests.Projects;

namespace MealRoute.Tests.ViewModels;

public class MainViewModelTests
{
    // ------------------------------------------------------------------ opening projects

    [Fact]
    public async Task Opening_a_project_fills_map_timeline_and_warnings()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        Assert.True(main.Map.Roads.Count > 1_000);
        Assert.Equal(30, main.Map.Stops.Count);
        Assert.Contains(main.Map.Roads, r => r.IsSmallComponent);
        Assert.Equal(3, main.Timeline.Rows.Count);
        Assert.Contains(main.Warnings, w => w.Kind == WarningKind.SmallComponent);
        Assert.Contains(main.Warnings, w => w.Kind == WarningKind.Snapping && w.StopIndex is not null);
        Assert.Contains(main.Map.Stops, s => s.Kind == StopMarkerKind.SnapWarning);
        Assert.Contains("small", main.Title, StringComparison.Ordinal);
        Assert.True(main.Optimization.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Corrupt_project_shows_a_message_and_does_not_crash()
    {
        using var kit = new ViewModelTestKit();
        var path = Path.Combine(kit.Dir, "broken.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\": 1, \"stops\": [");
        await kit.Main.OpenProjectFromPathAsync(path);
        var msg = Assert.Single(kit.Messages.Errors);
        Assert.Contains("損毀", msg, StringComparison.Ordinal);
        Assert.Null(kit.Main.Workspace);
        Assert.False(kit.Main.IsBusy);
    }

    [Fact]
    public async Task Newer_project_version_shows_a_clear_message()
    {
        using var kit = new ViewModelTestKit();
        var path = Path.Combine(kit.Dir, "future.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\": 99}");
        await kit.Main.OpenProjectFromPathAsync(path);
        Assert.Contains("較新版本", Assert.Single(kit.Messages.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\evil-host\share\city.geojson")]
    [InlineData(@"\\?\C:\city.geojson")]
    [InlineData("//evil-host/share/city.geojson")]
    public async Task Project_referencing_a_network_share_path_is_not_followed(string networkPath)
    {
        using var kit = new ViewModelTestKit();
        var project = ProjectSerializer.Load(SolomonTests.RepoFile("samples", "small", "project.mealroute.json"));
        var path = Path.Combine(kit.Dir, "shared.json");
        ProjectSerializer.Save(project with { Network = new NetworkReference(networkPath, "00") }, path);
        await kit.Main.OpenProjectFromPathAsync(path);
        Assert.Contains("網路或裝置路徑", Assert.Single(kit.Messages.Errors), StringComparison.Ordinal);
        Assert.Null(kit.Main.Workspace);
    }

    [Fact]
    public async Task Save_and_reopen_keeps_plan_and_locks()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var v = Enumerable.Range(0, 3).First(x => editor.Routes[x].Count > 0);
        main.Selection.SelectVehicle(v);
        main.ToggleRouteLock();
        var objective = editor.Objective;

        var target = Path.Combine(kit.Dir, "sub", "saved.json");
        kit.Files.Next.Enqueue(target);
        await main.SaveProjectAsAsync();
        Assert.Empty(kit.Messages.Errors);

        using var kit2 = new ViewModelTestKit();
        await kit2.Main.OpenProjectFromPathAsync(target);
        Assert.Empty(kit2.Messages.Errors);
        Assert.Equal(objective, kit2.Main.Workspace!.Editor!.Objective);
        Assert.True(kit2.Main.Workspace.Editor.IsRouteLocked(v));
    }

    // ------------------------------------------------------------------ selection linking (#21)

    [Fact]
    public async Task Clicking_a_stop_on_the_map_selects_it_on_the_timeline_too()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var marker = main.Map.Stops.First(s => s.Vehicle is not null);
        var (sx, sy) = main.Map.WorldToScreen(marker.X, marker.Y);

        main.Map.Click(sx, sy);

        Assert.Equal(marker.Stop, main.Selection.SelectedStop);
        Assert.Equal(marker.Vehicle, main.Selection.SelectedVehicle);
        var row = main.Timeline.Rows.Single(r => r.Vehicle == marker.Vehicle);
        Assert.Contains(row.Segments, s => s.Stop == marker.Stop && s.Kind == SegmentKind.Service);
        Assert.Contains(main.Workspace!.Instance.Stops[marker.Stop].Name, main.SelectionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_a_service_bar_on_the_timeline_selects_the_stop_on_the_map()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var rowIndex = main.Timeline.Rows.ToList().FindIndex(r => r.Segments.Count > 0);
        var service = main.Timeline.Rows[rowIndex].Segments.First(s => s.Kind == SegmentKind.Service);

        main.Timeline.Click(rowIndex, (service.Start + service.End) / 2);

        Assert.Equal(service.Stop, main.Selection.SelectedStop);
        Assert.Equal(main.Timeline.Rows[rowIndex].Vehicle, main.Selection.SelectedVehicle);
        var version = main.Map.Version;
        main.Selection.Clear();
        Assert.True(main.Map.Version > version, "map redraws on selection change");
    }

    [Fact]
    public async Task Clicking_empty_map_space_clears_the_selection()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        main.Selection.SelectStop(0, null);
        main.Map.Click(-10_000, -10_000);
        Assert.Null(main.Selection.SelectedStop);
    }

    // ------------------------------------------------------------------ map viewport

    [Fact]
    public async Task Zoom_keeps_the_point_under_the_cursor_and_coordinates_round_trip()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        var map = main.Map;
        map.Resize(1000, 700);
        var before = map.ScreenToWorld(300, 200);
        map.ZoomAt(300, 200, 2.5);
        var after = map.ScreenToWorld(300, 200);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        map.PanBy(50, -20);
        var (sx, sy) = map.WorldToScreen(after.X, after.Y);
        Assert.Equal(350, sx, 6);
        Assert.Equal(180, sy, 6);

        var g = new GeoPoint(25.01, 121.46);
        var (wx, wy) = map.GeoToWorld(g);
        var back = map.WorldToGeo(wx, wy);
        Assert.Equal(g.Lat, back.Lat, 9);
        Assert.Equal(g.Lon, back.Lon, 9);
    }

    [Fact]
    public async Task Fit_all_shows_every_stop()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        main.Map.Resize(800, 600);
        main.Map.ZoomAt(0, 0, 50);
        main.Map.FitAll();
        foreach (var s in main.Map.Stops)
        {
            var (x, y) = main.Map.WorldToScreen(s.X, s.Y);
            Assert.InRange(x, 0, 800);
            Assert.InRange(y, 0, 600);
        }
    }

    // ------------------------------------------------------------------ drag feedback (#14)

    [Fact]
    public async Task Dragging_a_stop_gives_feasibility_metric_changes_and_first_violation_within_50_ms()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var from = Enumerable.Range(0, 3).First(v => editor.Routes[v].Count >= 2);
        var to = Enumerable.Range(0, 3).First(v => v != from);
        var stop = editor.Routes[from][0];

        main.Drag.Begin(stop);
        var latencies = new List<double>();
        for (var pos = 0; pos <= editor.Routes[to].Count; pos++)
        {
            main.Drag.UpdateTarget(to, pos);
            latencies.Add(main.Drag.LatencyMs);
            Assert.True(main.Drag.IsAllowed);
            Assert.False(string.IsNullOrEmpty(main.Drag.Message));
            Assert.Contains("行車", main.Drag.DeltaText, StringComparison.Ordinal);
            if (!main.Drag.IsFeasible)
            {
                Assert.NotNull(main.Drag.FirstViolation);
                Assert.Equal(main.Drag.FirstViolation!.Message, main.Drag.Message);
            }
        }
        latencies.Sort();
        Assert.True(latencies[latencies.Count / 2] < 50, $"median {latencies[latencies.Count / 2]:F1} ms");
        main.Drag.Cancel();
    }

    [Fact]
    public async Task Infeasible_drop_names_the_stop_its_arrival_and_its_window()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var inst = main.Workspace.Instance;
        // move a stop to the very end of a long route: something will be late or over the work limit
        var target = Enumerable.Range(0, 3).OrderByDescending(v => editor.Routes[v].Count).First();
        var feasibleFound = false;
        foreach (var stop in Enumerable.Range(0, inst.Stops.Count).Where(s => !editor.Routes[target].Contains(s)))
        {
            main.Drag.Begin(stop);
            main.Drag.UpdateTarget(target, 0);
            if (main.Drag.IsFeasible) { feasibleFound = true; continue; }
            var v = main.Drag.FirstViolation!;
            if (v.Kind == Core.Evaluation.ViolationKind.TimeWindow)
            {
                var named = inst.Stops[v.Stop];
                Assert.StartsWith(named.Name, main.Drag.Message, StringComparison.Ordinal);
                Assert.Contains("到，時間窗", main.Drag.Message, StringComparison.Ordinal);
                Assert.Contains(Core.Evaluation.TimeOfDay.Format(named.WindowEnd), main.Drag.Message, StringComparison.Ordinal);
                return;
            }
        }
        Assert.True(feasibleFound, "no infeasible time-window drop found and no feasible one either");
    }

    [Fact]
    public async Task Commit_applies_the_move_and_undo_redo_follow()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var stop = editor.Routes.First(r => r.Count > 0)[0];

        main.Drag.Begin(stop);
        main.Drag.UpdateTarget(null, 0); // take it off the routes (always allowed & feasible)
        Assert.True(main.Drag.Commit());
        Assert.Contains(stop, editor.Unserved);
        Assert.Equal(StopMarkerKind.Unserved, main.Map.Stops[stop].Kind);
        Assert.True(main.UndoCommand.CanExecute(null));

        main.Undo();
        Assert.DoesNotContain(stop, editor.Unserved);
        Assert.True(main.RedoCommand.CanExecute(null));
        main.Redo();
        Assert.Contains(stop, editor.Unserved);
    }

    // ------------------------------------------------------------------ locks (#21)

    [Fact]
    public async Task Locking_a_stop_marks_it_on_the_map_and_blocks_dragging_it()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var v = Enumerable.Range(0, 3).First(x => editor.Routes[x].Count > 0);
        var stop = editor.Routes[v][0];
        main.Selection.SelectStop(stop, v);

        main.ToggleStopLockCommand.Execute(null);
        Assert.True(main.Map.Stops[stop].IsLocked);
        Assert.Contains("已鎖定", main.SelectionText, StringComparison.Ordinal);

        main.Drag.Begin(stop);
        main.Drag.UpdateTarget(null, 0);
        Assert.False(main.Drag.IsAllowed);
        Assert.Contains("鎖定", main.Drag.Message, StringComparison.Ordinal);
        Assert.False(main.Drag.Commit());

        main.ToggleStopLockCommand.Execute(null);
        Assert.False(main.Map.Stops[stop].IsLocked);
    }

    [Fact]
    public async Task Locking_a_route_marks_route_and_timeline_row_and_survives_reoptimisation()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var v = Enumerable.Range(0, 3).First(x => editor.Routes[x].Count > 0);
        var stops = editor.Routes[v].ToArray();
        main.Selection.SelectVehicle(v);
        main.ToggleRouteLockCommand.Execute(null);
        Assert.True(main.Map.Routes.Single(r => r.Vehicle == v).IsLocked);
        Assert.True(main.Timeline.Rows[v].IsLocked);

        main.Optimization.Seed = 99;
        await main.Optimization.RunAsync();
        Assert.Equal(stops, editor.Routes[v]);
    }

    // ------------------------------------------------------------------ optimisation progress (#21)

    [Fact]
    public async Task Optimisation_reports_monotone_progress_and_replaces_the_plan()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        var seen = new List<double>();
        var running = new List<bool>();
        main.Optimization.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizationViewModel.Progress)) seen.Add(main.Optimization.Progress);
            if (e.PropertyName == nameof(OptimizationViewModel.IsRunning)) running.Add(main.Optimization.IsRunning);
        };
        await kit.SolveAsync(iterations: 300);

        Assert.Equal(new[] { true, false }, running);
        Assert.NotEmpty(seen);
        for (var i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1]);
        Assert.Equal(1.0, main.Optimization.Progress);
        Assert.StartsWith("完成", main.Optimization.Status, StringComparison.Ordinal);
        Assert.Equal(0, main.Workspace!.Editor!.Objective.UnservedMandatory);
        Assert.Contains(main.Map.Routes, r => r.Xy.Length > 4);
        Assert.NotEmpty(main.RouteSheets.Sheets);
    }

    [Fact]
    public async Task Cancelling_optimisation_keeps_a_complete_plan()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        main.Optimization.MaxIterations = 5_000_000;
        main.Optimization.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizationViewModel.Progress) && main.Optimization.CancelCommand.CanExecute(null))
                main.Optimization.CancelCommand.Execute(null);
        };
        await main.Optimization.RunAsync();
        Assert.True(main.Optimization.LastResult!.Cancelled);
        Assert.False(main.Optimization.IsRunning);
        var editor = main.Workspace!.Editor!;
        var all = editor.Routes.SelectMany(r => r).Concat(editor.Unserved).OrderBy(s => s);
        Assert.Equal(Enumerable.Range(0, 30), all);
    }

    // ------------------------------------------------------------------ rescheduling flow (#21)

    [Fact]
    public async Task Reschedule_flow_defaults_to_now_prechecks_done_stops_and_applies()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var vm = main.Reschedule;
        vm.Prepare();
        Assert.Equal(3, vm.Vehicles.Count);
        Assert.Equal(11.5 * 3600, vm.ReportTime, 6); // FakeTimeProvider now = 11:30

        var v = Enumerable.Range(0, 3).First(x => editor.Routes[x].Count >= 2);
        vm.SelectedVehicle = v;
        var eval = editor.CurrentEvaluation(v);
        Assert.Equal(eval.Visits.Select(x => x.Departure <= vm.ReportTime), vm.Stops.Select(s => s.IsCompleted));

        vm.DelayMinutes = 15;
        vm.Seconds = 2;
        vm.AddReport();
        Assert.Single(vm.Reports);
        await vm.RunCoreAsync();
        Assert.NotNull(vm.Result);
        Assert.Contains("重新排程後", vm.Summary, StringComparison.Ordinal);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.ApplyCommand.Execute(null);
        Assert.Null(vm.Result);
        Assert.True(main.UndoCommand.CanExecute(null));
        var done = vm.Stops.Count == 0 ? Array.Empty<int>() : eval.Visits.Where(x => x.Departure <= 11.5 * 3600).Select(x => x.Stop).ToArray();
        Assert.Equal(done, editor.Routes[v].Take(done.Length));
    }

    [Fact]
    public async Task Reschedule_with_breakdown_lists_stops_as_unservable()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var editor = main.Workspace!.Editor!;
        var vm = main.Reschedule;
        vm.Prepare();
        var v = Enumerable.Range(0, 3).First(x => editor.Routes[x].Count >= 2);
        vm.SelectedVehicle = v;
        foreach (var s in vm.Stops) s.IsCompleted = false;
        vm.Stops[0].IsCompleted = true;
        vm.IsBrokenDown = true;
        vm.Seconds = 2;
        vm.AddReport();
        await vm.RunCoreAsync();
        var lost = editor.Routes[v].Skip(1).ToArray();
        Assert.All(lost, s => Assert.Contains(vm.Result!.Solution.Unserved, u => u.Stop == s && u.Reason.Contains("故障", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Reschedule_position_can_come_from_the_map()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        var vm = main.Reschedule;
        vm.Prepare();
        vm.SelectedVehicle = 0;
        var p = main.Map.WorldToGeo(10, 10);
        vm.SetPositionFromMap(p);
        vm.AddReport();
        Assert.Equal(p, vm.Reports.Single().Position);
    }

    // ------------------------------------------------------------------ outputs

    [Fact]
    public async Task Route_sheets_print_and_exports_round_trip()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await kit.SolveAsync();
        await main.RouteSheets.PrintAsync();
        Assert.Equal(main.RouteSheets.Sheets.Count, kit.Print.Printed!.Count);

        var csv = Path.Combine(kit.Dir, "out.csv");
        kit.Files.Next.Enqueue(csv);
        await main.RouteSheets.ExportCsvAsync();
        var inst = main.Workspace!.Instance;
        ProjectAndExportTests.AssertSameSolution(main.Workspace.Editor!.ToSolution(), RouteCsv.ReadFile(csv, inst));

        var gj = Path.Combine(kit.Dir, "out.geojson");
        kit.Files.Next.Enqueue(gj);
        await main.RouteSheets.ExportGeoJsonAsync();
        ProjectAndExportTests.AssertSameSolution(main.Workspace.Editor.ToSolution(), RouteGeoJson.Read(await File.ReadAllTextAsync(gj), inst));
    }

    [Fact]
    public async Task Cancelled_file_dialog_does_nothing()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync();
        await main.OpenProjectAsync(); // dialog returns null
        await main.RouteSheets.ExportCsvAsync();
        Assert.Empty(kit.Messages.Errors);
        Assert.NotNull(main.Workspace);
    }

    // ------------------------------------------------------------------ static mode, settings

    [Fact]
    public async Task Solomon_instance_opens_in_static_mode_and_can_be_optimised()
    {
        using var kit = new ViewModelTestKit();
        kit.Files.Next.Enqueue(SolomonTests.RepoFile("benchmarks", "solomon", "c101.txt"));
        await kit.Main.OpenSolomonAsync();
        var main = kit.Main;
        Assert.True(main.Workspace!.IsStaticMode);
        Assert.Empty(main.Map.Roads);
        Assert.Equal(100, main.Map.Stops.Count);
        await kit.SolveAsync(iterations: 50);
        Assert.Equal(0, main.Workspace.Editor!.Objective.UnservedMandatory);
    }

    [Fact]
    public void Settings_round_trip_and_unreadable_settings_fall_back_to_defaults()
    {
        using var kit = new ViewModelTestKit();
        var store = new JsonSettingsStore(Path.Combine(kit.Dir, "s", "settings.json"));
        Assert.Equal(new AppSettings(), store.Load());
        store.Save(new AppSettings("x.json", 30, 2));
        Assert.Equal(new AppSettings("x.json", 30, 2), store.Load());
        File.WriteAllText(store.Path, "{not json");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public async Task Opening_a_project_remembers_it_in_settings()
    {
        using var kit = new ViewModelTestKit();
        await kit.OpenSampleAsync();
        var settings = new JsonSettingsStore(Path.Combine(kit.Dir, "settings.json")).Load();
        Assert.Equal(Path.Combine(kit.Dir, "project.mealroute.json"), settings.LastProjectPath);
    }

    [Fact]
    public async Task Drag_latency_on_the_medium_sample_stays_under_50_ms()
    {
        using var kit = new ViewModelTestKit();
        var main = await kit.OpenSampleAsync("medium");
        await kit.SolveAsync(iterations: 100);
        var editor = main.Workspace!.Editor!;
        var r = new Random(4);
        var latencies = new List<double>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            var stop = r.Next(main.Workspace.Instance.Stops.Count);
            var v = r.Next(main.Workspace.Instance.Vehicles.Count);
            main.Drag.Begin(stop);
            main.Drag.UpdateTarget(v, r.Next(editor.Routes[v].Count + 1));
            latencies.Add(main.Drag.LatencyMs);
            main.Drag.Cancel();
        }
        latencies.Sort();
        Assert.True(latencies[latencies.Count / 2] < 50, $"median {latencies[latencies.Count / 2]:F1} ms, max {latencies[^1]:F1} ms");
    }
}
