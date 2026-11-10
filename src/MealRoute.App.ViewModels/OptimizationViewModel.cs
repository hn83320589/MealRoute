using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Optimization;
using MealRoute.Core.Projects;

namespace MealRoute.App.ViewModels;

/// <summary>Runs the optimiser in the background with monotone progress, cancel, and applies the result.</summary>
public sealed partial class OptimizationViewModel : ObservableObject
{
    private readonly Func<PlanningWorkspace?> _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Action _completed;
    private Action? _cancel;

    public OptimizationViewModel(Func<PlanningWorkspace?> workspace, IUiDispatcher dispatcher, TimeProvider time, Action completed)
    {
        _workspace = workspace;
        _dispatcher = dispatcher;
        _time = time;
        _completed = completed;
    }

    [ObservableProperty] private double _seconds = 60;
    [ObservableProperty] private int _runs = 4;
    [ObservableProperty] private ulong _seed = 1;
    /// <summary>Optional iteration limit per run (reproducible results); 0 = only the time limit.</summary>
    [ObservableProperty] private int _maxIterations;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private OptimizationResult? _lastResult;

    private bool CanStart() => !IsRunning && _workspace()?.Editor is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync();

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        _cancel?.Invoke();
        Status = "取消中…已保留目前最佳可行解";
    }

    /// <summary>Re-optimises from the current plan, keeping locks; the result replaces the plan.</summary>
    public async Task RunAsync()
    {
        var ws = _workspace();
        if (ws?.Editor is not { } editor || IsRunning) return;
        IsRunning = true;
        Progress = 0;
        Status = "最佳化中…";
        using var cts = new CancellationTokenSource();
        _cancel = cts.Cancel;
        var progress = new DispatchedProgress(_dispatcher, p =>
        {
            if (p.Fraction > Progress) Progress = p.Fraction;
            Status = $"最佳化中 {p.Fraction:P0}：{p.BestObjective}";
        });
        try
        {
            var options = new OptimizerOptions
            {
                Seed = Seed,
                Runs = Math.Max(1, Runs),
                MaxIterations = MaxIterations > 0 ? MaxIterations : 10_000_000,
                TimeLimit = TimeSpan.FromSeconds(Math.Max(1, Seconds)),
                TimeProvider = _time,
            };
            ws.UpdateOptimizerSettings(new OptimizerSettings(Seed, Seconds, options.MaxIterations, options.Runs));
            var locks = editor.Locks;
            var initial = editor.ToSolution();
            var result = await ws.CreateOptimizer().RunAsync(initial, locks, options, progress, cts.Token).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                editor.ReplaceSolution(result.Solution);
                LastResult = result;
                Progress = Math.Max(Progress, result.Cancelled ? Progress : 1);
                Status = (result.Cancelled ? "已取消，採用目前最佳解：" : "完成：") + result.Objective;
                _completed();
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
            _cancel = null;
        }
    }

    /// <summary>Marshals progress to the UI thread.</summary>
    internal sealed class DispatchedProgress(IUiDispatcher dispatcher, Action<OptimizationProgress> handler) : IProgress<OptimizationProgress>
    {
        public void Report(OptimizationProgress value) => dispatcher.Post(() => handler(value));
    }
}
