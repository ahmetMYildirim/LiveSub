using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Subtitles;

public sealed class SubtitleCaptureAddResult
{
    public required CapturedSubtitleItem Item { get; init; }
    public bool IsNew { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Captures every valid OCR subtitle candidate in arrival order, deduplicating
/// repeated OCR frames of the same line without ever overwriting a previous,
/// still-pending item with a newer, unrelated one (Part C).
/// </summary>
public sealed class OrderedSubtitleCaptureQueue
{
    private readonly TranslationSettings _settings;
    private readonly object _gate = new();
    private readonly List<CapturedSubtitleItem> _items = [];
    private long _nextId = 1;

    public OrderedSubtitleCaptureQueue(TranslationSettings settings)
    {
        _settings = settings;
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    public SubtitleCaptureAddResult AddOrUpdate(
        string sourceText,
        string normalizedKey,
        string speakerName,
        long frameNumber,
        SubtitleReplacementContext? replacementContext)
    {
        lock (_gate)
        {
            TrimExpiredUnsafe();

            var threshold = Math.Clamp(_settings.DuplicateSimilarityThreshold, 0.5, 1.0);
            foreach (var existing in _items)
            {
                var isDuplicate = existing.NormalizedSourceKey == normalizedKey ||
                    SubtitleTranslationQueue.Similarity(normalizedKey, existing.NormalizedSourceKey) >= threshold;
                if (!isDuplicate) continue;

                if (_settings.UpdateLastSeenForDuplicates)
                    existing.LastSeenAt = DateTimeOffset.Now;
                if (replacementContext is not null)
                    existing.ReplacementContext = replacementContext.Clone();
                return new SubtitleCaptureAddResult
                {
                    Item = existing,
                    IsNew = false,
                    Reason = "duplicate_updated_last_seen",
                };
            }

            var item = new CapturedSubtitleItem
            {
                Id = _nextId++,
                SourceText = sourceText,
                NormalizedSourceKey = normalizedKey,
                SpeakerName = speakerName,
                FrameNumber = frameNumber,
                ReplacementContext = replacementContext?.Clone(),
            };
            _items.Add(item);

            // Preserve order: only expired or duplicate items are ever dropped.
            // If nothing has expired yet, we still cap growth by dropping the
            // single oldest entry rather than losing the newest dialogue line.
            while (_items.Count > Math.Max(1, _settings.MaxCapturedQueueSize))
            {
                var expiredIndex = _items.FindIndex(i => i.AgeMs > _settings.CaptureQueueMaxAgeMs);
                _items.RemoveAt(expiredIndex >= 0 ? expiredIndex : 0);
            }

            return new SubtitleCaptureAddResult { Item = item, IsNew = true, Reason = "captured" };
        }
    }

    public void MarkStatus(long id, CapturedSubtitleStatus status)
    {
        lock (_gate)
        {
            var item = _items.Find(i => i.Id == id);
            if (item is not null) item.Status = status;
        }
    }

    public void Remove(long id)
    {
        lock (_gate) { _items.RemoveAll(i => i.Id == id); }
    }

    public IReadOnlyList<CapturedSubtitleItem> GetSnapshot()
    {
        lock (_gate) { return _items.Select(Clone).ToList(); }
    }

    private void TrimExpiredUnsafe()
    {
        // Regardless of status, an item older than the age window no longer
        // protects against duplicate OCR frames and would otherwise grow
        // unbounded over a long play session.
        _items.RemoveAll(i => i.AgeMs > _settings.CaptureQueueMaxAgeMs);
    }

    private static CapturedSubtitleItem Clone(CapturedSubtitleItem i) => new()
    {
        Id = i.Id,
        SourceText = i.SourceText,
        NormalizedSourceKey = i.NormalizedSourceKey,
        SpeakerName = i.SpeakerName,
        FirstSeenAt = i.FirstSeenAt,
        LastSeenAt = i.LastSeenAt,
        FrameNumber = i.FrameNumber,
        Status = i.Status,
        TranslationRecordId = i.TranslationRecordId,
        FromMemory = i.FromMemory,
        FromCache = i.FromCache,
        ReplacementContext = i.ReplacementContext?.Clone(),
    };
}
