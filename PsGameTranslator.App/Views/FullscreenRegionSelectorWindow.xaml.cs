using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.App.Views;

public partial class FullscreenRegionSelectorWindow : Window
{
    private Point _dragStart;
    private bool _isDragging;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    public CaptureRegion? SelectedRegion { get; private set; }

    public FullscreenRegionSelectorWindow(BitmapSource screenshot)
    {
        InitializeComponent();
        ScreenshotImage.Source = screenshot;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        RootCanvas.MouseLeftButtonDown += OnMouseDown;
        RootCanvas.MouseMove += OnMouseMove;
        RootCanvas.MouseLeftButtonUp += OnMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _dpiScaleX = src.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = src.CompositionTarget.TransformToDevice.M22;
        }

        var w = ActualWidth;
        var h = ActualHeight;

        ScreenshotImage.Width = w;
        ScreenshotImage.Height = h;
        DarkOverlay.Width = w;
        DarkOverlay.Height = h;

        HintLabel.UpdateLayout();
        Canvas.SetLeft(HintLabel, (w - HintLabel.ActualWidth) / 2);
        Canvas.SetTop(HintLabel, 24);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(RootCanvas);
        _isDragging = true;
        RootCanvas.CaptureMouse();
        HintLabel.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Visible;
        CoordLabel.Visibility = Visibility.Visible;
        UpdateSelectionRect(_dragStart);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
            UpdateSelectionRect(e.GetPosition(RootCanvas));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        RootCanvas.ReleaseMouseCapture();
        Confirm(e.GetPosition(RootCanvas));
    }

    private void UpdateSelectionRect(Point current)
    {
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.X);
        var h = Math.Abs(current.Y - _dragStart.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = Math.Max(0, w);
        SelectionBorder.Height = Math.Max(0, h);

        var px = (int)(x * _dpiScaleX);
        var py = (int)(y * _dpiScaleY);
        var pw = (int)(w * _dpiScaleX);
        var ph = (int)(h * _dpiScaleY);
        CoordText.Text = $"X:{px}  Y:{py}  {pw}×{ph} px";

        var labelX = Math.Min(current.X + 14, ActualWidth - 200);
        var labelY = Math.Min(current.Y + 14, ActualHeight - 40);
        Canvas.SetLeft(CoordLabel, Math.Max(0, labelX));
        Canvas.SetTop(CoordLabel, Math.Max(0, labelY));
    }

    private void Confirm(Point end)
    {
        var x = (int)(Math.Min(_dragStart.X, end.X) * _dpiScaleX);
        var y = (int)(Math.Min(_dragStart.Y, end.Y) * _dpiScaleY);
        var w = (int)(Math.Abs(end.X - _dragStart.X) * _dpiScaleX);
        var h = (int)(Math.Abs(end.Y - _dragStart.Y) * _dpiScaleY);

        if (w < 20 || h < 10)
            return;

        SelectedRegion = new CaptureRegion { X = x, Y = y, Width = w, Height = h };
        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                Close();
                break;

            case Key.Enter when SelectionBorder.Visibility == Visibility.Visible:
                var endX = Canvas.GetLeft(SelectionBorder) + SelectionBorder.Width;
                var endY = Canvas.GetTop(SelectionBorder) + SelectionBorder.Height;
                Confirm(new Point(endX, endY));
                break;
        }
    }
}
