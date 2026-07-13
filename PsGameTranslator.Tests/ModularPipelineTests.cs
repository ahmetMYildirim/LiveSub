using Microsoft.Extensions.Logging.Abstractions;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Ocr;
using Xunit;

namespace PsGameTranslator.Tests;

public sealed class ModularPipelineTests
{
    [Fact]
    public void OcrScorerRejectsHudControlsAndSelectsSubtitle()
    {
        var scorer = new OcrResultScorer();
        var hud = new OcrResult
        {
            ProviderName = "PaddleOCR",
            Text = "Sheathe/Draw LT\nSwitch Weapon Skill RB\nFront Kick Y\nDash B",
            Confidence = 0.95,
            Lines =
            [
                new OcrLine { Text = "Sheathe/Draw LT", Confidence = 0.95 },
                new OcrLine { Text = "Switch Weapon Skill RB", Confidence = 0.95 },
            ],
        };
        var subtitle = new OcrResult
        {
            ProviderName = "WindowsOCR",
            Text = "Greetings! Welcome to the guild hall.",
            Confidence = 0.82,
            Lines = [new OcrLine { Text = "Greetings! Welcome to the guild hall.", Confidence = 0.82 }],
        };

        var selection = scorer.SelectBest([hud, subtitle]);

        Assert.Equal("Greetings! Welcome to the guild hall.", selection.DialogueText);
        Assert.Equal("WindowsOCR", selection.BestResult.ProviderName);
        Assert.Contains(selection.RejectedResults, r => r.Reason.Contains("hud_control_text"));
    }

    [Fact]
    public void OcrScorerSplitsSpeakerFromDialogue()
    {
        var scorer = new OcrResultScorer();
        var result = new OcrResult
        {
            ProviderName = "PaddleOCR",
            Text = "Daniella\nCome to think of it, we're all of differing vocations, aren't we?",
            Confidence = 0.9,
            Lines =
            [
                new OcrLine { Text = "Daniella", Confidence = 0.95 },
                new OcrLine { Text = "Come to think of it, we're all of differing vocations, aren't we?", Confidence = 0.9 },
            ],
        };

        var selection = scorer.SelectBest([result]);

        Assert.Equal("Daniella", selection.SpeakerName);
        Assert.Equal("Come to think of it, we're all of differing vocations, aren't we?", selection.DialogueText);
    }

    [Fact]
    public void PostProcessorAppliesPreferredRpgPhrase()
    {
        var userRepo = new UserGlossaryRepository(
            NullLogger<UserGlossaryRepository>.Instance);
        var glossary = new GlossaryDictionaryManager(
            userRepo,
            NullLogger<GlossaryDictionaryManager>.Instance);
        var processor = new TranslationPostProcessor(glossary, new TranslationSettings());

        var output = processor.Process(
            "Come to think of it, we're all of differing vocations, aren't we?",
            "Yanlış çıktı");

        Assert.Equal("Düşününce, hepimiz farklı mesleklerdeniz, değil mi?", output);
    }
}
