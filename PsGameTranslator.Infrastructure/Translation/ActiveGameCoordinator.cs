using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Subtitles;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed record ActiveGameMatchResult(
    string? GlossaryGameName,
    string? GameProfileName,
    string? RecognizedGameName = null,
    GameGlossaryInfo? PendingConfirmation = null)
{
    public bool AnyMatch => GlossaryGameName is not null || GameProfileName is not null;
}

/// <summary>
/// Single point of truth for "what game is the user currently capturing".
/// Previously the glossary's GameSpecific slot, the OCR filtering GameProfile,
/// and window capture were three independent systems the user had to sync by
/// hand. This coordinator matches the captured window's title against the
/// built-in glossary catalog (PsGameTranslator.Core.Translation.GameGlossaryCatalog)
/// and the OCR filtering profiles (GameProfileRepository), activating both in
/// one step whenever a window is selected for capture.
/// </summary>
public sealed class ActiveGameCoordinator
{
    private readonly GlossaryDictionaryManager _glossaryManager;
    private readonly GameProfileRepository _gameProfileRepository;
    private readonly SubtitleFilterSettings _subtitleFilterSettings;
    private readonly OllamaVisionGameIdentifier _visionIdentifier;
    private readonly TranslationSettings _translationSettings;
    private readonly ILogger<ActiveGameCoordinator> _logger;

    public event Action<ActiveGameMatchResult>? GameMatched;

    public ActiveGameCoordinator(
        GlossaryDictionaryManager glossaryManager,
        GameProfileRepository gameProfileRepository,
        SubtitleFilterSettings subtitleFilterSettings,
        OllamaVisionGameIdentifier visionIdentifier,
        TranslationSettings translationSettings,
        ILogger<ActiveGameCoordinator> logger)
    {
        _glossaryManager = glossaryManager;
        _gameProfileRepository = gameProfileRepository;
        _subtitleFilterSettings = subtitleFilterSettings;
        _visionIdentifier = visionIdentifier;
        _translationSettings = translationSettings;
        _logger = logger;
    }

    public async Task<ActiveGameMatchResult> MatchAndActivateAsync(string windowTitle)
    {
        string? glossaryName = null;

        var glossaryMatch = GameGlossaryCatalog.MatchByWindowTitle(windowTitle);
        if (glossaryMatch is not null)
            glossaryName = await ActivateGlossaryAsync(glossaryMatch, windowTitle);

        var profileName = MatchGameProfile(windowTitle);

        var result = new ActiveGameMatchResult(glossaryName, profileName);
        if (result.AnyMatch) GameMatched?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Fallback for capture sources where the window title is not a real game
    /// title (PS5 Remote Play, YouTube) — asks a local vision model to identify
    /// the game from a screenshot. Only call this after MatchAndActivateAsync
    /// has already failed to find a title-based match; a vision call takes
    /// seconds, so it should never run on every frame.
    ///
    /// Deliberately never auto-activates a glossary — a small vision model's
    /// free-text guess is not reliable enough to trust unattended (see
    /// OllamaVisionGameIdentifier's doc comment). When the guess happens to
    /// match a catalog title it comes back as PendingConfirmation; the caller
    /// must show it to the user and call ConfirmPendingGameAsync only if they
    /// agree.
    /// </summary>
    public async Task<ActiveGameMatchResult> TryIdentifyByScreenshotAsync(byte[] pngScreenshot)
    {
        if (!_translationSettings.EnableVisionGameDetection)
            return new ActiveGameMatchResult(null, null);

        var identification = await _visionIdentifier.IdentifyAsync(pngScreenshot);
        if (!identification.Success || identification.RecognizedGameName is null)
        {
            _logger.LogInformation(
                "active_game_vision_no_match - success={Success}, answer={Answer}",
                identification.Success, identification.RawModelAnswer);
            return new ActiveGameMatchResult(null, null);
        }

        if (identification.CatalogMatch is null)
        {
            // Vision model recognized a game, but it is not one we ship a glossary
            // for — still useful to surface the name, just nothing to auto-load.
            _logger.LogInformation(
                "active_game_vision_unmatched_catalog - recognized={Recognized}", identification.RecognizedGameName);
            return new ActiveGameMatchResult(null, null, identification.RecognizedGameName);
        }

        _logger.LogInformation(
            "active_game_vision_pending_confirmation - candidate={Candidate}", identification.CatalogMatch.DisplayName);
        return new ActiveGameMatchResult(null, null, identification.RecognizedGameName, identification.CatalogMatch);
    }

    /// <summary>Called once the user confirms a vision-guessed candidate in the UI.</summary>
    public async Task<string?> ConfirmPendingGameAsync(GameGlossaryInfo candidate)
    {
        var glossaryName = await ActivateGlossaryAsync(candidate, "(vision-confirmed)");
        if (glossaryName is null) return null;

        var profileName = MatchGameProfile(candidate.DisplayName);
        GameMatched?.Invoke(new ActiveGameMatchResult(glossaryName, profileName));
        return glossaryName;
    }

    private async Task<string?> ActivateGlossaryAsync(GameGlossaryInfo glossaryMatch, string sourceDescription)
    {
        var path = GameGlossaryCatalog.ResolveFullPath(glossaryMatch);
        var (count, error) = await _glossaryManager.LoadFromFileAsync(DictionarySlotKind.GameSpecific, path);
        if (error is not null)
        {
            _logger.LogWarning("active_game_glossary_load_failed - game={Game}, error={Error}",
                glossaryMatch.DisplayName, error);
            return null;
        }

        _glossaryManager.UseGameSpecific = true;
        _logger.LogInformation(
            "active_game_glossary_matched - source={Source}, game={Game}, terms={Count}",
            sourceDescription, glossaryMatch.DisplayName, count);
        return glossaryMatch.DisplayName;
    }

    private string? MatchGameProfile(string windowTitle)
    {
        var profileMatch = _gameProfileRepository.Profiles.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.WindowTitle) &&
            windowTitle.Contains(p.WindowTitle, StringComparison.OrdinalIgnoreCase));
        if (profileMatch is null ||
            string.Equals(_subtitleFilterSettings.ActiveGameProfileName, profileMatch.Name, StringComparison.OrdinalIgnoreCase))
            return null;

        _subtitleFilterSettings.ActiveGameProfileName = profileMatch.Name;
        _logger.LogInformation("active_game_profile_matched - window={Window}, profile={Profile}",
            windowTitle, profileMatch.Name);
        return profileMatch.Name;
    }
}
