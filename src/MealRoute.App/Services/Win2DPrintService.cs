using MealRoute.App.ViewModels.Services;
using MealRoute.Core.Evaluation;
using MealRoute.Core.Reports;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Graphics.Printing;

namespace MealRoute.App.Services;

/// <summary>
/// Prints one route sheet per page with Win2D's CanvasPrintDocument through the window's
/// PrintManager (PrintManagerInterop, required for unpackaged WinUI 3 apps).
/// </summary>
internal sealed class Win2DPrintService(Window window, IMessageService messages) : IPrintService
{
    private CanvasPrintDocument? _document;
    private IReadOnlyList<RouteSheet> _sheets = Array.Empty<RouteSheet>();

    public async Task PrintAsync(IReadOnlyList<RouteSheet> sheets)
    {
        _sheets = sheets;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            var manager = PrintManagerInterop.GetForWindow(hwnd);
            manager.PrintTaskRequested += OnPrintTaskRequested;
            try { await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd); }
            finally { manager.PrintTaskRequested -= OnPrintTaskRequested; }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            await messages.ShowErrorAsync("無法列印", $"此電腦目前無法開啟列印對話框（{ex.Message}）。請改用「匯出 CSV」或「匯出 GeoJSON」。");
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var task = args.Request.CreatePrintTask("MealRoute 路線單", src =>
        {
            _document?.Dispose();
            _document = new CanvasPrintDocument();
            _document.Preview += (doc, e) => DrawPage(e.DrawingSession, (int)e.PageNumber - 1, e.PrintTaskOptions);
            _document.PrintTaskOptionsChanged += (doc, e) =>
            {
                doc.SetPageCount((uint)Math.Max(1, _sheets.Count));
                doc.InvalidatePreview();
            };
            _document.Print += (doc, e) =>
            {
                for (var i = 0; i < _sheets.Count; i++)
                {
                    using var ds = e.CreateDrawingSession();
                    DrawPage(ds, i, e.PrintTaskOptions);
                }
            };
            src.SetSource(_document);
        });
        task.Options.Orientation = PrintOrientation.Portrait;
    }

    private void DrawPage(CanvasDrawingSession ds, int index, PrintTaskOptions options)
    {
        if (index < 0 || index >= _sheets.Count) return;
        var sheet = _sheets[index];
        var page = options.GetPageDescription(0);
        var margin = 48f;
        var y = margin;
        using var title = new CanvasTextFormat { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        using var body = new CanvasTextFormat { FontSize = 11 };
        ds.Clear(Colors.White);
        ds.DrawText($"路線單　{sheet.Driver}（{sheet.VehicleId}）", margin, y, Colors.Black, title);
        y += 34;
        ds.DrawText($"出發 {TimeOfDay.Format(sheet.Departure)}　返回 {TimeOfDay.Format(sheet.EndArrival)}　餐盒 {sheet.TotalMeals}　保溫箱 {sheet.TotalBoxes}　行車 {TimeOfDay.FormatDuration(sheet.TravelSeconds)}", margin, y, Colors.Black, body);
        y += 26;
        float[] cols = { 0, 34, 150, 205, 300, 350 };
        string[] head = { "站序", "姓名", "到達", "時間窗", "餐/箱", "地址・備註" };
        for (var c = 0; c < head.Length; c++) ds.DrawText(head[c], margin + cols[c], y, Colors.Gray, body);
        y += 18;
        ds.DrawLine(margin, y, (float)page.PageSize.Width - margin, y, Colors.Gray);
        y += 4;
        foreach (var r in sheet.Rows)
        {
            if (y > page.PageSize.Height - margin) break;
            string[] cells =
            {
                r.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Name, TimeOfDay.Format(r.Arrival),
                $"{TimeOfDay.Format(r.WindowStart)}–{TimeOfDay.Format(r.WindowEnd)}", $"{r.Meals}/{r.Boxes}",
                r.Address + (r.Note.Length > 0 ? "　" + r.Note : "") + (r.BreakAfter ? "　★服務後休息 30 分鐘" : ""),
            };
            for (var c = 0; c < cells.Length; c++) ds.DrawText(cells[c], margin + cols[c], y, Colors.Black, body);
            y += 20;
        }
    }
}
