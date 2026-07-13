using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class UserGlossaryRepository
{
    private static readonly string DictionaryPath = Path.Combine(
        AppContext.BaseDirectory, "config", "dictionaries", "user", "corrections_en_tr.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private readonly ILogger<UserGlossaryRepository> _logger;
    private readonly object _gate = new();
    private List<GlossaryTerm>? _terms;

    public event Action? Changed;

    public UserGlossaryRepository(ILogger<UserGlossaryRepository> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<GlossaryTerm> Terms
    {
        get { lock (_gate) { return Load(); } }
    }

    public void Add(GlossaryTerm term)
    {
        lock (_gate)
        {
            Load().Add(term);
            Persist();
        }
        Changed?.Invoke();
    }

    public void Remove(GlossaryTerm term)
    {
        lock (_gate)
        {
            Load().RemoveAll(t =>
                string.Equals(t.SourceTerm, term.SourceTerm, StringComparison.OrdinalIgnoreCase));
            Persist();
        }
        Changed?.Invoke();
    }

    public void Update(GlossaryTerm updated)
    {
        lock (_gate)
        {
            var list = Load();
            var index = list.FindIndex(t =>
                string.Equals(t.SourceTerm, updated.SourceTerm, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) list[index] = updated;
            else list.Add(updated);
            Persist();
        }
        Changed?.Invoke();
    }

    public void Save() { lock (_gate) { Persist(); } }

    public void Reload()
    {
        lock (_gate) { _terms = null; Load(); }
        Changed?.Invoke();
    }

    private List<GlossaryTerm> Load()
    {
        if (_terms is not null) return _terms;
        try
        {
            if (File.Exists(DictionaryPath))
            {
                var json = File.ReadAllText(DictionaryPath, Encoding.UTF8);
                _terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(json, JsonOptions)
                    ?? [];
                MigrateCategories(_terms);
                _logger.LogInformation("User glossary loaded - {Count} terms from {Path}",
                    _terms.Count, DictionaryPath);
            }
            else
            {
                _terms = CreateDefaults();
                Persist();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load user glossary — using empty list");
            _terms = [];
        }
        return _terms;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DictionaryPath)!);
            File.WriteAllText(
                DictionaryPath,
                JsonSerializer.Serialize(_terms, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist user glossary to {Path}", DictionaryPath);
        }
    }

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

    private static List<GlossaryTerm> CreateDefaults() =>
    [
        new GlossaryTerm
        {
            SourceTerm = "Pawn",
            TargetTerm = "Pawn",
            Categories = ["GameTerm"],
            ShouldTranslate = false,
            IsProtected = true,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 50,
            Notes = "Dragon's Dogma term — keep as-is"
        },
        new GlossaryTerm
        {
            SourceTerm = "Arisen",
            TargetTerm = "Arisen",
            Categories = ["GameTerm"],
            ShouldTranslate = false,
            IsProtected = true,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 50,
            Notes = "Dragon's Dogma protagonist title — keep as-is"
        },
        new GlossaryTerm
        {
            SourceTerm = "dragon's fury",
            TargetTerm = "ejderhanın öfkesi",
            Categories = ["Phrase"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "User correction"
        },
        new GlossaryTerm
        {
            SourceTerm = "guild hall",
            TargetTerm = "lonca salonu",
            Categories = ["Location"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "RPG term"
        },
        new GlossaryTerm
        {
            SourceTerm = "vocations",
            TargetTerm = "meslekler",
            Categories = ["GameTerm"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "RPG term (plural listed before singular)"
        },
        new GlossaryTerm
        {
            SourceTerm = "vocation",
            TargetTerm = "meslek",
            Categories = ["GameTerm"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "RPG term"
        },
        new GlossaryTerm
        {
            SourceTerm = "procedures",
            TargetTerm = "işlemler",
            Categories = ["GameTerm"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "RPG term (plural listed before singular)"
        },
        new GlossaryTerm
        {
            SourceTerm = "procedure",
            TargetTerm = "işlem",
            Categories = ["GameTerm"],
            ShouldTranslate = true,
            IsProtected = false,
            MatchMode = GlossaryMatchMode.Phrase,
            Priority = 0,
            Notes = "RPG term"
        },
    ];
}
