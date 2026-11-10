using System.Globalization;
using MealRoute.App.Services;
using MealRoute.App.ViewModels;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Evaluation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MealRoute.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        var messages = new ContentDialogMessageService(() => Content?.XamlRoot);
        ViewModel = new MainViewModel(
            new WinUiFileDialogService(this),
            messages,
            new Win2DPrintService(this, messages),
            new WinUiDispatcher(DispatcherQueue),
            TimeProvider.System,
            new JsonSettingsStore());
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 950));
        Map.Attach(ViewModel);
        Timeline.Attach(ViewModel);
        Map.PositionPicked += (_, _) =>
        {
            PickPositionButton.IsChecked = false;
            ViewModel.Status = "已從地圖選取車輛目前位置";
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Title)) Title = ViewModel.Title;
            if (e.PropertyName == nameof(MainViewModel.ObjectiveText)) RefreshSheets();
        };
        ViewModel.Reschedule.Reports.CollectionChanged += (_, _) =>
            ReportsText.Text = string.Join("\n", ViewModel.Reschedule.Reports.Select(r =>
                $"{ViewModel.Workspace?.Instance.Vehicles[r.Vehicle].DriverName}：{TimeOfDay.Format(r.ReportTime)}，完成 {r.CompletedStops.Count} 站" +
                (r.BrokenDown ? "，故障" : r.DelayMinutes > 0 ? $"，延誤 {r.DelayMinutes:F0} 分" : "")));
        Activated += OnFirstActivated;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Opens the project given on the command line, else the last opened one.</summary>
    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        var argsPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        var path = argsPath ?? ViewModel.Settings.LastProjectPath;
        if (path is not null && File.Exists(path)) await ViewModel.OpenProjectFromPathAsync(path);
    }

    private void RefreshSheets() =>
        SheetsText.Text = string.Join("\n", ViewModel.RouteSheets.Sheets.Select(s => s.ToText()));

    private void OnRunsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue)) ViewModel.Optimization.Runs = (int)args.NewValue;
    }

    private void OnSeedChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue) && args.NewValue >= 0) ViewModel.Optimization.Seed = (ulong)args.NewValue;
    }

    private void OnPrepareReschedule(object sender, RoutedEventArgs e)
    {
        ViewModel.Reschedule.Prepare();
        ReportTimeBox.Text = TimeOfDay.Format(ViewModel.Reschedule.ReportTime);
    }

    private void OnReportTimeLostFocus(object sender, RoutedEventArgs e)
    {
        var parts = ReportTimeBox.Text.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && h is >= 0 and < 48 && m is >= 0 and < 60)
            ViewModel.Reschedule.ReportTime = h * 3600 + m * 60;
        ReportTimeBox.Text = TimeOfDay.Format(ViewModel.Reschedule.ReportTime);
    }

    private void OnPickPosition(object sender, RoutedEventArgs e)
    {
        Map.PickingPosition = PickPositionButton.IsChecked == true;
        if (Map.PickingPosition) ViewModel.Status = "請在地圖上點選車輛目前位置";
    }
}
