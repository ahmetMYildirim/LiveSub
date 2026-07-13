using Microsoft.Extensions.Logging.Abstractions;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using Xunit;

namespace PsGameTranslator.Tests;

/// <summary>
/// Regression tests for the fine-tune export path. GetExportableRecordsAsync used to
/// SELECT 17 columns while the shared reader read column 17 (speaker_name), so every
/// export silently returned 0 records.
/// </summary>
public sealed class ExportRepositoryTests
{
    private static SqliteTranslationRecordRepository CreateRepository() =>
        new(NullLogger<SqliteTranslationRecordRepository>.Instance);

    private static TranslationRecord NewAcceptedRecord(string gameName, string? speaker) => new()
    {
        GameName = gameName,
        SourceText = "More marks of the dragon's fury.",
        NormalizedSourceKey = "more marks of the dragon's fury.",
        OpusTranslation = "Ejderhanın öfkesinin daha fazla izi.",
        FinalTranslation = "Ejderhanın öfkesinin daha fazla izi.",
        Status = TranslationRecordStatus.AcceptedByUser,
        ProviderName = "opus-mt-test",
        SpeakerName = speaker,
    };

    [Fact]
    public async Task ExportableRecords_AcceptedRecord_IsReturned()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var gameName = $"test-game-{Guid.NewGuid():N}";
        var id = await repository.SaveRecordAsync(NewAcceptedRecord(gameName, speaker: "Haymish"));
        Assert.True(id > 0, "record should be saved");

        var exportable = await repository.GetExportableRecordsAsync(gameName);

        var record = Assert.Single(exportable);
        Assert.Equal(gameName, record.GameName);
        Assert.Equal("More marks of the dragon's fury.", record.SourceText);
        Assert.Equal("Ejderhanın öfkesinin daha fazla izi.", record.FinalTranslation);
        Assert.Equal("Haymish", record.SpeakerName);
    }

    [Fact]
    public async Task ExportableRecords_NullSpeaker_IsReturned()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var gameName = $"test-game-{Guid.NewGuid():N}";
        await repository.SaveRecordAsync(NewAcceptedRecord(gameName, speaker: null));

        var exportable = await repository.GetExportableRecordsAsync(gameName);

        var record = Assert.Single(exportable);
        Assert.Null(record.SpeakerName);
    }

    [Fact]
    public async Task ExportableRecords_AutoSavedAndRejected_AreExcluded()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var gameName = $"test-game-{Guid.NewGuid():N}";
        var autoSaved = NewAcceptedRecord(gameName, speaker: null);
        autoSaved.Status = TranslationRecordStatus.AutoSaved;
        var rejected = NewAcceptedRecord(gameName, speaker: null);
        rejected.Status = TranslationRecordStatus.RejectedByUser;
        var edited = NewAcceptedRecord(gameName, speaker: null);
        edited.Status = TranslationRecordStatus.EditedByUser;

        await repository.SaveRecordAsync(autoSaved);
        await repository.SaveRecordAsync(rejected);
        await repository.SaveRecordAsync(edited);

        var exportable = await repository.GetExportableRecordsAsync(gameName);

        var record = Assert.Single(exportable);
        Assert.Equal(TranslationRecordStatus.EditedByUser, record.Status);
    }
}
