using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace MealRoute.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMapDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        args.DrawingSession.DrawText("MealRoute", 20, 20, Colors.Gray);
    }
}
