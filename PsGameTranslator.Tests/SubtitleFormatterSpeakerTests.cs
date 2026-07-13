using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Infrastructure.Subtitles;
using Xunit;

namespace PsGameTranslator.Tests;

public class SubtitleFormatterSpeakerTests
{
    private static SubtitleFormatter CreateFormatter(bool showSpeakerName = true) =>
        new(
            new SubtitleFormatterSettings { ShowSpeakerName = showSpeakerName },
            new SpeakerNameDetector());

    [Fact]
    public async Task MainText_ExcludesSpeakerName()
    {
        var formatter = CreateFormatter();
        var result = await formatter.FormatAsync(
            "Klaus\nGreetings! Welcome to the guild hall.", 0.95, CancellationToken.None);

        Assert.Equal("Klaus", result.SpeakerName);
        Assert.Equal("Greetings! Welcome to the guild hall.", result.MainText);
        Assert.DoesNotContain("Klaus", result.MainText);
    }

    [Fact]
    public async Task SpeakerOnlyBlock_IsEmpty_NeverReachesTranslation()
    {
        var formatter = CreateFormatter();
        var result = await formatter.FormatAsync("Klaus", 0.95, CancellationToken.None);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task HudBlock_DoesNotProduceSpeaker()
    {
        var formatter = CreateFormatter();
        var result = await formatter.FormatAsync(
            "Switch Weapon Skill\nFront Kick\nDash", 0.95, CancellationToken.None);

        // HUD lines are not name plates — nothing here may be treated as a speaker.
        Assert.Equal(string.Empty, result.SpeakerName);
    }

    [Fact]
    public async Task ShowSpeakerNameOff_StillExcludesSpeakerFromMainText()
    {
        var formatter = CreateFormatter(showSpeakerName: false);
        var result = await formatter.FormatAsync(
            "Pearson\nBrought some food back, boys.", 0.95, CancellationToken.None);

        // Even when not displayed, the speaker must never leak into translation input.
        Assert.Equal("Brought some food back, boys.", result.MainText);
        Assert.DoesNotContain("Pearson", result.DisplayText);
    }

    [Fact]
    public async Task HaymishPartialDialogue_IsAcceptedAndSpeakerExcluded()
    {
        var formatter = CreateFormatter();
        var result = await formatter.FormatAsync(
            "Haymish\nI thank you for...", 0.95, CancellationToken.None);

        Assert.False(result.IsEmpty);
        Assert.Equal("Haymish", result.SpeakerName);
        Assert.Equal("I thank you for...", result.MainText);
        Assert.DoesNotContain("Haymish", result.MainText);
    }
}
