using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Infrastructure.Subtitles;
using Xunit;

namespace PsGameTranslator.Tests;

public class SpeakerNameDetectorTests
{
    private readonly SpeakerNameDetector _detector = new();

    // ── Test 1 (Part K): Klaus + dialogue ────────────────────────────────────────

    [Fact]
    public void Klaus_WithDialogue_SplitsSpeakerFromDialogue()
    {
        var parsed = _detector.Parse(
            ["Klaus", "Greetings! Welcome to the guild hall."],
            "Klaus\nGreetings! Welcome to the guild hall.");

        Assert.Equal("Klaus", parsed.SpeakerName);
        Assert.Equal("Greetings! Welcome to the guild hall.", parsed.DialogueText);
        Assert.False(parsed.IsRejected);
        // Translation input (DialogueText) must exclude the speaker.
        Assert.DoesNotContain("Klaus", parsed.DialogueText);
    }

    [Fact]
    public void Klaus_WithMultiLineDialogue_JoinsDialogueLines()
    {
        var parsed = _detector.Parse(
            [
                "Klaus",
                "Greetings! Welcome to the guild hall.",
                "Here we conduct all manner of procedures pertaining to vocations.",
            ],
            string.Empty);

        Assert.Equal("Klaus", parsed.SpeakerName);
        Assert.Equal(
            "Greetings! Welcome to the guild hall. Here we conduct all manner of procedures pertaining to vocations.",
            parsed.DialogueText);
    }

    // ── Test 2 (Part K): Pearson + dialogue ──────────────────────────────────────

    [Fact]
    public void Pearson_WithDialogue_SplitsSpeakerFromDialogue()
    {
        var parsed = _detector.Parse(
            ["Pearson", "Brought some food back, boys."],
            "Pearson\nBrought some food back, boys.");

        Assert.Equal("Pearson", parsed.SpeakerName);
        Assert.Equal("Brought some food back, boys.", parsed.DialogueText);
    }

    [Theory]
    [InlineData("Abigail Roberts")]
    [InlineData("Captain Brant")]
    [InlineData("Guild Master")]
    public void MultiWordProperNames_AreDetectedAsSpeaker(string name)
    {
        var parsed = _detector.Parse(
            [name, "We should get moving before nightfall."],
            string.Empty);

        Assert.Equal(name, parsed.SpeakerName);
        Assert.DoesNotContain(name, parsed.DialogueText);
    }

    // ── Test 3 (Part K): HUD/control lines never become speaker ──────────────────

    [Theory]
    [InlineData("Switch Weapon Skill")]
    [InlineData("Sheathe/Draw")]
    [InlineData("Front Kick")]
    [InlineData("Dash")]
    [InlineData("Grab")]
    [InlineData("Jump")]
    [InlineData("LT")]
    [InlineData("RB")]
    [InlineData("Press R")]
    [InlineData("Open your inventory")]
    public void HudControlLines_AreNotSpeakerNames(string hudLine)
    {
        var (isSpeaker, _) = _detector.IsSpeakerNameLine(hudLine, "Some dialogue line below it.");
        Assert.False(isSpeaker);
    }

    // ── Interjections are dialogue, never speaker names ─────────────────────────

    [Theory]
    [InlineData("Go!")]
    [InlineData("Wait!")]
    [InlineData("Help!")]
    [InlineData("Stop!")]
    [InlineData("To me!")]
    public void Interjections_AreNotSpeakerNames(string interjection)
    {
        var (isSpeaker, _) = _detector.IsSpeakerNameLine(interjection, "Something else.");
        Assert.False(isSpeaker);
    }

    [Fact]
    public void SingleDialogueLine_WithoutSpeaker_IsAllDialogue()
    {
        var parsed = _detector.Parse(["We have to move, now!"], string.Empty);

        Assert.Null(parsed.SpeakerName);
        Assert.Equal("We have to move, now!", parsed.DialogueText);
    }

    [Fact]
    public void SpeakerOnly_NoDialogue_IsRejected()
    {
        var parsed = _detector.Parse(["Klaus"], "Klaus");

        Assert.True(parsed.IsRejected);
        Assert.Equal("speaker_only_no_dialogue", parsed.RejectionReason);
        Assert.Equal(string.Empty, parsed.DialogueText);
    }

    // ── Test 4 (Part K): OCR garbage ─────────────────────────────────────────────

    [Fact]
    public void ArudI_IsRejectedAsOcrGarbage_ByValidator()
    {
        var validator = new SubtitleCandidateValidator();
        var result = validator.Validate("ARUD I");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ArudI_IsNotASpeakerName()
    {
        var (isSpeaker, _) = _detector.IsSpeakerNameLine("ARUD I", "Some dialogue.");
        Assert.False(isSpeaker);
    }

    // ── Test 5 (Part K): dataset source_text never contains the speaker ─────────

    [Fact]
    public void DialogueText_UsedAsDatasetSource_NeverContainsSpeaker()
    {
        var parsed = _detector.Parse(
            ["Klaus", "Greetings! Welcome to the guild hall."],
            "Klaus\nGreetings! Welcome to the guild hall.");

        // OrderedSubtitlePipeline saves item.SourceText (= DialogueText) as the
        // learning record's source_text; the speaker travels separately.
        var datasetSourceText = parsed.DialogueText;
        Assert.False(datasetSourceText.Contains("Klaus", StringComparison.OrdinalIgnoreCase));
        Assert.False(datasetSourceText.Contains('\n'));
    }
}
