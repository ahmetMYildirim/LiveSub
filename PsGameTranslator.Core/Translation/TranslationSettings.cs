using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Translation
{
    public sealed class TranslationSettings
    {
        public bool EnableTranslation { get; set; } = false;
        public TranslationProviderType ProviderType { get; set; } = TranslationProviderType.MachineTranslation;
        public TranslationProfile Profile { get; set; } = TranslationProfile.Balanced;
        public TranslationProviderChainMode ProviderChainMode { get; set; } = TranslationProviderChainMode.LocalOnly;
        public int FastTranslationTimeoutMs { get; set; } = 1200;
        public int BalancedTranslationTimeoutMs { get; set; } = 1800;
        public int AccurateTranslationTimeoutMs { get; set; } = 3500;
        public bool EnableCloudProviders { get; set; } = false;
        public bool EnableTranslationProviderFallback { get; set; } = true;
        public bool AllowFallbackDuringProviderTest { get; set; } = false;
        public TranslationProviderType TranslationProviderType
        {
            get => ProviderType;
            set => ProviderType = value;
        }
        public bool EnableOllamaTranslation
        {
            get => EnableTranslation;
            set => EnableTranslation = value;
        }
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "qwen3:4b";

        // LM Studio (OpenAI-compatible local server).
        public string LmStudioBaseUrl { get; set; } = "http://127.0.0.1:1234";
        // Empty = whatever model is currently loaded in LM Studio.
        public string LmStudioModel { get; set; } = "";
        public int LmStudioTimeoutMs { get; set; } = 10000;
        public int TranslationTimeoutMs { get; set; } = 120000;
        public string SourceLanguage { get; set; } = "en";
        public string TargetLanguage { get; set; } = "tr";
        public string MachineTranslationBaseUrl { get; set; } = "http://127.0.0.1:8770";
        // 4500 (was 3000): OPUS runs on the same GPU the game uses, so inference
        // occasionally spikes well past 3s under load and the request was timing
        // out and dropping the line. More headroom keeps those lines instead of
        // losing them (fallback to a cloud provider still covers a true hang).
        public int MachineTranslationTimeoutMs { get; set; } = 4500;
        // HuggingFace model id served by the local translation server.
        // MarianMT (Helsinki-NLP/opus-mt-*) and NLLB models are supported.
        public string MachineTranslationModel { get; set; } = "Helsinki-NLP/opus-mt-tc-big-en-tr";
        public string GameProfile { get; set; } = "default";
        // When false, TranslationPostProcessor skips glossary term substitution
        // (built-in phrase corrections still apply).
        public bool EnableGlossaryCorrections { get; set; } = true;
        // Official Google Cloud Translation API key. When empty, GoogleTranslateProvider
        // falls back to the free unofficial translate.googleapis.com endpoint.
        public string GoogleTranslateApiKey { get; set; } = "";

        // DeepL API key. Keys ending in ":fx" are free-tier and use the api-free
        // host; anything else uses the paid api host. Empty = provider unavailable.
        public string DeepLApiKey { get; set; } = "";

        // Google Gemini (Generative Language API). Empty = provider unavailable.
        public string GeminiApiKey { get; set; } = "";
        public string GeminiModel { get; set; } = "gemini-2.0-flash";

        // Groq (OpenAI-compatible fast inference of open models). Empty = provider unavailable.
        public string GroqApiKey { get; set; } = "";
        public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
        public bool UseTranslationCache { get; set; } = true;
        public bool CacheEnabled
        {
            get => UseTranslationCache;
            set => UseTranslationCache = value;
        }
        public bool DropStaleTranslations { get; set; } = false;
        public bool ShowOcrFallbackWhenTranslationFails { get; set; } = true;
        // When true, FakeTranslationProvider overrides the selected provider.
        // Debug/testing only — never enable for real gameplay translation.
        public bool UseFakeTranslationProviderForDebug { get; set; } = false;
        public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.OcrThenTranslate;

        // Fast-dialogue subtitle translation queue.
        public int MaxTranslationQueueSize { get; set; } = 5;
        // Lowered from 0.92: OCR re-reads the same on-screen subtitle with small
        // variance (a letter or two different) frame to frame, which pushed a
        // long sentence just under the old threshold and got it re-translated and
        // re-shown as if it were a new line. 0.86 absorbs that OCR noise while
        // still keeping genuinely different short lines apart.
        public double SubtitleDedupSimilarityThreshold { get; set; } = 0.86;
        public int MinSubtitleDisplayMs { get; set; } = 1200;
        public int MaxSubtitleAgeMs { get; set; } = 5000;
        public bool DropExpiredUntranslatedSubtitles { get; set; } = true;
        public bool PrioritizeCurrentSubtitle { get; set; } = true;
        // Show the English OCR subtitle immediately while translation is pending
        // so the overlay is never blank during fast dialogue.
        // Superseded by TurkishOnlyMode in the ordered pipeline (Part A) — kept for
        // the legacy (TurkishOnlyMode=false / debug) display path.
        public bool ShowSourceWhileTranslating { get; set; } = false;
        public bool KeepTranslatedTextWhileSameSourceDetected { get; set; } = true;
        public int MinTranslatedDisplayMs { get; set; } = 1200;
        public int ClearOverlayAfterNoSubtitleMs { get; set; } = 1400;

        // ── Ordered subtitle pipeline / Turkish-only live mode (Part A–L) ───────────

        // Part A — Turkish-only live mode.
        public bool TurkishOnlyMode { get; set; } = true;
        public bool KeepPreviousTurkishWhileTranslating { get; set; } = true;
        public int PreviousTurkishHoldMs { get; set; } = 1200;
        public int MinTurkishDisplayMs { get; set; } = 1700;
        public int MaxTurkishDisplayMs { get; set; } = 5000;
        public bool ShowPendingIndicator { get; set; } = false;
        public bool ShowMaskWhileTranslationPending { get; set; } = true;
        public bool EnableReadableSubtitleTiming { get; set; } = true;
        public int MsPerCharacter { get; set; } = 45;
        public int ExtraLineMs { get; set; } = 350;

        // Part C — ordered subtitle capture queue.
        public int MaxCapturedQueueSize { get; set; } = 12;
        public int CaptureQueueMaxAgeMs { get; set; } = 8000;
        // Lowered from 0.94 for the same OCR-variance reason as
        // SubtitleDedupSimilarityThreshold above: the capture queue must treat a
        // slightly-differently-read repeat of the current on-screen subtitle as
        // the same line so it is not captured and translated twice.
        public double DuplicateSimilarityThreshold { get; set; } = 0.86;
        public bool UpdateLastSeenForDuplicates { get; set; } = true;
        public bool PreserveQueueOrder { get; set; } = true;

        // Part E — ordered translation queue.
        public int OrderedTranslationQueueMaxSize { get; set; } = 8;
        public int MaxConcurrentTranslations { get; set; } = 1;
        public bool DropExpiredBeforeTranslation { get; set; } = true;
        public int MaxAgeBeforeTranslationMs { get; set; } = 6000;
        public bool PreferMemoryAndCacheInstant { get; set; } = true;

        // Part F — translation playback queue.
        public int MaxPlaybackQueueSize { get; set; } = 10;
        public bool ReplaceSameSourceIfBetter { get; set; } = true;
        public bool DoNotSkipReadyTranslations { get; set; } = true;
        public bool DropExpiredTranslations { get; set; } = true;
        public int MaxTranslationAgeForDisplayMs { get; set; } = 7000;

        // Fast-dialogue handling: a translation that finishes shortly after the
        // source line changed is still shown (briefly) instead of being dropped,
        // as long as the newer line's translation is not ready yet.
        public bool EnableStaleTranslationGraceWindow { get; set; } = true;
        public int StaleTranslationGraceMs { get; set; } = 2500;
        // When the playback queue is backed up, the minimum display time shrinks
        // to this floor so bursts drain instead of trimming the oldest lines.
        public int MinTurkishDisplayUnderPressureMs { get; set; } = 900;

        // Part H — subtitle stabilization / multi-line merge.
        // 250 (was 150, briefly 400): games type/scroll their subtitles in over
        // ~1-2s, so a short window dispatched the half-typed, sometimes
        // word-jumbled OCR reads mid-animation — each one translated into
        // garbage. The fix that actually matters is in OrderedSubtitlePipeline:
        // the timer only resets when the captured text GROWS, not on every
        // identical re-read of an already-finished line, so this value only
        // controls how long we wait *after* growth stops before dispatching —
        // it no longer needs to cover the whole typing animation. 250ms keeps
        // that safety margin while cutting the added display latency roughly
        // in half versus 400.
        public int SubtitleStabilizationMs { get; set; } = 250;
        // Total time budget (from the first OCR read of a line) that the pipeline
        // will keep merging growing/continuation reads before flushing for
        // translation. 350ms only covers subtitles that render all at once —
        // games with a "typewriter" reveal effect can take 1-2+ seconds to finish
        // printing a line, so a short window flushes a half-typed sentence,
        // translates it, then flushes the full sentence moments later as a
        // separate line (duplicate/partial translations for the same subtitle).
        // 2500ms still wasn't enough once the OCR+translation round-trip itself
        // eats into the same budget (observed ~1.5-2s per stage on PaddleOCR
        // Server): by the time the "full" reading arrives, the window measured
        // from the *first* partial reading had often already elapsed even though
        // the line was still genuinely the same growing sentence.
        public int MaxSubtitleMergeWindowMs { get; set; } = 6000;
        public bool MergeNearbySubtitleLines { get; set; } = true;

        // OCR noise rejection.
        public bool RejectSingleLetterOcrNoise { get; set; } = true;

        // Ollama refinement (lightweight post-translation improvement).
        public bool EnableOllamaRefinement { get; set; } = false;
        public string OllamaRefinementModel { get; set; } = "qwen3:1.7b";
        public int OllamaRefinementTimeoutMs { get; set; } = 1800;
        public OllamaRefinementMode OllamaRefinementMode { get; set; } = OllamaRefinementMode.ManualOnly;
        public bool ReplaceOverlayWithRefinedTranslation { get; set; } = true;
        public bool SaveRefinedTranslationToCache { get; set; } = true;
        public bool DoNotReplaceIfSubtitleChanged { get; set; } = true;

        // Vision-based game identification (Remote Play / YouTube capture sources,
        // where the captured window title is not a real game title).
        public bool EnableVisionGameDetection { get; set; } = true;
        public string OllamaVisionModel { get; set; } = "gemma3:4b";

        // Local ONNX game classifier gating. The classifier runs first (fast,
        // fully offline); its top-1 softmax probability is only trusted when it
        // clears this threshold. Below it, we fall back to the vision LLM — this
        // is the confidence-gated hybrid the offline benchmarks were built around.
        // ~0.45 keeps the frequent same-studio/same-engine confusions (e.g. Jedi
        // Survivor vs Starfield) from being surfaced as confident guesses.
        public bool EnableOnnxGameDetection { get; set; } = true;
        public double OnnxGameConfidenceThreshold { get; set; } = 0.45;
        // First call after Ollama starts (or switches models) pays a cold-load
        // cost of 30-60s; subsequent calls with the model already resident in
        // memory are much faster. Timeout must cover the worst case, not the
        // typical case.
        public int OllamaVisionTimeoutMs { get; set; } = 90000;

        // Speaker-name aware parsing (Part D): when false (default), two subtitles
        // with the same dialogue but different speakers share one memory/cache entry.
        public bool IncludeSpeakerInMemoryKey { get; set; } = false;

        // A dedicated name-plate crop is more reliable than trying to infer the
        // speaker from the dialogue OCR. Only the region marked as speaker is read.
        public bool EnableSecondarySpeakerOcr { get; set; } = false;
        public bool ShowSpeakerNameInOverlay { get; set; } = true;
        public List<SecondaryOcrRegionSettings> SecondaryOcrRegions { get; set; } = [];

        // Translation Learning & Memory (Part A–N)
        public bool EnableTranslationMemory { get; set; } = true;
        public bool EnableLearningRecords { get; set; } = true;
        public bool UseGlobalTranslationMemoryFallback { get; set; } = true;
        public bool EnableDatasetQualityFilter { get; set; } = true;

        // Ollama async post-edit (separate from synchronous refinement)
        public bool EnableOllamaPostEdit { get; set; } = false;
        public bool ReplaceOverlayWithOllamaPostEdit { get; set; } = false;
        public int OllamaPostEditTimeoutMs { get; set; } = 1800;
        public bool SaveOllamaPostEditToRecord { get; set; } = true;

        // Machine translation server lifecycle management.
        public bool AutoStartMachineTranslationServer { get; set; } = true;
        public bool AutoStartOpusServer
        {
            get => AutoStartMachineTranslationServer;
            set => AutoStartMachineTranslationServer = value;
        }
        public bool StartOpusOnlyWhenSelectedOrFallback { get; set; } = true;
        public int TranslationServerStartupTimeoutMs { get; set; } = 60000;
        public int TranslationServerHealthRetryIntervalMs { get; set; } = 1500;
        public string TranslationServerScriptPath { get; set; } =
            "tools/translation/start_translation_server.ps1";
        public bool ShowTranslationServerConsole { get; set; } = true;

        public List<TranslationProviderType> FallbackProviderOrder { get; set; } =
        [
            TranslationProviderType.MachineTranslation,
            TranslationProviderType.GoogleTranslate,
            TranslationProviderType.DeepL,
            TranslationProviderType.Gemini,
            TranslationProviderType.Groq,
        ];
    }
}
