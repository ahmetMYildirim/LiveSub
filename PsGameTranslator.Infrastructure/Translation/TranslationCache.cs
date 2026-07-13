using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation
{
    public sealed class TranslationCache
    {
        private static readonly string CachePath = Path.Combine(AppContext.BaseDirectory, "config", "translation_cache.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ILogger<TranslationCache> _logger;
        private readonly TranslationSettings _settings;
        private readonly object _gate = new();
        private Dictionary<string, string>? _entries;

        // Store() used to rewrite the entire cache file synchronously on every
        // translated subtitle (inside the lock) — O(cache size) disk work per
        // line. Lookups are RAM-only; persistence is debounced to this timer.
        private readonly Timer _flushTimer;
        private bool _dirty;

        public TranslationCache(ILogger<TranslationCache> logger, TranslationSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _flushTimer = new Timer(_ => FlushIfDirty(), null,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }

        /// <summary>Writes pending changes to disk. Call on app shutdown.</summary>
        public void Flush() => FlushIfDirty();

        private void FlushIfDirty()
        {
            lock (_gate)
            {
                if (!_dirty || _entries is null) return;
                Persist();
                _dirty = false;
            }
        }

        public int Count
        {
            get { lock (_gate) { return Load().Count; } }
        }

        public bool TryGet(TranslationRequest request, out string translated)
        {
            lock (_gate)
            {
                return Load().TryGetValue(BuildKey(request), out translated!);
            }
        }

        public void Store(TranslationRequest request, string translated)
        {
            lock (_gate)
            {
                Load()[BuildKey(request)] = translated;
                _dirty = true; // persisted by the debounce timer
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Load().Clear();
                Persist();
                _logger.LogInformation("Translation cache cleared");
            }
        }

        // ------------ Key -----------------------------------------------

        // Speaker name is excluded from the key by default (Part D) so the same
        // dialogue spoken by different characters shares one cached translation.
        private string BuildKey(TranslationRequest request)
        {
            var speakerSegment = _settings.IncludeSpeakerInMemoryKey &&
                !string.IsNullOrWhiteSpace(request.SpeakerName)
                    ? Normalize(request.SpeakerName) + "|"
                    : string.Empty;
            var context = TranslationContextWindow.Join(request.PreviousContextLines);
            var contextFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
            return $"{_settings.ProviderType}|{_settings.Profile}|{request.GameProfile}|{request.SourceLanguage}|{request.TargetLanguage}|{speakerSegment}ctx={contextFingerprint}|{Normalize(request.SourceText)}";
        }

        private static string Normalize(string key) =>
            SubtitleTextNormalizer.NormalizeKey(key);

        // ------------ Persistence (call only under _gate)----------------
        private Dictionary<string, string> Load()
        {
            if (_entries != null) return _entries;

            try
            {
                if (File.Exists(CachePath))
                {
                    var json = File.ReadAllText(CachePath, Encoding.UTF8);
                    _entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    _logger.LogInformation(
                        "Translation Cache Loaded - {Count} entries from {Path}", _entries.Count, CachePath
                        );
                }
                else
                {
                    _entries = new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load translation cache - starting empty");
                _entries = new Dictionary<string, string>();
            }
            return _entries;
        }

        private void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(
                    CachePath,
                    JsonSerializer.Serialize(_entries, JsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist translation cache to {Path}", CachePath);
            }
        }
    }
}
