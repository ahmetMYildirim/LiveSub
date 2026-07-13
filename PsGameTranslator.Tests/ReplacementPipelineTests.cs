using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Subtitles;
using Xunit;

namespace PsGameTranslator.Tests;

public class ReplacementPipelineTests
{
    // ── Test 1 (Part L): Daniella + dialogue ─────────────────────────────────────

    [Fact]
    public void Daniella_WithDialogue_SpeakerIsNotSentToTranslation()
    {
        var detector = new SpeakerNameDetector();
        var parsed = detector.Parse(
            ["Daniella", "Come to think of it, we're all of differing vocations, aren't we?"],
            "Daniella\nCome to think of it, we're all of differing vocations, aren't we?");

        Assert.Equal("Daniella", parsed.SpeakerName);
        Assert.Equal("Come to think of it, we're all of differing vocations, aren't we?", parsed.DialogueText);
        Assert.DoesNotContain("Daniella", parsed.DialogueText);
    }

    // ── Test 2 (Part L): HUD/control block rejected ──────────────────────────────

    [Theory]
    [InlineData("Sheathe/Draw LT")]
    [InlineData("Switch Weapon Skill RB")]
    [InlineData("Front Kick Y")]
    [InlineData("Dash B")]
    public void HudControlLines_AreRejectedInReplacementMode(string hudLine)
    {
        var validator = new SubtitleCandidateValidator();
        var result = validator.IsValidForReplacementMode(hudLine);

        Assert.False(result.IsValid);
    }

    // ── Test 3 (Part L): same dialogue 5× → one new capture item ─────────────────

    [Fact]
    public void SameDialogueFiveTimes_ProducesOneNewCaptureItem()
    {
        var settings = new TranslationSettings();
        var queue = new OrderedSubtitleCaptureQueue(settings);
        const string dialogue = "Come to think of it, we're all of differing vocations, aren't we?";
        var key = SubtitleTextNormalizer.NormalizeKey(dialogue);

        var newItemCount = 0;
        for (var i = 0; i < 5; i++)
        {
            var result = queue.AddOrUpdate(dialogue, key, "Daniella", i, null);
            if (result.IsNew) newItemCount++;
        }

        // Only the first sighting may reach translation; the rest are duplicates,
        // so the provider can be called at most once for this dialogue.
        Assert.Equal(1, newItemCount);
        Assert.Equal(1, queue.Count);
    }

    // ── Test 4 (Part L): manual replacement region ───────────────────────────────

    [Fact]
    public void ManualRegion_Configured_ProducesExactScreenRect()
    {
        var settings = new SubtitleReplacementOverlaySettings
        {
            UseManualReplacementRegion = true,
            ManualReplacementRegionX = 400,
            ManualReplacementRegionY = 800,
            ManualReplacementRegionWidth = 1100,
            ManualReplacementRegionHeight = 140,
        };

        var rect = ManualReplacementRegionHelper.TryGetScreenRect(
            settings, windowLeft: 100, windowTop: 50, windowWidth: 1920, windowHeight: 1080);

        Assert.NotNull(rect);
        // The region is used exactly as selected, offset by the window position —
        // it never shrinks to the translated-text size.
        Assert.Equal(500, rect!.X);
        Assert.Equal(850, rect.Y);
        Assert.Equal(1100, rect.Width);
        Assert.Equal(140, rect.Height);
    }

    [Fact]
    public void ManualRegion_NotConfigured_ReturnsNull()
    {
        var settings = new SubtitleReplacementOverlaySettings
        {
            UseManualReplacementRegion = true,
            ManualReplacementRegionWidth = 0,
            ManualReplacementRegionHeight = 0,
        };

        Assert.Null(ManualReplacementRegionHelper.TryGetScreenRect(settings, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ManualRegion_Disabled_ReturnsNull_EvenWhenSizeIsSet()
    {
        var settings = new SubtitleReplacementOverlaySettings
        {
            UseManualReplacementRegion = false,
            ManualReplacementRegionWidth = 800,
            ManualReplacementRegionHeight = 120,
        };

        Assert.Null(ManualReplacementRegionHelper.TryGetScreenRect(settings, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ManualRegion_OutsideWindow_IsClampedWithoutResizing()
    {
        var settings = new SubtitleReplacementOverlaySettings
        {
            UseManualReplacementRegion = true,
            ManualReplacementRegionX = 1800,
            ManualReplacementRegionY = 1000,
            ManualReplacementRegionWidth = 600,
            ManualReplacementRegionHeight = 200,
        };

        var rect = ManualReplacementRegionHelper.TryGetScreenRect(settings, 0, 0, 1920, 1080);

        Assert.NotNull(rect);
        Assert.Equal(600, rect!.Width);
        Assert.Equal(200, rect.Height);
        Assert.True(rect.X + rect.Width <= 1920);
        Assert.True(rect.Y + rect.Height <= 1080);
    }

    [Fact]
    public void ManualRegion_IsDefaultOn()
    {
        Assert.True(new SubtitleReplacementOverlaySettings().UseManualReplacementRegion);
    }

    // ── Test 5 (Part L): fast dialogue sequence stays ordered ────────────────────

    [Fact]
    public void FastDialogueSequence_PreservesCaptureOrder()
    {
        var settings = new TranslationSettings();
        var queue = new OrderedSubtitleCaptureQueue(settings);
        string[] lines =
        [
            "Wait, what was that noise?",
            "Someone is coming this way!",
            "Hide behind the crates, quickly!",
        ];

        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
            queue.AddOrUpdate(line, SubtitleTextNormalizer.NormalizeKey(line), string.Empty, index, null);

        var snapshot = queue.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(lines[0], snapshot[0].SourceText);
        Assert.Equal(lines[1], snapshot[1].SourceText);
        Assert.Equal(lines[2], snapshot[2].SourceText);
        Assert.True(snapshot[0].Id < snapshot[1].Id && snapshot[1].Id < snapshot[2].Id);
    }
}
