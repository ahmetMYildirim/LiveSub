namespace PsGameTranslator.Core.Translation;

public sealed record TranslationModelInfo(
    TranslationProviderType Provider,
    string ModelId,
    string DisplayName,
    string Notes);

/// <summary>
/// Static catalog of known-good models per provider. Local server providers
/// (Ollama / LM Studio) additionally list their installed models live.
/// </summary>
public static class TranslationModelCatalog
{
    /// <summary>
    /// Sentinel value stored in MachineTranslationModel when the user picks the
    /// locally fine-tuned adapter. MachineTranslationServerManager expands it to
    /// an absolute path before passing it to the Python server.
    /// </summary>
    public const string FineTunedModelSentinel = "opus-mt-finetuned";

    public static readonly IReadOnlyList<TranslationModelInfo> MachineTranslationModels =
    [
        new(TranslationProviderType.OpusMT, "Helsinki-NLP/opus-mt-tc-big-en-tr",
            "OPUS-MT Big EN→TR (recommended)", "~1.2 GB download, fast on CPU"),
        new(TranslationProviderType.OpusMT, FineTunedModelSentinel,
            "OPUS-MT FT (Fine-Tuned)", "Yerel ince ayar modeli — önce Eğitim sayfasından eğitin"),
        new(TranslationProviderType.OpusMT, "facebook/nllb-200-distilled-600M",
            "NLLB-200 distilled 600M", "~2.5 GB, better quality, slower"),
        new(TranslationProviderType.OpusMT, "facebook/nllb-200-1.3B",
            "NLLB-200 1.3B", "~5 GB, best quality, needs strong CPU/GPU"),
    ];

    public static readonly IReadOnlyList<TranslationModelInfo> SuggestedOllamaModels =
    [
        new(TranslationProviderType.Ollama, "qwen2.5:3b", "Qwen 2.5 3B", "~2 GB, fast"),
        new(TranslationProviderType.Ollama, "qwen3:4b", "Qwen 3 4B", "~2.6 GB, balanced"),
        new(TranslationProviderType.Ollama, "gemma3:4b", "Gemma 3 4B", "~3 GB"),
        new(TranslationProviderType.Ollama, "phi4-mini", "Phi-4 Mini", "~2.5 GB"),
    ];
}
