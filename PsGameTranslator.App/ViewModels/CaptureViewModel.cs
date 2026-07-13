using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Capture;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

public sealed class CaptureViewModel : ObservableObject
{
    private readonly IWindowCaptureService _windowCaptureService;
    private readonly ActiveGameCoordinator _activeGameCoordinator;
    private readonly GameCoverService _gameCoverService;
    private readonly ILogger<CaptureViewModel> _logger;
    private string _statusText = "Click Refresh to list visible windows.";
    private string _recognizedGameTitle = string.Empty;
    private CapturedWindow? _selectedWindow;
    private BitmapSource? _previewImageSource;
    private BitmapSource? _coverImageSource;
    private GameGlossaryInfo? _pendingGameCandidate;
    private GameGlossaryInfo? _selectedManualGame;
    private readonly AsyncRelayCommand _captureCommand;
    private readonly AsyncRelayCommand _identifyFromScreenshotCommand;
    private readonly AsyncRelayCommand _confirmPendingGameCommand;
    private readonly AsyncRelayCommand _rejectPendingGameCommand;
    private readonly AsyncRelayCommand _loadManualGameCommand;

    public CaptureViewModel(
        IWindowCaptureService windowCaptureService,
        ActiveGameCoordinator activeGameCoordinator,
        GameCoverService gameCoverService,
        ILogger<CaptureViewModel> logger)
    {
        _windowCaptureService = windowCaptureService;
        _activeGameCoordinator = activeGameCoordinator;
        _gameCoverService = gameCoverService;
        _logger = logger;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _captureCommand = new AsyncRelayCommand(async () => await CaptureScreenshotAsync(), () => _selectedWindow is not null);
        _identifyFromScreenshotCommand = new AsyncRelayCommand(IdentifyFromScreenshotAsync, () => _selectedWindow is not null);
        _confirmPendingGameCommand = new AsyncRelayCommand(ConfirmPendingGameAsync, () => _pendingGameCandidate is not null);
        _rejectPendingGameCommand = new AsyncRelayCommand(RejectPendingGameAsync, () => _pendingGameCandidate is not null);
        _loadManualGameCommand = new AsyncRelayCommand(LoadManualGameAsync, () => _selectedManualGame is not null);
        _selectedManualGame = ManualGameOptions.FirstOrDefault();
    }

    public ObservableCollection<CapturedWindow> Windows { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand CaptureCommand => _captureCommand;

    /// <summary>Manual "identify from screenshot" — for PS5 Remote Play / YouTube
    /// captures where the window title match never fires automatically.</summary>
    public ICommand IdentifyFromScreenshotCommand => _identifyFromScreenshotCommand;

    /// <summary>
    /// A game the vision model guessed and matched to a built-in glossary, but
    /// has NOT been loaded yet — small vision models get this wrong often
    /// enough (Bloodborne mistaken for Elden Ring, etc.) that the human has to
    /// approve it first. Null when there is nothing awaiting confirmation.
    /// </summary>
    public GameGlossaryInfo? PendingGameCandidate
    {
        get => _pendingGameCandidate;
        private set
        {
            if (SetProperty(ref _pendingGameCandidate, value))
            {
                _confirmPendingGameCommand.NotifyCanExecuteChanged();
                _rejectPendingGameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ICommand ConfirmPendingGameCommand => _confirmPendingGameCommand;
    public ICommand RejectPendingGameCommand => _rejectPendingGameCommand;

    /// <summary>
    /// Manual override, always available regardless of what the vision model
    /// guessed (or whether it ran at all): a small vision model gets confused
    /// often enough on visually-similar franchises (AC Shadows recognized as
    /// Watch Dogs Legion, etc.) that the user needs a direct way to just pick
    /// the right game themselves.
    /// </summary>
    public static IReadOnlyList<GameGlossaryInfo> ManualGameOptions { get; } = GameGlossaryCatalog.Games;

    public GameGlossaryInfo? SelectedManualGame
    {
        get => _selectedManualGame;
        set
        {
            if (SetProperty(ref _selectedManualGame, value))
                _loadManualGameCommand.NotifyCanExecuteChanged();
        }
    }

    public ICommand LoadManualGameCommand => _loadManualGameCommand;

    public CapturedWindow? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                OnPropertyChanged(nameof(IsWindowSelected));
                _captureCommand.NotifyCanExecuteChanged();
                _identifyFromScreenshotCommand.NotifyCanExecuteChanged();

                // Grab a thumbnail immediately on selection so the Home page hero
                // card has a "cover photo" of the game without the user having to
                // press a separate capture button.
                if (value is not null)
                    _ = MatchActiveGameThenCaptureAsync(value.Title);
            }
        }
    }

    public bool IsWindowSelected => _selectedWindow is not null;

    public BitmapSource? PreviewImageSource
    {
        get => _previewImageSource;
        private set => SetProperty(ref _previewImageSource, value);
    }

    /// <summary>Steam header art for the active game, shown as the Home hero
    /// background. Null until a game is matched and its cover downloaded.</summary>
    public BitmapSource? CoverImageSource
    {
        get => _coverImageSource;
        private set => SetProperty(ref _coverImageSource, value);
    }

    /// <summary>Name last used to look up cover art, to avoid re-fetching the
    /// same game's header on every reselection.</summary>
    public string RecognizedGameTitle
    {
        get => _recognizedGameTitle;
        private set => SetProperty(ref _recognizedGameTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private async Task RefreshAsync()
    {
        StatusText = "Refreshing window list...";

        try
        {
            var windows = await _windowCaptureService.GetAvailableWindowsAsync();

            Windows.Clear();
            foreach (var window in windows)
                Windows.Add(window);

            StatusText = $"{Windows.Count} visible windows found.";
            _logger.LogInformation("Capture view loaded {WindowCount} windows", Windows.Count);
        }
        catch (Exception exception)
        {
            StatusText = "The window list could not be refreshed.";
            _logger.LogError(exception, "Failed to refresh the window list");
        }
    }

    private async Task MatchActiveGameThenCaptureAsync(string windowTitle)
    {
        string? matchedGameName = null;
        try
        {
            var result = await _activeGameCoordinator.MatchAndActivateAsync(windowTitle);
            matchedGameName = result.GlossaryGameName;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "active_game_match_failed - window={Window}", windowTitle);
        }

        var pngBytes = await CaptureScreenshotAsync(matchedGameName);

        // Fetch Steam header art for the Home hero background. Use the matched
        // glossary name when known, otherwise the raw window title (Steam's
        // store search is fuzzy enough to resolve most real game titles).
        await SetRecognizedGameAsync(matchedGameName ?? windowTitle);

        // Window title didn't match any known game (Remote Play client title,
        // a YouTube video title, etc.) — fall back to identifying the game from
        // the screenshot itself using a local vision model.
        if (matchedGameName is null && pngBytes is not null)
        {
            try
            {
                var visionResult = await _activeGameCoordinator.TryIdentifyByScreenshotAsync(pngBytes);
                if (visionResult.PendingConfirmation is not null)
                {
                    PendingGameCandidate = visionResult.PendingConfirmation;
                    StatusText = $"Bu ekran goruntusu {visionResult.PendingConfirmation.DisplayName} gibi gorunuyor. Onaylar misiniz?";
                }
                else if (visionResult.RecognizedGameName is not null)
                {
                    StatusText = $"{visionResult.RecognizedGameName} tanindi (bu oyun icin hazir sozluk yok).";
                    await SetRecognizedGameAsync(visionResult.RecognizedGameName);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "active_game_vision_match_failed - window={Window}", windowTitle);
            }
        }
    }

    private async Task IdentifyFromScreenshotAsync()
    {
        if (_selectedWindow is null) return;

        StatusText = "Ekran goruntusunden oyun taniniyor (görsel model calisiyor)...";
        try
        {
            var pngBytes = await _windowCaptureService.CaptureAsync(_selectedWindow);
            PreviewImageSource = ToBitmapSource(pngBytes);

            var result = await _activeGameCoordinator.TryIdentifyByScreenshotAsync(pngBytes);
            if (result.PendingConfirmation is not null)
            {
                PendingGameCandidate = result.PendingConfirmation;
                StatusText = $"Bu ekran goruntusu {result.PendingConfirmation.DisplayName} gibi gorunuyor. Onaylar misiniz?";
            }
            else
            {
                StatusText = result.RecognizedGameName is not null
                    ? $"{result.RecognizedGameName} tanindi (bu oyun icin hazir sozluk yok)."
                    : "Oyun taninamadi. Kucuk gorsel modeller (gemma3:4b) az bilinen veya benzer gorunumlu oyunlarda yanilabiliyor.";
            }
        }
        catch (Exception exception)
        {
            StatusText = "Gorsel tanima basarisiz oldu.";
            _logger.LogWarning(exception, "manual_vision_identify_failed - window={Window}", _selectedWindow.Title);
        }
    }

    private async Task ConfirmPendingGameAsync()
    {
        if (_pendingGameCandidate is null) return;
        var candidate = _pendingGameCandidate;

        try
        {
            var glossaryName = await _activeGameCoordinator.ConfirmPendingGameAsync(candidate);
            StatusText = glossaryName is not null
                ? $"{glossaryName} onaylandi — sozluk yuklendi."
                : "Sozluk yuklenemedi.";
            await SetRecognizedGameAsync(glossaryName ?? candidate.DisplayName);
        }
        catch (Exception exception)
        {
            StatusText = "Sozluk yuklenirken hata olustu.";
            _logger.LogWarning(exception, "confirm_pending_game_failed - candidate={Candidate}", candidate.DisplayName);
        }
        finally
        {
            PendingGameCandidate = null;
        }
    }

    private Task RejectPendingGameAsync()
    {
        var rejected = _pendingGameCandidate?.DisplayName;
        PendingGameCandidate = null;
        StatusText = rejected is not null ? $"{rejected} reddedildi." : StatusText;
        return Task.CompletedTask;
    }

    private async Task LoadManualGameAsync()
    {
        if (_selectedManualGame is null) return;
        var game = _selectedManualGame;

        try
        {
            var glossaryName = await _activeGameCoordinator.ConfirmPendingGameAsync(game);
            StatusText = glossaryName is not null
                ? $"{glossaryName} el ile secildi — sozluk yuklendi."
                : "Sozluk yuklenemedi.";
            PendingGameCandidate = null;
        }
        catch (Exception exception)
        {
            StatusText = "Sozluk yuklenirken hata olustu.";
            _logger.LogWarning(exception, "manual_game_load_failed - game={Game}", game.DisplayName);
        }
    }

    private async Task<byte[]?> CaptureScreenshotAsync(string? matchedGameName = null)
    {
        if (_selectedWindow is null)
            return null;

        var gamePrefix = matchedGameName is not null ? $"{matchedGameName} algilandi (sozluk yuklendi). " : "";
        StatusText = $"{gamePrefix}Capturing \"{_selectedWindow.Title}\"...";

        try
        {
            var pngBytes = await _windowCaptureService.CaptureAsync(_selectedWindow);

            var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
            Directory.CreateDirectory(samplesDir);
            var outputPath = Path.Combine(samplesDir, "capture_test.png");

            await File.WriteAllBytesAsync(outputPath, pngBytes);
            _logger.LogInformation("Screenshot saved to {Path}", outputPath);

            PreviewImageSource = ToBitmapSource(pngBytes);
            StatusText = $"{gamePrefix}Saved → {outputPath}";
            return pngBytes;
        }
        catch (Exception exception)
        {
            StatusText = "Screenshot capture failed.";
            _logger.LogError(exception, "Failed to capture window handle {Handle}", _selectedWindow.Handle);
            return null;
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

    private async Task SetRecognizedGameAsync(string? gameTitle)
    {
        if (string.IsNullOrWhiteSpace(gameTitle)) return;
        // Same game already resolved a cover — don't hit Steam again.
        if (string.Equals(gameTitle, RecognizedGameTitle, StringComparison.OrdinalIgnoreCase)
            && CoverImageSource is not null)
            return;

        RecognizedGameTitle = gameTitle;
        try
        {
            var cover = await _gameCoverService.GetCoverAsync(gameTitle);
            if (cover is not null) CoverImageSource = cover;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "game_cover_fetch_failed - game={Game}", gameTitle);
        }
    }
}
