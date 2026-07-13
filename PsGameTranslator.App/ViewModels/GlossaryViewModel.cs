using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PsGameTranslator.App.Commands;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

public sealed class GlossaryViewModel : ObservableObject
{
    private readonly GlossaryDictionaryManager _manager;
    private readonly UserGlossaryRepository _userRepo;
    private readonly RefinementOrchestrator _refinementOrchestrator;
    private readonly TranslationSettings _translationSettings;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly ILogger<GlossaryViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };

    // Used for reading external/imported JSON files — tolerates camelCase and string enums.
    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    // ── Term collection + view ────────────────────────────────────────────────

    public ObservableCollection<GlossaryTermViewModel> Terms { get; } = [];
    public ICollectionView TermsView { get; }

    // ── Search / filter ───────────────────────────────────────────────────────

    private string _searchSource = string.Empty;
    private string _searchTarget = string.Empty;
    private string _filterCategory = string.Empty;
    private bool _filterProtectedOnly;
    private bool _filterUserCorrectionsOnly;

    public string SearchSource
    {
        get => _searchSource;
        set { SetProperty(ref _searchSource, value); TermsView.Refresh(); }
    }
    public string SearchTarget
    {
        get => _searchTarget;
        set { SetProperty(ref _searchTarget, value); TermsView.Refresh(); }
    }
    public string FilterCategory
    {
        get => _filterCategory;
        set { SetProperty(ref _filterCategory, value); TermsView.Refresh(); }
    }
    public bool FilterProtectedOnly
    {
        get => _filterProtectedOnly;
        set { SetProperty(ref _filterProtectedOnly, value); TermsView.Refresh(); }
    }
    public bool FilterUserCorrectionsOnly
    {
        get => _filterUserCorrectionsOnly;
        set { SetProperty(ref _filterUserCorrectionsOnly, value); TermsView.Refresh(); }
    }

    // ── Selected item ─────────────────────────────────────────────────────────

    private GlossaryTermViewModel? _selectedTerm;
    public GlossaryTermViewModel? SelectedTerm
    {
        get => _selectedTerm;
        set => SetProperty(ref _selectedTerm, value);
    }

    // ── Add new term ──────────────────────────────────────────────────────────

    private string _newSourceTerm = string.Empty;
    private string _newTargetTerm = string.Empty;
    private string _newCategoriesText = "UserCorrection";
    private GlossaryMatchMode _newMatchMode = GlossaryMatchMode.Phrase;
    private int _newPriority;
    private bool _newCaseSensitive;
    private bool _newShouldTranslate = true;
    private bool _newIsProtected;
    private string _newNotes = string.Empty;

    public string NewSourceTerm { get => _newSourceTerm; set => SetProperty(ref _newSourceTerm, value); }
    public string NewTargetTerm { get => _newTargetTerm; set => SetProperty(ref _newTargetTerm, value); }
    public string NewCategoriesText { get => _newCategoriesText; set => SetProperty(ref _newCategoriesText, value); }
    public GlossaryMatchMode NewMatchMode { get => _newMatchMode; set => SetProperty(ref _newMatchMode, value); }
    public int NewPriority { get => _newPriority; set => SetProperty(ref _newPriority, value); }
    public bool NewCaseSensitive { get => _newCaseSensitive; set => SetProperty(ref _newCaseSensitive, value); }
    public bool NewShouldTranslate { get => _newShouldTranslate; set => SetProperty(ref _newShouldTranslate, value); }
    public bool NewIsProtected { get => _newIsProtected; set => SetProperty(ref _newIsProtected, value); }
    public string NewNotes { get => _newNotes; set => SetProperty(ref _newNotes, value); }

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ── Bulk import ───────────────────────────────────────────────────────────

    private string _bulkImportText = string.Empty;
    private bool _bulkImportUpdateExisting;
    private string _bulkImportPreview = string.Empty;

    public string BulkImportText { get => _bulkImportText; set => SetProperty(ref _bulkImportText, value); }
    public bool BulkImportUpdateExisting { get => _bulkImportUpdateExisting; set => SetProperty(ref _bulkImportUpdateExisting, value); }
    public string BulkImportPreview { get => _bulkImportPreview; private set => SetProperty(ref _bulkImportPreview, value); }

    // ── Dictionary slot enable/disable (bound to manager) ────────────────────

    public bool UseGlobal { get => _manager.UseGlobal; set { _manager.UseGlobal = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseRpg { get => _manager.UseRpg; set { _manager.UseRpg = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseCrpg { get => _manager.UseCrpg; set { _manager.UseCrpg = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseActionRpg { get => _manager.UseActionRpg; set { _manager.UseActionRpg = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseJrpg { get => _manager.UseJrpg; set { _manager.UseJrpg = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseGameSpecific { get => _manager.UseGameSpecific; set { _manager.UseGameSpecific = value; OnPropertyChanged(); RefreshSlotStats(); } }
    public bool UseUserCorrections { get => _manager.UseUserCorrections; set { _manager.UseUserCorrections = value; OnPropertyChanged(); RefreshSlotStats(); } }

    private string _slotStatsText = string.Empty;
    public string SlotStatsText { get => _slotStatsText; private set => SetProperty(ref _slotStatsText, value); }

    // ── Refinement test ───────────────────────────────────────────────────────

    private string _refinementSourceText = "More marks of the dragon's fury.";
    private string _refinementMachineText = "Ejderhanın öfkesinin daha fazla izi.";
    private string _refinementModel = string.Empty;
    private int _refinementTimeoutMs = 1800;
    private string _refinementResultText = "-";
    private string _refinementRawOutput = "-";
    private string _refinementDurationText = "-";
    private string _refinementStatusText = "Not tested";

    public string RefinementSourceText { get => _refinementSourceText; set => SetProperty(ref _refinementSourceText, value); }
    public string RefinementMachineText { get => _refinementMachineText; set => SetProperty(ref _refinementMachineText, value); }
    public string RefinementModel
    {
        get => string.IsNullOrWhiteSpace(_refinementModel) ? _translationSettings.OllamaRefinementModel : _refinementModel;
        set => SetProperty(ref _refinementModel, value);
    }
    public int RefinementTimeoutMs { get => _refinementTimeoutMs; set => SetProperty(ref _refinementTimeoutMs, Math.Max(500, value)); }
    public string RefinementResultText { get => _refinementResultText; private set => SetProperty(ref _refinementResultText, value); }
    public string RefinementRawOutput { get => _refinementRawOutput; private set => SetProperty(ref _refinementRawOutput, value); }
    public string RefinementDurationText { get => _refinementDurationText; private set => SetProperty(ref _refinementDurationText, value); }
    public string RefinementStatusText { get => _refinementStatusText; private set => SetProperty(ref _refinementStatusText, value); }
    public string RefinementEnabledText => _translationSettings.EnableOllamaRefinement ? "Enabled" : "Disabled";
    public string RefinementModeText => _translationSettings.OllamaRefinementMode.ToString();
    public string LastRefinedTextDiag => _diagnostics.LastRefinedText;
    public string LastRefinementErrorDiag => _diagnostics.LastRefinementError;
    public string LastRefinementDurationDiag => $"{_diagnostics.LastRefinementDurationMs} ms";

    // ── Static option lists ───────────────────────────────────────────────────

    public static IReadOnlyList<GlossaryMatchMode> MatchModeOptions { get; } =
        Enum.GetValues<GlossaryMatchMode>().ToList();

    public static IReadOnlyList<string> CategoryOptions { get; } =
        ["GameTerm", "CharacterName", "LocationName", "Phrase", "UserCorrection", "CRPG", "JRPG", "ActionRPG"];

    // ── Built-in per-game glossaries ──────────────────────────────────────────

    public static IReadOnlyList<GameGlossaryInfo> BuiltInGameOptions { get; } = GameGlossaryCatalog.Games;

    private GameGlossaryInfo? _selectedBuiltInGame = GameGlossaryCatalog.Games.FirstOrDefault();
    public GameGlossaryInfo? SelectedBuiltInGame
    {
        get => _selectedBuiltInGame;
        set => SetProperty(ref _selectedBuiltInGame, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand AddTermCommand { get; }
    public ICommand RemoveSelectedTermCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand SaveCurrentCorrectionCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand PreviewBulkImportCommand { get; }
    public ICommand ImportBulkCommand { get; }
    public ICommand ImportJsonFileCommand { get; }
    public ICommand LoadGlobalDictionaryCommand { get; }
    public ICommand LoadGameDictionaryCommand { get; }
    public ICommand LoadBuiltInGameDictionaryCommand { get; }
    public ICommand ReloadDefaultDictionariesCommand { get; }
    public ICommand ExportUserCorrectionsJsonCommand { get; }
    public ICommand ExportMergedJsonCommand { get; }
    public ICommand ExportFineTuneCsvCommand { get; }
    public ICommand ExportFineTuneJsonlCommand { get; }
    public ICommand TestRefinementCommand { get; }

    public GlossaryViewModel(
        GlossaryDictionaryManager manager,
        UserGlossaryRepository userRepo,
        RefinementOrchestrator refinementOrchestrator,
        TranslationSettings translationSettings,
        PipelineDiagnostics diagnostics,
        ILogger<GlossaryViewModel> logger)
    {
        _manager = manager;
        _userRepo = userRepo;
        _refinementOrchestrator = refinementOrchestrator;
        _translationSettings = translationSettings;
        _diagnostics = diagnostics;
        _logger = logger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("GlossaryViewModel must be created on the UI thread.");

        foreach (var t in manager.GetMergedTerms())
            Terms.Add(new GlossaryTermViewModel(t));

        TermsView = CollectionViewSource.GetDefaultView(Terms);
        TermsView.Filter = FilterTerm;

        AddTermCommand = new AsyncRelayCommand(AddTermAsync);
        RemoveSelectedTermCommand = new AsyncRelayCommand(RemoveSelectedTermAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);
        SaveCurrentCorrectionCommand = new AsyncRelayCommand(SaveCurrentCorrectionAsync);
        ClearFilterCommand = new AsyncRelayCommand(ClearFilterAsync);
        PreviewBulkImportCommand = new AsyncRelayCommand(PreviewBulkImportAsync);
        ImportBulkCommand = new AsyncRelayCommand(ImportBulkAsync);
        ImportJsonFileCommand = new AsyncRelayCommand(ImportJsonFileAsync);
        LoadGlobalDictionaryCommand = new AsyncRelayCommand(LoadGlobalDictionaryAsync);
        LoadGameDictionaryCommand = new AsyncRelayCommand(LoadGameDictionaryAsync);
        LoadBuiltInGameDictionaryCommand = new AsyncRelayCommand(LoadBuiltInGameDictionaryAsync);
        ReloadDefaultDictionariesCommand = new AsyncRelayCommand(ReloadDefaultDictionariesAsync);
        ExportUserCorrectionsJsonCommand = new AsyncRelayCommand(ExportUserCorrectionsJsonAsync);
        ExportMergedJsonCommand = new AsyncRelayCommand(ExportMergedJsonAsync);
        ExportFineTuneCsvCommand = new AsyncRelayCommand(ExportFineTuneCsvAsync);
        ExportFineTuneJsonlCommand = new AsyncRelayCommand(ExportFineTuneJsonlAsync);
        TestRefinementCommand = new AsyncRelayCommand(TestRefinementAsync);

        userRepo.Changed += OnUserRepoChanged;
        manager.Changed += OnManagerChanged;

        RefreshSlotStats();
    }

    // ── Filter predicate ──────────────────────────────────────────────────────

    private bool FilterTerm(object obj)
    {
        if (obj is not GlossaryTermViewModel t) return false;

        if (!string.IsNullOrWhiteSpace(_searchSource) &&
            !t.SourceTerm.Contains(_searchSource, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(_searchTarget) &&
            !t.TargetTerm.Contains(_searchTarget, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(_filterCategory) &&
            !t.CategoriesText.Contains(_filterCategory, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_filterProtectedOnly && !t.IsProtected)
            return false;

        return true;
    }

    // ── Add / Remove ──────────────────────────────────────────────────────────

    private Task AddTermAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceTerm))
        {
            StatusText = "Kaynak terim gerekli.";
            return Task.CompletedTask;
        }
        var categories = NewCategoriesText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var term = new GlossaryTerm
        {
            SourceTerm = NewSourceTerm.Trim(),
            TargetTerm = NewTargetTerm.Trim(),
            Categories = categories,
            ShouldTranslate = NewShouldTranslate,
            IsProtected = NewIsProtected,
            MatchMode = NewMatchMode,
            CaseSensitive = NewCaseSensitive,
            Priority = NewPriority,
            Notes = NewNotes.Trim(),
        };
        _userRepo.Add(term);
        NewSourceTerm = string.Empty;
        NewTargetTerm = string.Empty;
        NewNotes = string.Empty;
        StatusText = $"Eklendi: {term.SourceTerm}";
        RefreshTermsFromRepo();
        return Task.CompletedTask;
    }

    private Task RemoveSelectedTermAsync()
    {
        if (SelectedTerm is null) { StatusText = "Select a term to remove."; return Task.CompletedTask; }
        if (!string.Equals(SelectedTerm.SourceDictionary, "UserCorrections", StringComparison.Ordinal))
        {
            StatusText = $"'{SelectedTerm.SourceTerm}' bir sözlük dosyasından geliyor ({SelectedTerm.SourceDictionary}) — buradan silinemez, dosyayı düzenleyin.";
            return Task.CompletedTask;
        }
        var name = SelectedTerm.SourceTerm;
        _userRepo.Remove(SelectedTerm.ToModel());
        StatusText = $"Removed: {name}";
        RefreshTermsFromRepo();
        return Task.CompletedTask;
    }

    // Terms mirrors the full merged dictionary (user corrections + global/genre/
    // game slots), not just user corrections — every mutation path (add/remove/
    // bulk import/reload) must call this or the grid goes stale/incomplete.
    private void RefreshTermsFromRepo()
    {
        var selectedSource = SelectedTerm?.SourceTerm;
        Terms.Clear();
        foreach (var t in _manager.GetMergedTerms())
            Terms.Add(new GlossaryTermViewModel(t));
        if (selectedSource is not null)
            SelectedTerm = Terms.FirstOrDefault(t =>
                string.Equals(t.SourceTerm, selectedSource, StringComparison.OrdinalIgnoreCase));
    }

    private Task SaveAsync()
    {
        _userRepo.Save();
        StatusText = "Glossary saved.";
        _ = SaveDiagnosticAsync("last_user_correction_save.json", new { Timestamp = DateTimeOffset.Now, Status = "saved", Count = _userRepo.Terms.Count });
        return Task.CompletedTask;
    }

    private Task ReloadAsync()
    {
        _userRepo.Reload();
        RefreshTermsFromRepo();
        StatusText = "Glossary reloaded.";
        return Task.CompletedTask;
    }

    private Task SaveCurrentCorrectionAsync()
    {
        var sourceEnglish = _diagnostics.CurrentSubtitleSourceText;
        var currentTurkish = _diagnostics.CurrentOverlayDisplayText;
        if (string.IsNullOrWhiteSpace(sourceEnglish))
        {
            StatusText = "No current subtitle detected. Run monitoring first.";
            return Task.CompletedTask;
        }
        NewSourceTerm = sourceEnglish.Trim();
        NewTargetTerm = currentTurkish.Trim();
        NewCategoriesText = "UserCorrection";
        NewShouldTranslate = true;
        NewIsProtected = false;
        NewNotes = $"Saved from current subtitle at {DateTimeOffset.Now:HH:mm:ss}";
        StatusText = "Fields pre-filled from current subtitle. Review and click Add.";
        return Task.CompletedTask;
    }

    private Task ClearFilterAsync()
    {
        SearchSource = string.Empty;
        SearchTarget = string.Empty;
        FilterCategory = string.Empty;
        FilterProtectedOnly = false;
        FilterUserCorrectionsOnly = false;
        return Task.CompletedTask;
    }

    // ── Bulk import ───────────────────────────────────────────────────────────

    private Task PreviewBulkImportAsync()
    {
        var (terms, errors) = ParseBulkImportText(BulkImportText);
        var sb = new StringBuilder();
        sb.AppendLine($"Parsed: {terms.Count} terms, {errors.Count} parse errors");
        foreach (var term in terms.Take(10))
            sb.AppendLine($"  + [{term.MatchMode}] {term.SourceTerm} → {term.TargetTerm}  [{string.Join(",", term.Categories)}]");
        if (terms.Count > 10) sb.AppendLine($"  ... and {terms.Count - 10} more");
        foreach (var err in errors.Take(5)) sb.AppendLine($"  ERR: {err}");
        BulkImportPreview = sb.ToString().TrimEnd();
        return Task.CompletedTask;
    }

    private async Task ImportBulkAsync()
    {
        var (terms, errors) = ParseBulkImportText(BulkImportText);
        if (terms.Count == 0) { StatusText = "No valid terms to import."; return; }

        int added = 0, updated = 0, skipped = 0;
        foreach (var term in terms)
        {
            var existing = _userRepo.Terms
                .FirstOrDefault(t => string.Equals(t.SourceTerm, term.SourceTerm, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _userRepo.Add(term);
                added++;
            }
            else if (BulkImportUpdateExisting)
            {
                _userRepo.Update(term);
                updated++;
            }
            else
            {
                skipped++;
            }
        }

        var result = new { Timestamp = DateTimeOffset.Now, Added = added, Updated = updated, Skipped = skipped, Errors = errors };
        await SaveDiagnosticAsync("last_bulk_import_result.json", result);
        BulkImportPreview = $"Import done: +{added} added, ~{updated} updated, {skipped} skipped, {errors.Count} parse errors.";
        StatusText = $"Bulk import: +{added} added.";
        RefreshTermsFromRepo();
    }

    private static (List<GlossaryTerm> Terms, List<string> Errors) ParseBulkImportText(string text)
    {
        var terms = new List<GlossaryTerm>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (terms, errors);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            string[] parts;
            if (line.Contains('|'))
                parts = line.Split('|');
            else if (line.Contains('\t'))
                parts = line.Split('\t');
            else
                parts = line.Split(',');

            parts = parts.Select(p => p.Trim().Trim('"')).ToArray();
            if (parts.Length < 2)
            {
                errors.Add($"Too few columns: {line[..Math.Min(line.Length, 60)]}");
                continue;
            }

            var source = parts[0].Trim();
            var target = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                errors.Add($"Empty source: {line[..Math.Min(line.Length, 60)]}");
                continue;
            }

            var categoriesRaw = parts.Length > 2 ? parts[2].Trim() : "UserCorrection";
            var categories = categoriesRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var priorityStr = parts.Length > 3 ? parts[3].Trim() : "0";
            int.TryParse(priorityStr, out var priority);
            var matchModeStr = parts.Length > 4 ? parts[4].Trim() : "Phrase";
            if (!Enum.TryParse<GlossaryMatchMode>(matchModeStr, true, out var matchMode))
                matchMode = GlossaryMatchMode.Phrase;
            var notes = parts.Length > 5 ? parts[5].Trim() : string.Empty;

            terms.Add(new GlossaryTerm
            {
                SourceTerm = source,
                TargetTerm = target,
                Categories = categories,
                Priority = priority,
                MatchMode = matchMode,
                Notes = notes,
                ShouldTranslate = true,
            });
        }
        return (terms, errors);
    }

    // ── Dictionary management ─────────────────────────────────────────────────

    private async Task LoadGlobalDictionaryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Global Dictionary",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var (count, error) = await _manager.LoadFromFileAsync(DictionarySlotKind.Global, dialog.FileName);
        var result = new { Timestamp = DateTimeOffset.Now, Slot = "Global", File = dialog.FileName, Count = count, Error = error };
        await SaveDiagnosticAsync("last_dictionary_load_result.json", result);
        StatusText = error is null ? $"Global dictionary loaded: {count} terms." : $"Load failed: {error}";
        RefreshSlotStats();
    }

    private async Task LoadGameDictionaryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Game-Specific Dictionary",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var (count, error) = await _manager.LoadFromFileAsync(DictionarySlotKind.GameSpecific, dialog.FileName);
        var result = new { Timestamp = DateTimeOffset.Now, Slot = "GameSpecific", File = dialog.FileName, Count = count, Error = error };
        await SaveDiagnosticAsync("last_dictionary_load_result.json", result);
        StatusText = error is null ? $"Game dictionary loaded: {count} terms." : $"Load failed: {error}";
        UseGameSpecific = true;
        RefreshSlotStats();
    }

    private async Task LoadBuiltInGameDictionaryAsync()
    {
        var game = SelectedBuiltInGame;
        if (game is null) return;

        var path = GameGlossaryCatalog.ResolveFullPath(game);
        var (count, error) = await _manager.LoadFromFileAsync(DictionarySlotKind.GameSpecific, path);
        var result = new { Timestamp = DateTimeOffset.Now, Slot = "GameSpecific", Game = game.DisplayName, File = path, Count = count, Error = error };
        await SaveDiagnosticAsync("last_dictionary_load_result.json", result);
        StatusText = error is null
            ? $"{game.DisplayName} sozlugu yuklendi: {count} terim."
            : $"Yukleme basarisiz: {error}";
        UseGameSpecific = true;
        RefreshSlotStats();
    }

    private async Task ReloadDefaultDictionariesAsync()
    {
        await _manager.ReloadDefaultPathsAsync();
        RefreshSlotStats();
        StatusText = $"Dictionaries reloaded. Active: {_manager.ActiveDictionariesSummary}";
        await SaveDiagnosticAsync("last_dictionary_load_result.json",
            new { Timestamp = DateTimeOffset.Now, Action = "ReloadDefaults", Active = _manager.ActiveDictionariesSummary });
    }

    // ── Import JSON ───────────────────────────────────────────────────────────

    private async Task ImportJsonFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Glossary JSON (into User Corrections)",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            var terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(json, JsonRead) ?? [];
            // Null-guard and migrate Category → Categories for files from older format.
            foreach (var t in terms)
            {
                t.Categories ??= [];
                if (t.Categories.Length == 0 && t.Category is { Length: > 0 })
                {
                    t.Categories = [t.Category];
                    t.Category = null;
                }
            }
            int added = 0;
            foreach (var term in terms)
            {
                _userRepo.Add(term);
                added++;
            }
            StatusText = $"Imported {added} terms from {Path.GetFileName(dialog.FileName)}.";
            RefreshTermsFromRepo();
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            _logger.LogWarning(ex, "glossary_json_import_failed");
        }
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private async Task ExportUserCorrectionsJsonAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export User Corrections",
            FileName = "user_corrections_en_tr.json",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        var json = JsonSerializer.Serialize(_userRepo.Terms, JsonPretty);
        await File.WriteAllTextAsync(dialog.FileName, json, Encoding.UTF8);
        StatusText = $"User corrections exported to {Path.GetFileName(dialog.FileName)}.";
    }

    private async Task ExportMergedJsonAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Merged Glossary",
            FileName = "merged_glossary_en_tr.json",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        var json = JsonSerializer.Serialize(_manager.GetMergedTerms(), JsonPretty);
        await File.WriteAllTextAsync(dialog.FileName, json, Encoding.UTF8);
        StatusText = $"Merged glossary exported ({_manager.GetMergedTerms().Count} terms).";
    }

    private async Task ExportFineTuneCsvAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Fine-Tune CSV",
            FileName = "fine_tune_dataset.csv",
            Filter = "CSV files (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;
        var terms = _manager.GetMergedTerms().Where(t => t.ShouldTranslate && !string.IsNullOrWhiteSpace(t.TargetTerm));
        var sb = new StringBuilder();
        sb.AppendLine("source,target");
        foreach (var t in terms)
            sb.AppendLine($"\"{EscapeCsv(t.SourceTerm)}\",\"{EscapeCsv(t.TargetTerm)}\"");
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
        StatusText = $"Fine-tune CSV exported.";
    }

    private async Task ExportFineTuneJsonlAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Fine-Tune JSONL",
            FileName = "fine_tune_dataset.jsonl",
            Filter = "JSONL files (*.jsonl)|*.jsonl|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        var terms = _manager.GetMergedTerms().Where(t => t.ShouldTranslate && !string.IsNullOrWhiteSpace(t.TargetTerm));
        var sb = new StringBuilder();
        foreach (var t in terms)
        {
            var entry = new
            {
                messages = new object[]
                {
                    new { role = "user", content = $"Translate to Turkish: {t.SourceTerm}" },
                    new { role = "assistant", content = t.TargetTerm },
                }
            };
            sb.AppendLine(JsonSerializer.Serialize(entry));
        }
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
        StatusText = $"Fine-tune JSONL exported.";
    }

    // ── Ollama refinement test ────────────────────────────────────────────────

    private async Task TestRefinementAsync()
    {
        if (string.IsNullOrWhiteSpace(RefinementSourceText) || string.IsNullOrWhiteSpace(RefinementMachineText))
        {
            RefinementStatusText = "Fill in source and machine translation first.";
            return;
        }
        RefinementStatusText = "Running Ollama refinement...";
        RefinementResultText = "-";
        RefinementRawOutput = "-";
        RefinementDurationText = "-";

        var savedModel = _translationSettings.OllamaRefinementModel;
        var savedTimeout = _translationSettings.OllamaRefinementTimeoutMs;
        _translationSettings.OllamaRefinementModel = RefinementModel;
        _translationSettings.OllamaRefinementTimeoutMs = RefinementTimeoutMs;

        try
        {
            var result = await _refinementOrchestrator.RefineManualAsync(
                RefinementSourceText.Trim(), RefinementMachineText.Trim(),
                _translationSettings.GameProfile, CancellationToken.None);

            RefinementResultText = result.RefinedText;
            RefinementRawOutput = result.RawOutput is { Length: > 0 }
                ? result.RawOutput[..Math.Min(result.RawOutput.Length, 2000)]
                : "(empty)";
            RefinementDurationText = $"{result.DurationMs} ms";
            RefinementStatusText = result.Success
                ? $"OK ({result.DurationMs} ms)"
                : result.TimedOut ? $"Timed out after {result.DurationMs} ms"
                : result.ErrorMessage ?? "Failed";

            _diagnostics.LastRefinementDurationMs = result.DurationMs;
            _diagnostics.LastRefinedText = result.RefinedText;
            _diagnostics.LastRefinementError = result.ErrorMessage ?? string.Empty;
            RaisePropertyChanged(nameof(LastRefinedTextDiag));
            RaisePropertyChanged(nameof(LastRefinementErrorDiag));
            RaisePropertyChanged(nameof(LastRefinementDurationDiag));
        }
        catch (Exception ex)
        {
            RefinementStatusText = $"Error: {ex.Message}";
            _logger.LogWarning(ex, "refinement_test_failed");
        }
        finally
        {
            _translationSettings.OllamaRefinementModel = savedModel;
            _translationSettings.OllamaRefinementTimeoutMs = savedTimeout;
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnUserRepoChanged()
    {
        _uiContext.Post(_ =>
        {
            RefreshTermsFromRepo();
            TermsView.Refresh();
            StatusText = $"Glossary updated — {Terms.Count} terms.";
            RefreshSlotStats();
        }, null);
    }

    private void OnManagerChanged()
    {
        _uiContext.Post(_ =>
        {
            RefreshTermsFromRepo();
            TermsView.Refresh();
            RefreshSlotStats();
        }, null);
    }

    private void RefreshSlotStats()
    {
        var totalMerged = _manager.TotalTermCount;
        var userCount = _manager.UserCorrectionCount;
        SlotStatsText = $"Total active terms: {totalMerged}  |  User corrections: {userCount}  |  Active: {_manager.ActiveDictionariesSummary}";
        _ = SaveDiagnosticAsync("last_glossary_ui_state.json", new
        {
            Timestamp = DateTimeOffset.Now,
            UseGlobal = _manager.UseGlobal,
            UseRpg = _manager.UseRpg,
            UseCrpg = _manager.UseCrpg,
            UseActionRpg = _manager.UseActionRpg,
            UseJrpg = _manager.UseJrpg,
            UseGameSpecific = _manager.UseGameSpecific,
            UseUserCorrections = _manager.UseUserCorrections,
            TotalTermCount = totalMerged,
            UserCorrectionCount = userCount,
            ActiveSummary = _manager.ActiveDictionariesSummary,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task SaveDiagnosticAsync(string fileName, object data)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, fileName),
                JsonSerializer.Serialize(data, JsonPretty),
                Encoding.UTF8);
        }
        catch { /* diagnostics are best-effort */ }
    }

    private static string EscapeCsv(string s) => s.Replace("\"", "\"\"");
}

// ── GlossaryTermViewModel ────────────────────────────────────────────────────

public sealed class GlossaryTermViewModel : ObservableObject
{
    private string _sourceTerm;
    private string _targetTerm;
    private string[] _categories;
    private GlossaryMatchMode _matchMode;
    private int _priority;
    private bool _caseSensitive;
    private bool _shouldTranslate;
    private bool _isProtected;
    private string _notes;
    private string _sourceDictionary;

    public GlossaryTermViewModel(GlossaryTerm term)
    {
        _sourceTerm = term.SourceTerm;
        _targetTerm = term.TargetTerm;
        var cats = term.Categories ?? [];
        _categories = cats.Length > 0
            ? cats
            : term.Category is { Length: > 0 } ? [term.Category] : [];
        _matchMode = term.MatchMode;
        _priority = term.Priority;
        _caseSensitive = term.CaseSensitive;
        _shouldTranslate = term.ShouldTranslate;
        _isProtected = term.IsProtected;
        _notes = term.Notes;
        _sourceDictionary = term.SourceDictionary;
    }

    public string SourceTerm { get => _sourceTerm; set => SetProperty(ref _sourceTerm, value); }
    public string TargetTerm { get => _targetTerm; set => SetProperty(ref _targetTerm, value); }
    public GlossaryMatchMode MatchMode { get => _matchMode; set => SetProperty(ref _matchMode, value); }
    public int Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public bool CaseSensitive { get => _caseSensitive; set => SetProperty(ref _caseSensitive, value); }
    public bool ShouldTranslate { get => _shouldTranslate; set => SetProperty(ref _shouldTranslate, value); }
    public bool IsProtected { get => _isProtected; set => SetProperty(ref _isProtected, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public string SourceDictionary { get => _sourceDictionary; set => SetProperty(ref _sourceDictionary, value); }

    public string CategoriesText
    {
        get => string.Join(", ", _categories);
        set
        {
            var arr = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _categories = arr;
            OnPropertyChanged();
        }
    }

    public GlossaryTerm ToModel() => new()
    {
        SourceTerm = _sourceTerm,
        TargetTerm = _targetTerm,
        Categories = _categories,
        ShouldTranslate = _shouldTranslate,
        IsProtected = _isProtected,
        MatchMode = _matchMode,
        CaseSensitive = _caseSensitive,
        Priority = _priority,
        Notes = _notes,
    };
}
