namespace PsGameTranslator.Core.Translation;

public sealed record GameGlossaryInfo(string DisplayName, string RelativeFilePath, string[] MatchKeywords);

/// <summary>
/// Built-in per-game glossary files shipped under config/dictionaries/game/.
/// The Glossary page lets the user pick one of these and load it straight
/// into the GameSpecific dictionary slot, instead of always browsing for a file.
/// MatchKeywords are used by ActiveGameCoordinator to auto-detect the game
/// from the captured window title and load the right glossary automatically.
/// </summary>
public static class GameGlossaryCatalog
{
    public static readonly IReadOnlyList<GameGlossaryInfo> Games =
    [
        new("Assassin's Creed Shadows", "ac_shadows_en_tr.json", ["Assassin's Creed Shadows", "AC Shadows"]),
        new("Red Dead Redemption 2", "rdr2_en_tr.json", ["Red Dead Redemption 2", "Red Dead Redemption II", "RDR2"]),
        new("Elden Ring", "elden_ring_en_tr.json", ["Elden Ring"]),
        new("The Witcher 3: Wild Hunt", "witcher3_en_tr.json", ["Witcher 3", "Wild Hunt"]),
        new("Final Fantasy VII Rebirth", "final_fantasy_en_tr.json", ["Final Fantasy VII Rebirth", "FF7 Rebirth", "FFVII Rebirth", "Final Fantasy 7 Rebirth"]),
        new("The Elder Scrolls V: Skyrim", "elder_scrolls_en_tr.json", ["Skyrim"]),
        new("Alan Wake 2", "alan_wake2_en_tr.json", ["Alan Wake 2", "Alan Wake II"]),
        new("Hogwarts Legacy", "hogwarts_legacy_en_tr.json", ["Hogwarts Legacy"]),
    ];

    public static string ResolveFullPath(GameGlossaryInfo game) =>
        Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "game", game.RelativeFilePath);

    /// <summary>Finds the first catalog entry whose MatchKeywords appear in the window title.</summary>
    public static GameGlossaryInfo? MatchByWindowTitle(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        return Games.FirstOrDefault(g =>
            g.MatchKeywords.Any(k => windowTitle.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }
}
