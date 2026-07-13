using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using Xunit;

namespace PsGameTranslator.Tests;

public sealed class TranslationContextTests
{
    [Fact]
    public async Task DeepL_SendsPreviousContextInOfficialContextField()
    {
        var handler = new CapturingHandler();
        var settings = new TranslationSettings { DeepLApiKey = "test:fx" };
        var glossary = new GlossaryDictionaryManager(new UserGlossaryRepository(NullLogger<UserGlossaryRepository>.Instance), NullLogger<GlossaryDictionaryManager>.Instance);
        var provider = new DeepLTranslateProvider(settings, new TranslationPostProcessor(glossary, settings), NullLogger<DeepLTranslateProvider>.Instance, handler);
        var result = await provider.TranslateAsync(new TranslationRequest
        {
            SourceText = "The door is open.", SourceLanguage = "en", TargetLanguage = "tr",
            PreviousContextLines = ["The guard warned me.", "We should leave now."]
        });
        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(handler.Body);
        Assert.Equal("The door is open.", payload.RootElement.GetProperty("text")[0].GetString());
        Assert.Equal("The guard warned me.\nWe should leave now.", payload.RootElement.GetProperty("context").GetString());
        Assert.DoesNotContain("The door is open.", payload.RootElement.GetProperty("context").GetString());
    }

    [Fact]
    public void Cache_DifferentContextDoesNotShareSameSource()
    {
        var settings = new TranslationSettings { GameProfile = "context-test" };
        var cache = new TranslationCache(NullLogger<TranslationCache>.Instance, settings); cache.Clear();
        var source = "same source " + Guid.NewGuid();
        cache.Store(Request(source, "first"), "first translation");
        Assert.False(cache.TryGet(Request(source, "second"), out _));
    }

    [Fact]
    public void Cache_SameSourceAndContextSharesEntry()
    {
        var settings = new TranslationSettings { GameProfile = "context-test" };
        var cache = new TranslationCache(NullLogger<TranslationCache>.Instance, settings); cache.Clear();
        var source = "same source " + Guid.NewGuid();
        cache.Store(Request(source, "same context"), "cached translation");
        Assert.True(cache.TryGet(Request(source, "same context"), out var translated));
        Assert.Equal("cached translation", translated);
    }

    [Fact]
    public void QueueContextWindowUsesOnlyBoundedPreviousHistory()
    {
        var context = TranslationContextWindow.Build(["one", "two", "three", "four", "five"], "current");
        Assert.Equal(["three", "four", "five"], context);
        Assert.DoesNotContain("current", context);
    }

    private static TranslationRequest Request(string source, string context) => new()
    {
        SourceText = source, SourceLanguage = "en", TargetLanguage = "tr",
        GameProfileName = "context-test", PreviousContextLines = [context]
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"translations\":[{\"text\":\"Kapı açık.\"}]}")
            };
        }
    }
}