namespace PsGameTranslator.Core.Ocr;

public sealed class PreprocessingSettings
{
    public PreprocessingPreset Preset { get; init; } = PreprocessingPreset.FastSubtitle;
    public bool EnableHighContrast { get; init; }
    public bool EnableSharpen { get; init; }
}
