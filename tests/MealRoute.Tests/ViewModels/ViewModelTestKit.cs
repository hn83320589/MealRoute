using MealRoute.App.ViewModels;
using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Reports;
using MealRoute.Tests.Benchmark;
using Microsoft.Extensions.Time.Testing;

namespace MealRoute.Tests.ViewModels;

internal sealed class FakeFiles : IFileDialogService
{
    public Queue<string?> Next { get; } = new();
    public Task<string?> PickOpenFileAsync(string title, string filters) => Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string filters) => Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);
}

internal sealed class FakeMessages : IMessageService
{
    public List<string> Errors { get; } = new();
    public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
}

internal sealed class FakePrint : IPrintService
{
    public IReadOnlyList<RouteSheet>? Printed { get; private set; }
    public Task PrintAsync(IReadOnlyList<RouteSheet> sheets) { Printed = sheets; return Task.CompletedTask; }
}

/// <summary>A MainViewModel wired to fakes, a fixed clock (11:30) and a temp folder.</summary>
internal sealed class ViewModelTestKit : IDisposable
{
    public ViewModelTestKit()
    {
        Dir = Path.Combine(Path.GetTempPath(), "mealroute-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 11, 30, 0, TimeSpan.Zero));
        Main = new MainViewModel(Files, Messages, Print, new InlineDispatcher(), Clock, new JsonSettingsStore(Path.Combine(Dir, "settings.json")));
        // The fake clock never advances, so searches must end on an iteration limit.
        Main.Reschedule.MaxIterations = 200;
    }

    public string Dir { get; }
    public FakeFiles Files { get; } = new();
    public FakeMessages Messages { get; } = new();
    public FakePrint Print { get; } = new();
    public FakeTimeProvider Clock { get; }
    public MainViewModel Main { get; }

    /// <summary>Copies a shipped sample into the temp folder (so tests never modify the repo) and opens it.</summary>
    public async Task<MainViewModel> OpenSampleAsync(string size = "small")
    {
        var src = SolomonTests.RepoFile("samples", size);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(Dir, Path.GetFileName(f)), overwrite: true);
        await Main.OpenProjectFromPathAsync(Path.Combine(Dir, "project.mealroute.json"));
        Assert.Empty(Messages.Errors);
        return Main;
    }

    /// <summary>Quick deterministic optimisation so there are routes to work with.</summary>
    public async Task SolveAsync(int iterations = 150)
    {
        Main.Optimization.MaxIterations = iterations;
        Main.Optimization.Runs = 1;
        await Main.Optimization.RunAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); }
        catch (IOException) { /* temp cleanup is best effort */ }
    }
}
