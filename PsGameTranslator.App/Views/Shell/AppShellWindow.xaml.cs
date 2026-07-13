using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PsGameTranslator.App.ViewModels;
using PsGameTranslator.App.ViewModels.Shell;
using PsGameTranslator.App.ViewModels.User;

namespace PsGameTranslator.App.Views.Shell;

public partial class AppShellWindow : Window
{
    // Subtitle-region rubber-band selection state. The Canvas/Rectangle/Viewbox
    // live inside the CapturePageViewModel DataTemplate, so they are found via
    // FindName on the event sender's NameScope rather than generated fields.
    private Point _regionDragStart;
    private bool _isDraggingRegion;

    public AppShellWindow(AppShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        TryLoadWindowIcon();
        SourceInitialized += OnSourceInitialized;
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(iconUri);
        }
        catch
        {
            // A malformed/missing icon must never prevent the shell from opening.
        }
    }

    // WindowStyle="None" windows do not know about the taskbar when maximizing,
    // so WPF/Windows expands them over it. Hooking WM_GETMINMAXINFO and clamping
    // the maximize size/position to the monitor's work area is the standard fix.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaToMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaToMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var workArea = monitorInfo.rcWork;
        var monitorArea = monitorInfo.rcMonitor;

        mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
        mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
        mmi.ptMaxSize.X = workArea.Right - workArea.Left;
        mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // ── Subtitle region rubber-band selection ────────────────────────────────

    // FindName is unreliable for elements nested inside an *implicit*
    // DataTemplate (matched by DataType, not x:Key) — the NameScope registered
    // by the template loader does not always resolve sibling names from a
    // non-root descendant. The Rectangle is the Canvas's only child and the
    // Viewbox is two Parent-hops up, so walk the tree directly instead.
    private static Rectangle? FindSelectionRect(Canvas canvas) =>
        canvas.Children.Count > 0 ? canvas.Children[0] as Rectangle : null;

    private static Viewbox? FindSelectionViewbox(Canvas canvas) =>
        (canvas.Parent as FrameworkElement)?.Parent as Viewbox;

    private void SelectionOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Canvas canvas) return;
        if (canvas.DataContext is not CapturePageViewModel page) return;
        var region = page.Region;
        if (region.ScreenshotPixelWidth <= 0 || region.ScreenshotPixelHeight <= 0) return;
        if (FindSelectionRect(canvas) is not { } rect) return;

        _regionDragStart = e.GetPosition(canvas);
        _isDraggingRegion = true;
        canvas.CaptureMouse();

        Canvas.SetLeft(rect, _regionDragStart.X);
        Canvas.SetTop(rect, _regionDragStart.Y);
        rect.Width = 0;
        rect.Height = 0;
        rect.Visibility = Visibility.Visible;

        region.NotifySelectionStarted();
    }

    private void SelectionOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRegion) return;
        if (sender is not Canvas canvas) return;
        if (canvas.DataContext is not CapturePageViewModel page) return;
        var region = page.Region;
        if (FindSelectionRect(canvas) is not { } rect) return;

        var current = e.GetPosition(canvas);
        double cx = Math.Clamp(current.X, 0, region.ScreenshotPixelWidth);
        double cy = Math.Clamp(current.Y, 0, region.ScreenshotPixelHeight);

        double x = Math.Min(_regionDragStart.X, cx);
        double y = Math.Min(_regionDragStart.Y, cy);
        double w = Math.Abs(cx - _regionDragStart.X);
        double h = Math.Abs(cy - _regionDragStart.Y);

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = w;
        rect.Height = h;

        region.UpdateDebugMouse(cx, cy);
    }

    private void SelectionOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingRegion) return;
        if (sender is not Canvas canvas) return;
        if (canvas.DataContext is not CapturePageViewModel page) return;
        var region = page.Region;
        if (FindSelectionRect(canvas) is not { } rect) return;

        _isDraggingRegion = false;
        canvas.ReleaseMouseCapture();

        int imgX = (int)Canvas.GetLeft(rect);
        int imgY = (int)Canvas.GetTop(rect);
        int imgW = (int)rect.Width;
        int imgH = (int)rect.Height;

        int pw = region.ScreenshotPixelWidth;
        int ph = region.ScreenshotPixelHeight;
        if (pw <= 0 || ph <= 0 || imgW <= 0 || imgH <= 0) return;

        if (FindSelectionViewbox(canvas) is { } viewbox)
        {
            double vbW = viewbox.ActualWidth;
            double vbH = viewbox.ActualHeight;
            double scale = Math.Min(vbW / pw, vbH / ph);
            region.UpdateDebugConversion(pw * scale, ph * scale, scale, imgX, imgY, imgW, imgH);
        }

        region.SetRegion(imgX, imgY, imgW, imgH);
    }

    // PasswordBox.Password is intentionally not bindable in WPF (avoids the
    // password ending up in a binding/memory dump) — this is the standard
    // code-behind workaround.
    private void TrainingPinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: TrainingViewModel training } box)
            training.PinAttempt = box.Password;
    }
}
