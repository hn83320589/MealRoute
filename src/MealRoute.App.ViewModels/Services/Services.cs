using System.Text.Json;
using MealRoute.Core.Reports;

namespace MealRoute.App.ViewModels.Services;

/// <summary>File pickers (implemented by the WinUI app).</summary>
public interface IFileDialogService
{
    /// <param name="filters">Extension list like ".json;.geojson".</param>
    Task<string?> PickOpenFileAsync(string title, string filters);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string filters);
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs inline (tests, or when already on the UI thread).</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public interface IMessageService
{
    Task ShowErrorAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}

public interface IPrintService
{
    Task PrintAsync(IReadOnlyList<RouteSheet> sheets);
}

public sealed record AppSettings(string? LastProjectPath = null, double OptimizerSeconds = 60, int OptimizerRuns = 4);

/// <summary>Settings as JSON, by default in %LocalAppData%\MealRoute\settings.json.</summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MealRoute", "settings.json");
    }

    public string Path { get; }

    /// <summary>Missing or unreadable settings fall back to defaults (settings are a convenience).</summary>
    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Ignoring unreadable settings file {Path}: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
    }
}
