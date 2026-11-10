using CommunityToolkit.Mvvm.ComponentModel;

namespace MealRoute.App.ViewModels;

/// <summary>
/// Single source of truth for what is selected; the map, the timeline and the lists all bind to
/// it, which is what links clicks between them.
/// </summary>
public sealed partial class SelectionService : ObservableObject
{
    [ObservableProperty]
    private int? _selectedStop;

    [ObservableProperty]
    private int? _selectedVehicle;

    /// <summary>Selects a stop and the vehicle serving it (null vehicle when unserved).</summary>
    public void SelectStop(int? stop, int? vehicle)
    {
        SelectedStop = stop;
        SelectedVehicle = vehicle;
    }

    public void SelectVehicle(int? vehicle)
    {
        SelectedStop = null;
        SelectedVehicle = vehicle;
    }

    public void Clear() => SelectStop(null, null);
}

/// <summary>Distinct, colour-blind-friendly-ish colours per vehicle (ARGB), shared by map and timeline.</summary>
public static class VehiclePalette
{
    private static readonly uint[] Colors =
    {
        0xFF1F77B4, 0xFFFF7F0E, 0xFF2CA02C, 0xFFD62728, 0xFF9467BD, 0xFF8C564B, 0xFFE377C2, 0xFF17BECF,
        0xFFBCBD22, 0xFF393B79, 0xFF637939, 0xFF8C6D31, 0xFF843C39, 0xFF7B4173, 0xFF3182BD, 0xFFE6550D,
    };

    public static uint ColorOf(int vehicle) => Colors[vehicle % Colors.Length];
}
