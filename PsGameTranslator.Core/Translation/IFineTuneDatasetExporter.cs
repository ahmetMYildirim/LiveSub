namespace PsGameTranslator.Core.Translation;

public interface IFineTuneDatasetExporter
{
    Task<(int Exported, int Skipped, string OutputPath)> ExportJsonlAsync(string outputPath, string? gameName = null);
    Task<(int Exported, int Skipped, string OutputPath)> ExportTsvAsync(string outputPath, string? gameName = null);
}
