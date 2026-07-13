using Microsoft.Extensions.Logging.Abstractions;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Ocr;
using Xunit;

namespace PsGameTranslator.Tests;

/// <summary>
/// OcrEngineManager selection semantics: the selected provider must be the one
/// used, otherwise the failure or fallback must be explicit — never silent.
/// </summary>
public sealed class ProviderSelectionTests
{
    private static OcrEngineManager CreateManager(OcrEngineSettings settings, params IOcrProvider[] providers) =>
        new(providers, settings, new OcrResultScorer(), NullLogger<OcrEngineManager>.Instance);

    private static OcrRequest Request() => new()
    {
        ImageBytes = new byte[] { 1, 2, 3 },
        Language = "en",
        RegionId = "test",
    };

    [Fact]
    public async Task SelectedProviderUnavailable_FallbackDisabled_FailsExplicitly()
    {
        var settings = new OcrEngineSettings
        {
            Profile = OcrProfile.Custom,
            PreferredProvider = OcrProviderType.EasyOCR,
            EnableOcrProviderFallback = false,
        };
        var manager = CreateManager(
            settings,
            new MockOcrProvider(),
            new UnavailableOcrProvider(
                "EasyOCR", OcrProviderType.EasyOCR, "EasyOCR is not installed on the OCR server."));

        var result = await manager.RecognizeAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("EasyOCR", result.ErrorMessage);
        Assert.False(manager.LastFallbackUsed);
        Assert.Equal("none", manager.LastProviderUsed);
    }

    [Fact]
    public async Task SelectedProviderUnavailable_FallbackEnabled_UsesFallbackAndReportsIt()
    {
        var settings = new OcrEngineSettings
        {
            Profile = OcrProfile.Custom,
            PreferredProvider = OcrProviderType.EasyOCR,
            EnableOcrProviderFallback = true,
        };
        var manager = CreateManager(
            settings,
            new MockOcrProvider(),
            new UnavailableOcrProvider(
                "EasyOCR", OcrProviderType.EasyOCR, "EasyOCR is not installed on the OCR server."));

        var result = await manager.RecognizeAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("MockOCR", result.ProviderName);
        Assert.True(manager.LastFallbackUsed);
        Assert.Contains("EasyOCR", manager.LastFallbackReason);
    }

    [Fact]
    public async Task SelectedMockProvider_IsUsedDirectly_NoFallbackFlag()
    {
        var settings = new OcrEngineSettings
        {
            Profile = OcrProfile.Custom,
            PreferredProvider = OcrProviderType.MockOCR,
            EnableOcrProviderFallback = false,
        };
        var manager = CreateManager(settings, new MockOcrProvider("Hello there."));

        var result = await manager.RecognizeAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("MockOCR", result.ProviderName);
        Assert.Equal("Hello there.", result.Text);
        Assert.False(manager.LastFallbackUsed);
        Assert.Equal("MockOCR", manager.LastProviderUsed);
    }
}
