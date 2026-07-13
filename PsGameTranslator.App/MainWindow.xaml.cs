using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App;

public partial class MainWindow : Window
{
    // Rubber-band drag state — pure UI, not in the ViewModel.
    private Point _dragStart;
    private bool _isDragging;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // ── Region-selection mouse handlers ─────────────────────────────────────
    //
    // The Canvas (SelectionOverlay) lives inside a Viewbox whose inner Grid is
    // sized to ScreenshotPixelWidth × ScreenshotPixelHeight.  The Viewbox
    // applies a uniform scale to fit the available display area, but
    // e.GetPosition(SelectionOverlay) always returns coordinates in the
    // Canvas's own space — which is identical to image-pixel coordinates.
    // No manual coordinate conversion is therefore required.

    private void SelectionOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var vm = RegionViewModel();
        if (vm.ScreenshotPixelWidth <= 0 || vm.ScreenshotPixelHeight <= 0) return;

        _dragStart = e.GetPosition(SelectionOverlay);
        _isDragging = true;
        SelectionOverlay.CaptureMouse();

        Canvas.SetLeft(SelectionRect, _dragStart.X);
        Canvas.SetTop(SelectionRect, _dragStart.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;

        vm.NotifySelectionStarted();
    }

    private void SelectionOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var vm = RegionViewModel();
        var current = e.GetPosition(SelectionOverlay);

        // Clamp to canvas bounds (CaptureMouse lets events fire outside).
        double cx = Math.Clamp(current.X, 0, vm.ScreenshotPixelWidth);
        double cy = Math.Clamp(current.Y, 0, vm.ScreenshotPixelHeight);

        double x = Math.Min(_dragStart.X, cx);
        double y = Math.Min(_dragStart.Y, cy);
        double w = Math.Abs(cx - _dragStart.X);
        double h = Math.Abs(cy - _dragStart.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width  = w;
        SelectionRect.Height = h;

        vm.UpdateDebugMouse(cx, cy);
    }

    private void SelectionOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        SelectionOverlay.ReleaseMouseCapture();

        // Canvas coordinates are already image-pixel coordinates.
        int imgX = (int)Canvas.GetLeft(SelectionRect);
        int imgY = (int)Canvas.GetTop(SelectionRect);
        int imgW = (int)SelectionRect.Width;
        int imgH = (int)SelectionRect.Height;

        var vm = RegionViewModel();

        // Compute the display scale the Viewbox actually applied (for debug).
        int pw = vm.ScreenshotPixelWidth;
        int ph = vm.ScreenshotPixelHeight;
        if (pw > 0 && ph > 0 && imgW > 0 && imgH > 0)
        {
            double vbW = SelectionViewbox.ActualWidth;
            double vbH = SelectionViewbox.ActualHeight;
            double scale = Math.Min(vbW / pw, vbH / ph);
            vm.UpdateDebugConversion(pw * scale, ph * scale, scale, imgX, imgY, imgW, imgH);
            vm.SetRegion(imgX, imgY, imgW, imgH);
        }
    }

    private RegionViewModel RegionViewModel() =>
        ((MainViewModel)DataContext).Region;
}
