using MealRoute.App.ViewModels.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MealRoute.App.Services;

internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }
}

/// <summary>File pickers for an unpackaged app (initialised with the window handle).</summary>
internal sealed class WinUiFileDialogService(Window window) : IFileDialogService
{
    public async Task<string?> PickOpenFileAsync(string title, string filters)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var f in filters.Split(';', StringSplitOptions.RemoveEmptyEntries)) picker.FileTypeFilter.Add(f);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string filters)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        var exts = filters.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        picker.FileTypeChoices.Add(title, exts);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

internal sealed class ContentDialogMessageService(Func<XamlRoot?> root) : IMessageService
{
    public async Task ShowErrorAsync(string title, string message)
    {
        if (root() is not { } xamlRoot) return;
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "確定", XamlRoot = xamlRoot };
        await dialog.ShowAsync();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (root() is not { } xamlRoot) return false;
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = "確定", CloseButtonText = "取消", XamlRoot = xamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
