using Microsoft.Extensions.Logging.Abstractions;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Overlay;
using Xunit;

namespace PsGameTranslator.Tests;

/// <summary>
/// Behavior tests for TranslationPlaybackQueue: fast-dialogue grace window,
/// stale-translation dropping, and the anti-English replacement guard.
/// </summary>
public sealed class PlaybackQueueTests
{
    private sealed class FakeOverlayService : IOverlayService
    {
        private readonly List<SubtitleReplacementOverlayUpdate> _replacementUpdates = [];
        private readonly List<string> _textUpdates = [];
        private readonly object _gate = new();

        public bool IsOpen => true;
        public OverlaySettings CurrentSettings { get; } = new()
        {
            DisplayMode = SubtitleDisplayMode.SubtitleReplacementOverlay,
        };
        public SubtitleReplacementOverlaySnapshot? LastReplacementSnapshot => null;

        public IReadOnlyList<SubtitleReplacementOverlayUpdate> ReplacementUpdates
        {
            get { lock (_gate) return _replacementUpdates.ToArray(); }
        }

        public void Open(OverlaySettings settings) { }
        public void Close() { }
        public void UpdateText(string text) { lock (_gate) _textUpdates.Add(text); }
        public void UpdateReplacementOverlay(SubtitleReplacementOverlayUpdate update)
        {
            lock (_gate) _replacementUpdates.Add(update);
        }
        public void ApplySettings(OverlaySettings settings) { }
        public void EnterConfigMode() { }
        public (double X, double Y, double Width, double Height) ExitConfigMode() => (0, 0, 0, 0);

        public bool WaitForTranslatedText(string text, int timeoutMs = 3000)
        {
            var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.Now < deadline)
            {
                lock (_gate)
                {
                    if (_replacementUpdates.Any(u => !u.ShowMaskOnly && u.Text == text))
                        return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        public bool WaitForMaskOnly(int timeoutMs = 3000)
        {
            var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.Now < deadline)
            {
                lock (_gate)
                {
                    if (_replacementUpdates.Any(u => u.ShowMaskOnly))
                        return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }
    }

    private static (TranslationPlaybackQueue Queue, FakeOverlayService Overlay, TranslationSettings Settings) CreateQueue()
    {
        var overlay = new FakeOverlayService();
        var settings = new TranslationSettings
        {
            TurkishOnlyMode = true,
            ShowMaskWhileTranslationPending = true,
            EnableStaleTranslationGraceWindow = true,
            StaleTranslationGraceMs = 2500,
            MinTurkishDisplayMs = 200,
            DropExpiredTranslations = true,
            MaxTranslationAgeForDisplayMs = 60_000,
        };
        var diagnostics = new PipelineDiagnostics();
        var store = new PipelineDiagnosticsStore(diagnostics, NullLogger<PipelineDiagnosticsStore>.Instance);
        var queue = new TranslationPlaybackQueue(
            overlay, settings, new SubtitleFormatterSettings(), diagnostics, store,
            NullLogger<TranslationPlaybackQueue>.Instance);
        return (queue, overlay, settings);
    }

    private static SubtitleReplacementContext Context() => new()
    {
        ScreenRect = new OverlayRectangle { X = 100, Y = 500, Width = 600, Height = 60 },
        OverlayRect = new OverlayRectangle { X = 100, Y = 500, Width = 600, Height = 60 },
    };

    private static TranslatedSubtitleDisplayItem Item(
        string sourceText, string key, string turkish, DateTimeOffset? createdAt = null) => new()
    {
        SourceText = sourceText,
        NormalizedSourceKey = key,
        TranslatedText = turkish,
        CreatedAt = createdAt ?? DateTimeOffset.Now,
        ReplacementContext = Context(),
        DisplayDurationMs = 200,
    };

    [Fact]
    public void FreshStaleTranslation_WithinGraceWindow_IsStillDisplayed()
    {
        var (queue, overlay, _) = CreateQueue();
        using var _1 = queue;

        // Line 1 appears, then line 2 supersedes it before line 1's translation returns.
        queue.NotifyActivity("wait!", "Wait!", Context());
        queue.NotifyActivity("run, quickly!", "Run, quickly!", Context());

        // Line 1's translation arrives late but fresh — must be shown, not dropped.
        queue.Enqueue(Item("Wait!", "wait!", "Bekle!"));

        Assert.True(
            overlay.WaitForTranslatedText("Bekle!"),
            "a translation finishing within the grace window should still be displayed");
    }

    [Fact]
    public void OldStaleTranslation_BeyondGraceWindow_IsDropped()
    {
        var (queue, overlay, _) = CreateQueue();
        using var _1 = queue;

        queue.NotifyActivity("wait!", "Wait!", Context());
        queue.NotifyActivity("run, quickly!", "Run, quickly!", Context());

        queue.Enqueue(Item("Wait!", "wait!", "Bekle!", DateTimeOffset.Now.AddSeconds(-10)));

        Assert.False(
            overlay.WaitForTranslatedText("Bekle!", timeoutMs: 1000),
            "a translation older than the grace window must not be displayed");
    }

    [Fact]
    public void StaleTranslation_WhenNewerTranslationReady_IsDropped()
    {
        var (queue, overlay, _) = CreateQueue();
        using var _1 = queue;

        queue.NotifyActivity("wait!", "Wait!", Context());
        queue.NotifyActivity("run, quickly!", "Run, quickly!", Context());

        // The current line's translation is already ready — the old line must yield.
        queue.Enqueue(Item("Run, quickly!", "run, quickly!", "Koş, çabuk!"));
        queue.Enqueue(Item("Wait!", "wait!", "Bekle!"));

        Assert.True(overlay.WaitForTranslatedText("Koş, çabuk!"));
        Assert.False(
            overlay.WaitForTranslatedText("Bekle!", timeoutMs: 700),
            "a stale translation must not displace the current line's ready translation");
    }

    [Fact]
    public void ShortProperNounLine_EqualToSource_IsDisplayed()
    {
        var (queue, overlay, _) = CreateQueue();
        using var _1 = queue;

        queue.NotifyActivity("haymish!", "Haymish!", Context());
        queue.Enqueue(Item("Haymish!", "haymish!", "Haymish!"));

        Assert.True(
            overlay.WaitForTranslatedText("Haymish!"),
            "short proper-noun lines whose Turkish equals the source must not be blocked");
    }

    [Fact]
    public void LongLine_EqualToSource_IsBlockedAsEnglish()
    {
        var (queue, overlay, _) = CreateQueue();
        using var _1 = queue;

        const string source = "I have never seen such a beast before.";
        queue.NotifyActivity("i have never seen such a beast before.", source, Context());
        queue.Enqueue(Item(source, "i have never seen such a beast before.", source));

        Assert.False(
            overlay.WaitForTranslatedText(source, timeoutMs: 1000),
            "long untranslated text equal to the source must be blocked in replacement mode");
    }
}
