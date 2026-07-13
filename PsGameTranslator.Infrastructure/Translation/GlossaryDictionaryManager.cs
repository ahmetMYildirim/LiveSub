using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public enum DictionarySlotKind
{
    Global = 0,
    RpgGenre = 1,
    CrpgGenre = 2,
    ActionRpgGenre = 3,
    JrpgGenre = 4,
    GameSpecific = 5,
    UserCorrections = 6,
}

public sealed class DictionarySlotInfo
{
    public DictionarySlotKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public int TermCount { get; init; }
    public DateTimeOffset? LoadedAt { get; init; }
    public string? LoadError { get; init; }
}

/// <summary>
/// Manages multiple glossary dictionary sources (global, genre, game, user) and
/// provides a merged, priority-ordered list for the translation post-processor.
/// </summary>
public sealed class GlossaryDictionaryManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private readonly UserGlossaryRepository _userRepo;
    private readonly ILogger<GlossaryDictionaryManager> _logger;
    private readonly object _gate = new();

    private readonly Dictionary<DictionarySlotKind, SlotState> _slots;
    private IReadOnlyList<GlossaryTerm>? _mergedCache;

    public event Action? Changed;

    // ── Enable/disable flags ──────────────────────────────────────────────────

    private bool _useGlobal = true;
    private bool _useRpg = true;
    private bool _useCrpg;
    private bool _useActionRpg;
    private bool _useJrpg;
    private bool _useGameSpecific;
    private bool _useUserCorrections = true;

    public bool UseGlobal
    {
        get => _useGlobal;
        set { if (_useGlobal == value) return; _useGlobal = value; Invalidate(); }
    }
    public bool UseRpg
    {
        get => _useRpg;
        set { if (_useRpg == value) return; _useRpg = value; Invalidate(); }
    }
    public bool UseCrpg
    {
        get => _useCrpg;
        set { if (_useCrpg == value) return; _useCrpg = value; Invalidate(); }
    }
    public bool UseActionRpg
    {
        get => _useActionRpg;
        set { if (_useActionRpg == value) return; _useActionRpg = value; Invalidate(); }
    }
    public bool UseJrpg
    {
        get => _useJrpg;
        set { if (_useJrpg == value) return; _useJrpg = value; Invalidate(); }
    }
    public bool UseGameSpecific
    {
        get => _useGameSpecific;
        set { if (_useGameSpecific == value) return; _useGameSpecific = value; Invalidate(); }
    }
    public bool UseUserCorrections
    {
        get => _useUserCorrections;
        set { if (_useUserCorrections == value) return; _useUserCorrections = value; Invalidate(); }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public int TotalTermCount => GetMergedTerms().Count;
    public int UserCorrectionCount => _userRepo.Terms.Count;

    public string ActiveDictionariesSummary
    {
        get
        {
            var parts = new List<string>();
            if (_useGlobal && SlotHasTerms(DictionarySlotKind.Global)) parts.Add("Global");
            if (_useRpg && SlotHasTerms(DictionarySlotKind.RpgGenre)) parts.Add("RPG");
            if (_useCrpg && SlotHasTerms(DictionarySlotKind.CrpgGenre)) parts.Add("CRPG");
            if (_useActionRpg && SlotHasTerms(DictionarySlotKind.ActionRpgGenre)) parts.Add("ActionRPG");
            if (_useJrpg && SlotHasTerms(DictionarySlotKind.JrpgGenre)) parts.Add("JRPG");
            if (_useGameSpecific && SlotHasTerms(DictionarySlotKind.GameSpecific)) parts.Add("Game");
            if (_useUserCorrections) parts.Add("User");
            return parts.Count > 0 ? string.Join(", ", parts) : "(none)";
        }
    }

    public GlossaryDictionaryManager(
        UserGlossaryRepository userRepo,
        ILogger<GlossaryDictionaryManager> logger)
    {
        _userRepo = userRepo;
        _logger = logger;

        _slots = Enum.GetValues<DictionarySlotKind>()
            .ToDictionary(k => k, k => new SlotState(k));

        _userRepo.Changed += () => { Invalidate(); };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns all terms from all enabled sources, sorted by priority desc.</summary>
    public IReadOnlyList<GlossaryTerm> GetMergedTerms()
    {
        lock (_gate)
        {
            if (_mergedCache is not null) return _mergedCache;

            var list = new List<GlossaryTerm>();
            if (_useUserCorrections)
            {
                foreach (var t in _userRepo.Terms)
                {
                    var clone = CloneWithSource(t, "UserCorrections");
                    if (clone.Priority == 0) clone.Priority = 100; // user corrections always win
                    list.Add(clone);
                }
            }
            AddSlotIfEnabled(list, DictionarySlotKind.GameSpecific, _useGameSpecific, 80);
            AddSlotIfEnabled(list, DictionarySlotKind.CrpgGenre, _useCrpg, 40);
            AddSlotIfEnabled(list, DictionarySlotKind.RpgGenre, _useRpg, 30);
            AddSlotIfEnabled(list, DictionarySlotKind.ActionRpgGenre, _useActionRpg, 30);
            AddSlotIfEnabled(list, DictionarySlotKind.JrpgGenre, _useJrpg, 30);
            AddSlotIfEnabled(list, DictionarySlotKind.Global, _useGlobal, 10);

            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            _mergedCache = list;
            return list;
        }
    }

    /// <summary>Returns terms whose SourceTerm matches the given source text.</summary>
    public IReadOnlyList<GlossaryTerm> GetRelevantTerms(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return [];
        return GetMergedTerms()
            .Where(t => TermMatchesSource(t, sourceText))
            .ToList();
    }

    /// <summary>
    /// Load a JSON glossary file into the specified slot.
    /// Returns (termCount, errorMessage).
    /// </summary>
    public async Task<(int Count, string? Error)> LoadFromFileAsync(
        DictionarySlotKind kind, string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(json, JsonOptions);
            if (terms is null) return (0, "File is not a valid glossary JSON array.");

            MigrateCategories(terms);
            lock (_gate)
            {
                _slots[kind] = new SlotState(kind)
                {
                    Terms = terms,
                    FilePath = filePath,
                    LoadedAt = DateTimeOffset.Now,
                };
                _mergedCache = null;
            }
            Changed?.Invoke();
            _logger.LogInformation("glossary_dict_loaded - kind={Kind}, count={Count}, file={File}",
                kind, terms.Count, filePath);
            return (terms.Count, null);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "glossary_dict_load_failed - kind={Kind}, file={File}",
                kind, filePath);
            lock (_gate) { _slots[kind] = new SlotState(kind) { LoadError = exception.Message }; }
            return (0, exception.Message);
        }
    }

    /// <summary>Reload the default built-in file paths for all slots (if they exist).</summary>
    public async Task ReloadDefaultPathsAsync()
    {
        var paths = new Dictionary<DictionarySlotKind, string>
        {
            [DictionarySlotKind.Global] = Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "global", "terms_en_tr.json"),
            [DictionarySlotKind.RpgGenre] = Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "genre", "rpg_en_tr.json"),
            [DictionarySlotKind.CrpgGenre] = Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "genre", "crpg_en_tr.json"),
            [DictionarySlotKind.ActionRpgGenre] = Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "genre", "action_rpg_en_tr.json"),
            [DictionarySlotKind.JrpgGenre] = Path.Combine(AppContext.BaseDirectory, "config", "dictionaries", "genre", "jrpg_en_tr.json"),
        };

        foreach (var (kind, path) in paths)
        {
            if (File.Exists(path))
                await LoadFromFileAsync(kind, path);
        }

        lock (_gate) { _mergedCache = null; }
        Changed?.Invoke();
    }

    public IReadOnlyList<DictionarySlotInfo> GetSlotInfos()
    {
        lock (_gate)
        {
            return _slots.Values.Select(s => new DictionarySlotInfo
            {
                Kind = s.Kind,
                Name = s.Kind.ToString(),
                FilePath = s.FilePath,
                IsEnabled = IsSlotEnabled(s.Kind),
                TermCount = s.Kind == DictionarySlotKind.UserCorrections
                    ? _userRepo.Terms.Count
                    : s.Terms.Count,
                LoadedAt = s.LoadedAt,
                LoadError = s.LoadError,
            }).ToList();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Invalidate()
    {
        lock (_gate) { _mergedCache = null; }
        Changed?.Invoke();
    }

    private bool IsSlotEnabled(DictionarySlotKind kind) => kind switch
    {
        DictionarySlotKind.Global => _useGlobal,
        DictionarySlotKind.RpgGenre => _useRpg,
        DictionarySlotKind.CrpgGenre => _useCrpg,
        DictionarySlotKind.ActionRpgGenre => _useActionRpg,
        DictionarySlotKind.JrpgGenre => _useJrpg,
        DictionarySlotKind.GameSpecific => _useGameSpecific,
        DictionarySlotKind.UserCorrections => _useUserCorrections,
        _ => false,
    };

    private bool SlotHasTerms(DictionarySlotKind kind)
    {
        lock (_gate) { return _slots[kind].Terms.Count > 0; }
    }

    private void AddSlotIfEnabled(
        List<GlossaryTerm> target,
        DictionarySlotKind kind,
        bool enabled,
        int defaultPriority)
    {
        if (!enabled) return;
        lock (_gate)
        {
            foreach (var t in _slots[kind].Terms)
            {
                var clone = CloneWithSource(t, kind.ToString());
                if (clone.Priority == 0) clone.Priority = defaultPriority;
                target.Add(clone);
            }
        }
    }

    private static GlossaryTerm CloneWithSource(GlossaryTerm t, string source) => new()
    {
        SourceTerm = t.SourceTerm,
        TargetTerm = t.TargetTerm,
        Categories = t.Categories,
        Category = t.Category,
        ShouldTranslate = t.ShouldTranslate,
        IsProtected = t.IsProtected,
        MatchMode = t.MatchMode,
        CaseSensitive = t.CaseSensitive,
        Priority = t.Priority,
        Notes = t.Notes,
        SourceDictionary = source,
    };

    private static bool TermMatchesSource(GlossaryTerm term, string sourceText) =>
        term.MatchMode switch
        {
            GlossaryMatchMode.Exact => sourceText.Equals(term.SourceTerm,
                term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
            GlossaryMatchMode.Phrase => sourceText.Contains(term.SourceTerm,
                term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
            GlossaryMatchMode.Contains => sourceText.Contains(term.SourceTerm,
                StringComparison.OrdinalIgnoreCase),
            _ => sourceText.Contains(term.SourceTerm, StringComparison.OrdinalIgnoreCase),
        };

    private static void MigrateCategories(List<GlossaryTerm> terms)
    {
        foreach (var t in terms)
        {
            t.Categories ??= [];
            if (t.Categories.Length == 0 && t.Category is { Length: > 0 })
            {
                t.Categories = [t.Category];
                t.Category = null;
            }
        }
    }

    // ── Nested state class ────────────────────────────────────────────────────

    private sealed class SlotState
    {
        public DictionarySlotKind Kind { get; }
        public List<GlossaryTerm> Terms { get; set; } = [];
        public string FilePath { get; set; } = string.Empty;
        public DateTimeOffset? LoadedAt { get; set; }
        public string? LoadError { get; set; }

        public SlotState(DictionarySlotKind kind) { Kind = kind; }
    }
}
