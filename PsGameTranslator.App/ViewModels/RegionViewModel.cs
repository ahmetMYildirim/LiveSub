using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Views;
using PsGameTranslator.Capture;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Infrastructure.Region;

namespace PsGameTranslator.App.ViewModels;

public sealed class RegionViewModel : ObservableObject
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly IImageCropService _cropService;
    private readonly IRegionPersistenceService _persistenceService;
    private readonly ILogger<RegionViewModel> _logger;

    private BitmapSource? _screenshotSource;
    private BitmapSource? _croppedPreviewSource;
    private string _statusText = "Click 'Load Screenshot' to begin.";
    private CaptureRegion? _selectedRegion;
    private int _regionX, _regionY, _regionWidth, _regionHeight;
    private int _screenshotPixelWidth, _screenshotPixelHeight;
    private readonly AsyncRelayCommand _saveRegionCommand;
    private readonly AsyncRelayCommand _cropRegionCommand;
    private readonly AsyncRelayCommand _selectFullscreenCommand;

    // Debug display
    private string _dbgMouseUi  = "—";
    private string _dbgImgSize  = "—";
    private string _dbgScale    = "—";
    private string _dbgPixelRect = "—";

    private static readonly string ScreenshotPath =
        Path.Combine(AppContext.BaseDirectory, "samples", "capture_test.png");

    private static readonly string CroppedPath =
        Path.Combine(AppContext.BaseDirectory, "samples", "ocr_region.png");

    public RegionViewModel(
        IImageCropService cropService,
        IRegionPersistenceService persistenceService,
        ILogger<RegionViewModel> logger)
    {
        _cropService = cropService;
        _persistenceService = persistenceService;
        _logger = logger;

        LoadScreenshotCommand = new AsyncRelayCommand(LoadScreenshotAsync);
        _saveRegionCommand = new AsyncRelayCommand(SaveRegionAsync, () => _selectedRegion is not null);
        _cropRegionCommand = new AsyncRelayCommand(CropRegionAsync, () => _selectedRegion is not null);
        _selectFullscreenCommand = new AsyncRelayCommand(SelectRegionFullscreenAsync);
    }

    // ── Image source ────────────────────────────────────────────────────────

    public BitmapSource? ScreenshotSource
    {
        get => _screenshotSource;
        private set
        {
            if (SetProperty(ref _screenshotSource, value))
            {
                ScreenshotPixelWidth  = value?.PixelWidth  ?? 0;
                ScreenshotPixelHeight = value?.PixelHeight ?? 0;
                OnPropertyChanged(nameof(IsScreenshotLoaded));
                DbgImgSize = value is null
                    ? "—"
                    : $"{value.PixelWidth}×{value.PixelHeight} px";
            }
        }
    }

    /// <summary>Pixel width of the loaded screenshot (used to size the canvas in Viewbox).</summary>
    public int ScreenshotPixelWidth
    {
        get => _screenshotPixelWidth;
        private set => SetProperty(ref _screenshotPixelWidth, value);
    }

    /// <summary>Pixel height of the loaded screenshot (used to size the canvas in Viewbox).</summary>
    public int ScreenshotPixelHeight
    {
        get => _screenshotPixelHeight;
        private set => SetProperty(ref _screenshotPixelHeight, value);
    }

    public bool IsScreenshotLoaded => _screenshotSource is not null;

    public BitmapSource? CroppedPreviewSource
    {
        get => _croppedPreviewSource;
        private set => SetProperty(ref _croppedPreviewSource, value);
    }

    // ── Selected region ──────────────────────────────────────────────────────

    public int RegionX      { get => _regionX;      private set => SetProperty(ref _regionX, value); }
    public int RegionY      { get => _regionY;      private set => SetProperty(ref _regionY, value); }
    public int RegionWidth  { get => _regionWidth;  private set => SetProperty(ref _regionWidth, value); }
    public int RegionHeight { get => _regionHeight; private set => SetProperty(ref _regionHeight, value); }

    public bool HasRegion => _selectedRegion is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ── Debug display ────────────────────────────────────────────────────────

    public string DbgMouseUi   { get => _dbgMouseUi;   private set => SetProperty(ref _dbgMouseUi,   value); }
    public string DbgImgSize   { get => _dbgImgSize;   private set => SetProperty(ref _dbgImgSize,   value); }
    public string DbgScale     { get => _dbgScale;     private set => SetProperty(ref _dbgScale,     value); }
    public string DbgPixelRect { get => _dbgPixelRect; private set => SetProperty(ref _dbgPixelRect, value); }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand LoadScreenshotCommand      { get; }
    public ICommand SaveRegionCommand          => _saveRegionCommand;
    public ICommand CropRegionCommand          => _cropRegionCommand;
    public ICommand SelectFullscreenCommand    => _selectFullscreenCommand;

    // ── Methods called from code-behind ──────────────────────────────────────

    /// <summary>Updates the live mouse-position debug display.</summary>
    public void UpdateDebugMouse(double x, double y) =>
        DbgMouseUi = $"X={x:F0}, Y={y:F0}";

    /// <summary>Updates debug scale/rect fields after the user finishes drawing a selection.</summary>
    public void UpdateDebugConversion(double displayedW, double displayedH,
        double scale, int px, int py, int pw, int ph)
    {
        DbgScale     = $"scale={scale:F4}  displayed={displayedW:F1}×{displayedH:F1} px";
        DbgPixelRect = $"X={px}, Y={py}, W={pw}, H={ph}";
    }

    /// <summary>Notifies the ViewModel that the user started drawing a selection.</summary>
    public void NotifySelectionStarted()
    {
        StatusText = "Draw a rectangle to select the OCR region…";
        _logger.LogInformation("Region selection started");
    }

    /// <summary>Stores the final region in original image pixel coordinates.</summary>
    public void SetRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            StatusText = "Invalid region: width and height must be greater than zero.";
            _logger.LogWarning("Region rejected — invalid dimensions {W}×{H}", width, height);
            return;
        }

        _selectedRegion = new CaptureRegion { X = x, Y = y, Width = width, Height = height };
        RegionX = x;  RegionY = y;  RegionWidth = width;  RegionHeight = height;
        OnPropertyChanged(nameof(HasRegion));

        _saveRegionCommand.NotifyCanExecuteChanged();
        _cropRegionCommand.NotifyCanExecuteChanged();

        StatusText = $"Region selected: X={x}, Y={y}, {width}×{height} px";
        _logger.LogInformation(
            "Region selected: X={X}, Y={Y}, Width={Width}, Height={Height}",
            x, y, width, height);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task SelectRegionFullscreenAsync()
    {
        StatusText = "Ekran görüntüsü alınıyor…";

        // Hide the app window so it doesn't appear in the screenshot
        var mainWindow = System.Windows.Application.Current.MainWindow;
        mainWindow?.Hide();
        await Task.Delay(300); // wait for OS to fully hide the window

        BitmapSource screenshot;
        try
        {
            screenshot = await Task.Run(CaptureFullScreen);
        }
        catch (Exception ex)
        {
            StatusText = "Ekran görüntüsü alınamadı.";
            _logger.LogError(ex, "fullscreen_capture_failed");
            mainWindow?.Show();
            return;
        }

        var win = new FullscreenRegionSelectorWindow(screenshot);
        var confirmed = win.ShowDialog() == true;

        // Restore main window regardless of whether user confirmed
        mainWindow?.Show();
        mainWindow?.Activate();

        if (!confirmed || win.SelectedRegion is null)
        {
            StatusText = "Bölge seçimi iptal edildi.";
            return;
        }

        var r = win.SelectedRegion;
        SetRegion(r.X, r.Y, r.Width, r.Height);
        await SaveRegionAsync();
    }

    private static BitmapSource CaptureFullScreen()
    {
        // SM_CXSCREEN / SM_CYSCREEN return physical pixel dimensions
        var w = GetSystemMetrics(0);
        var h = GetSystemMetrics(1);

        using var bmp = new System.Drawing.Bitmap(w, h,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h));

        var hBmp = bmp.GetHbitmap();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBmp);
        }
    }

    private async Task LoadScreenshotAsync()
    {
        if (!File.Exists(ScreenshotPath))
        {
            StatusText = "No screenshot found. Capture a window first.";
            _logger.LogWarning("Screenshot not found at {Path}", ScreenshotPath);
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(ScreenshotPath);
            ScreenshotSource = ToBitmapSource(bytes);
            StatusText = "Screenshot loaded. Draw a rectangle to select the OCR region.";
            _logger.LogInformation("Screenshot loaded from {Path}", ScreenshotPath);
        }
        catch (Exception ex)
        {
            StatusText = "Failed to load screenshot.";
            _logger.LogError(ex, "Failed to load screenshot from {Path}", ScreenshotPath);
        }
    }

    private async Task SaveRegionAsync()
    {
        if (_selectedRegion is null) { StatusText = "No region selected."; return; }

        try
        {
            await _persistenceService.SaveAsync(_selectedRegion);
            StatusText = "Region saved to config/region.json.";
            _logger.LogInformation(
                "Region saved: X={X}, Y={Y}, W={W}, H={H}",
                _selectedRegion.X, _selectedRegion.Y,
                _selectedRegion.Width, _selectedRegion.Height);
        }
        catch (Exception ex)
        {
            StatusText = "Failed to save region.";
            _logger.LogError(ex, "Failed to save region");
        }
    }

    private async Task CropRegionAsync()
    {
        if (_selectedRegion is null) { StatusText = "No region selected."; return; }

        if (!File.Exists(ScreenshotPath))
        {
            StatusText = "No screenshot found. Capture a window first.";
            _logger.LogWarning("Screenshot not found at {Path}", ScreenshotPath);
            return;
        }

        StatusText = "Cropping region…";

        try
        {
            var pngBytes = await _cropService.CropAsync(ScreenshotPath, _selectedRegion);

            Directory.CreateDirectory(Path.GetDirectoryName(CroppedPath)!);
            await File.WriteAllBytesAsync(CroppedPath, pngBytes);

            CroppedPreviewSource = ToBitmapSource(pngBytes);
            StatusText = $"Cropped region saved → {CroppedPath}";
            _logger.LogInformation("Crop succeeded, saved to {Path}", CroppedPath);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            StatusText = $"Region is outside image bounds: {ex.Message}";
            _logger.LogError(ex, "Crop failed — region outside bounds");
        }
        catch (Exception ex)
        {
            StatusText = "Crop failed.";
            _logger.LogError(ex, "Failed to crop region from {Path}", ScreenshotPath);
        }
    }

    private static BitmapSource ToBitmapSource(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
