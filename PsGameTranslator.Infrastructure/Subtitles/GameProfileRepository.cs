using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Subtitles;

/// <summary>
/// Built-in OCR filtering profiles per game. Falls back to the default English
/// profile when the requested name is unknown.
/// </summary>
public sealed class GameProfileRepository
{
    public const string DefaultProfileName = "Default English Game";
    public const string Rdr2ProfileName = "Red Dead Redemption 2";

    private readonly List<GameProfile> _profiles;

    public GameProfileRepository()
    {
        _profiles =
        [
            new GameProfile
            {
                Name = DefaultProfileName,
                Genre = "generic",
                EnableSubtitleLineFiltering = true,
                SubtitleBandTopPercent = 0.00,
                SubtitleBandBottomPercent = 0.55,
                HudNoisePatterns = [],
                ProtectedTerms = [],
                TutorialPromptPatterns = [],
            },
            new GameProfile
            {
                Name = Rdr2ProfileName,
                Genre = "action-adventure",
                WindowTitle = "Red Dead Redemption 2",
                EnableSubtitleLineFiltering = true,
                SubtitleBandTopPercent = 0.00,
                SubtitleBandBottomPercent = 0.55,
                HudNoisePatterns = [],
                // O'Driscoll is a proper name, but tutorial lines like
                // "Press R near the O'Driscoll to hogtie them" are still rejected
                // because they match multiple prompt indicators (Press + near the + hogtie).
                ProtectedTerms = ["O'Driscoll", "Dead Eye", "Arthur", "Dutch"],
                TutorialPromptPatterns =
                [
                    "Press",
                    "near the",
                    "hogtie",
                    "hold",
                    "mount",
                    "loot",
                    "pick up",
                    "weapon wheel",
                    "Dead Eye",
                    "O'Driscoll",
                ],
            },
        ];
    }

    public IReadOnlyList<GameProfile> Profiles => _profiles;

    public GameProfile GetByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return _profiles[0];
        return _profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? _profiles[0];
    }
}
