using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PsGameTranslator.App.Commands;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.App.ViewModels;

public sealed class LearningViewModel : ObservableObject
{
    private static readonly string ExportDir = Path.Combine(AppContext.BaseDirectory, "exports");
    private static readonly string DebugDir = Path.Combine(AppContext.BaseDirectory, "debug");

    private readonly ITranslationLearningService _learning;
    private readonly TranslationSettings _settings;
    private readonly ILogger<LearningViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    private string _statusText = "Ready";
    private string _statsText = "-";
    private string _editCorrectionText = string.Empty;
    private bool _isEditMode;
    private TranslationRecordViewModel? _selectedRecord;

    public ObservableCollection<TranslationRecordViewModel> Records { get; } = [];

    public LearningViewModel(
        ITranslationLearningService learning,
        TranslationSettings settings,
        ILogger<LearningViewModel> logger)
    {
        _learning = learning;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("LearningViewModel must be created on the UI thread.");

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AcceptCommand = new AsyncRelayCommand(AcceptAsync, CanActOnRecord);
        ApproveTurkishCommand = new ParameterizedAsyncRelayCommand(
            ApproveTurkishAsync,
            parameter => TryGetRecordId(parameter, out _));
        EditCommand = new AsyncRelayCommand(BeginEditAsync, CanActOnRecord);
        SaveCorrectionCommand = new AsyncRelayCommand(SaveCorrectionAsync, () => _isEditMode);
        CancelEditCommand = new AsyncRelayCommand(CancelEditAsync, () => _isEditMode);
        RejectCommand = new AsyncRelayCommand(RejectAsync, CanActOnRecord);
        ExportJsonlCommand = new AsyncRelayCommand(ExportJsonlAsync);
        ExportTsvCommand = new AsyncRelayCommand(ExportTsvAsync);
        OpenExportFolderCommand = new AsyncRelayCommand(OpenExportFolderAsync);
        BulkAcceptSelectedCommand = new AsyncRelayCommand(BulkAcceptSelectedAsync, () => Records.Any(r => r.IsSelected));

        _learning.RecordsChanged += OnRecordsChanged;
        _ = RefreshAsync();
    }

    // ── Bindable properties ───────────────────────────────────────────────────

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string StatsText { get => _statsText; private set => SetProperty(ref _statsText, value); }
    public string EditCorrectionText { get => _editCorrectionText; set => SetProperty(ref _editCorrectionText, value); }
    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value))
                return;

            RaisePropertyChanged(nameof(IsNotEditMode));
        }
    }

    public bool IsNotEditMode => !IsEditMode;

    public TranslationRecordViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                IsEditMode = false;
                ((AsyncRelayCommand)AcceptCommand).NotifyCanExecuteChanged();
                ((ParameterizedAsyncRelayCommand)ApproveTurkishCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)EditCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)RejectCommand).NotifyCanExecuteChanged();
            }
        }
    }

    // Settings bound in UI
    public bool EnableTranslationMemory
    {
        get => _settings.EnableTranslationMemory;
        set { if (_settings.EnableTranslationMemory != value) { _settings.EnableTranslationMemory = value; OnPropertyChanged(); } }
    }
    public bool EnableLearningRecords
    {
        get => _settings.EnableLearningRecords;
        set { if (_settings.EnableLearningRecords != value) { _settings.EnableLearningRecords = value; OnPropertyChanged(); } }
    }
    public bool UseGlobalFallback
    {
        get => _settings.UseGlobalTranslationMemoryFallback;
        set { if (_settings.UseGlobalTranslationMemoryFallback != value) { _settings.UseGlobalTranslationMemoryFallback = value; OnPropertyChanged(); } }
    }
    public bool EnableDatasetQualityFilter
    {
        get => _settings.EnableDatasetQualityFilter;
        set { if (_settings.EnableDatasetQualityFilter != value) { _settings.EnableDatasetQualityFilter = value; OnPropertyChanged(); } }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand RefreshCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand ApproveTurkishCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCorrectionCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ExportJsonlCommand { get; }
    public ICommand ExportTsvCommand { get; }
    public ICommand OpenExportFolderCommand { get; }
    public ICommand BulkAcceptSelectedCommand { get; }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        try
        {
            var records = await _learning.GetRecentRecordsAsync(200);
            var memCount = await _learning.GetMemoryEntryCountAsync();
            var acceptedCount = await _learning.GetCountByStatusAsync(TranslationRecordStatus.AcceptedByUser);
            var editedCount = await _learning.GetCountByStatusAsync(TranslationRecordStatus.EditedByUser);
            var rejectedCount = await _learning.GetCountByStatusAsync(TranslationRecordStatus.RejectedByUser);

            _uiContext.Post(_ =>
            {
                Records.Clear();
                foreach (var r in records)
                {
                    var recordViewModel = new TranslationRecordViewModel(r);
                    recordViewModel.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(TranslationRecordViewModel.IsSelected))
                            NotifySelectionChanged();
                    };
                    Records.Add(recordViewModel);
                }

                var exportable = acceptedCount + editedCount;
                var readiness = exportable switch
                {
                    < 500 => $"collect more before fine-tuning (recommend ≥2,000 pairs; {exportable} so far)",
                    < 2000 => "enough for small LoRA experiments; ≥2,000 recommended for stable results",
                    _ => "ready for LoRA/QLoRA fine-tuning",
                };
                StatsText = $"Records: {records.Count} shown | TM entries: {memCount} | " +
                            $"Accepted: {acceptedCount} | Edited: {editedCount} | Rejected: {rejectedCount} | " +
                            $"Exportable: {exportable} — {readiness}";
                StatusText = "Refreshed";
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LearningViewModel.RefreshAsync failed");
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task AcceptAsync()
    {
        if (_selectedRecord is null) return;
        await AcceptRecordAsync(_selectedRecord.Id);
    }

    private async Task ApproveTurkishAsync(object? parameter)
    {
        if (!TryGetRecordId(parameter, out var id))
        {
            StatusText = "Approve failed: no translation record was selected.";
            return;
        }

        await AcceptRecordAsync(id);
    }

    private async Task AcceptRecordAsync(long id)
    {
        try
        {
            await _learning.AcceptRecordAsync(id);
            IsEditMode = false;
            StatusText = $"Record #{id} accepted and added to translation memory.";
        }
        catch (Exception ex) { StatusText = $"Accept failed: {ex.Message}"; }
    }

    private Task BeginEditAsync()
    {
        if (_selectedRecord is null) return Task.CompletedTask;
        EditCorrectionText = _selectedRecord.FinalTranslation;
        IsEditMode = true;
        ((AsyncRelayCommand)SaveCorrectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)CancelEditCommand).NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private async Task SaveCorrectionAsync()
    {
        if (_selectedRecord is null || string.IsNullOrWhiteSpace(EditCorrectionText)) return;
        var id = _selectedRecord.Id;
        try
        {
            await _learning.EditRecordAsync(id, EditCorrectionText.Trim());
            IsEditMode = false;
            StatusText = $"Record #{id} edited and saved to translation memory.";
        }
        catch (Exception ex) { StatusText = $"Save correction failed: {ex.Message}"; }
    }

    private Task CancelEditAsync()
    {
        IsEditMode = false;
        EditCorrectionText = string.Empty;
        ((AsyncRelayCommand)SaveCorrectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)CancelEditCommand).NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private async Task RejectAsync()
    {
        if (_selectedRecord is null) return;
        var id = _selectedRecord.Id;
        try
        {
            await _learning.RejectRecordAsync(id);
            StatusText = $"Record #{id} rejected.";
        }
        catch (Exception ex) { StatusText = $"Reject failed: {ex.Message}"; }
    }

    private async Task ExportJsonlAsync()
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            var path = Path.Combine(ExportDir, "fine_tune_dataset_en_tr.jsonl");
            StatusText = "Exporting JSONL...";
            var (exported, skipped, outputPath) = await _learning.ExportJsonlAsync(path);
            StatusText = $"JSONL export: {exported} records → {outputPath} ({skipped} skipped)";
        }
        catch (Exception ex) { StatusText = $"JSONL export failed: {ex.Message}"; }
    }

    private async Task ExportTsvAsync()
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            var path = Path.Combine(ExportDir, "fine_tune_dataset_en_tr.tsv");
            StatusText = "Exporting TSV...";
            var (exported, skipped, outputPath) = await _learning.ExportTsvAsync(path);
            StatusText = $"TSV export: {exported} records → {outputPath} ({skipped} skipped)";
        }
        catch (Exception ex) { StatusText = $"TSV export failed: {ex.Message}"; }
    }

    private Task OpenExportFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            System.Diagnostics.Process.Start("explorer.exe", ExportDir);
        }
        catch (Exception ex) { StatusText = $"Cannot open folder: {ex.Message}"; }
        return Task.CompletedTask;
    }

    // Lets the user build their own fine-tune dataset by checking off good
    // translations in bulk instead of accepting them one at a time — accepted
    // records are what ExportJsonl/ExportTsv actually include.
    private async Task BulkAcceptSelectedAsync()
    {
        var selected = Records.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var accepted = 0;
        foreach (var record in selected)
        {
            try
            {
                await _learning.AcceptRecordAsync(record.Id);
                accepted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk accept failed for record {Id}", record.Id);
            }
        }

        StatusText = $"{accepted}/{selected.Count} secili kayit datasete eklendi.";
    }

    public void NotifySelectionChanged() =>
        ((AsyncRelayCommand)BulkAcceptSelectedCommand).NotifyCanExecuteChanged();

    private bool CanActOnRecord() => _selectedRecord is not null && !_isEditMode;

    private static bool TryGetRecordId(object? parameter, out long id)
    {
        switch (parameter)
        {
            case long longId:
                id = longId;
                return longId > 0;
            case int intId:
                id = intId;
                return intId > 0;
            case string text when long.TryParse(text, out var parsed):
                id = parsed;
                return parsed > 0;
            default:
                id = 0;
                return false;
        }
    }

    private void OnRecordsChanged() =>
        _uiContext.Post(_ => { _ = RefreshAsync(); }, null);
}

// ── TranslationRecordViewModel ────────────────────────────────────────────────

public sealed class TranslationRecordViewModel : ObservableObject
{
    private readonly TranslationRecord _model;
    private bool _isSelected;

    public TranslationRecordViewModel(TranslationRecord model)
    {
        _model = model;
    }

    // Bulk-selection for building a custom fine-tune dataset (see
    // LearningViewModel.BulkAcceptSelectedCommand).
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public long Id => _model.Id;
    public string GameName => _model.GameName;
    public string SourceText => _model.SourceText;
    public string OpusTranslation => _model.OpusTranslation ?? string.Empty;
    public string GlossaryTranslation => _model.GlossaryTranslation ?? string.Empty;
    public string OllamaPostedit => _model.OllamaPosteditTranslation ?? string.Empty;
    public string UserCorrection => _model.UserCorrection ?? string.Empty;
    public string FinalTranslation => _model.FinalTranslation;
    public string Status => _model.Status.ToString();
    public string Timestamp => _model.Timestamp.LocalDateTime.ToString("HH:mm:ss");
    public string ProviderName => _model.ProviderName;
    public long DurationMs => _model.DurationMs;
    public string Notes => _model.Notes ?? string.Empty;
    public string UsedGlossaryTerms => _model.UsedGlossaryTermsJson ?? string.Empty;
}
