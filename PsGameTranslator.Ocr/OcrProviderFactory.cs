using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

/// <summary>
/// Single lookup point for OCR providers by engine type. Never substitutes a
/// different engine: when the requested engine has no usable provider, the
/// caller gets null plus a reason — fallback decisions stay with the caller.
/// </summary>
public sealed class OcrProviderFactory
{
    private readonly IReadOnlyList<IOcrProvider> _providers;

    public OcrProviderFactory(IEnumerable<IOcrProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IReadOnlyList<IOcrProvider> All => _providers;

    public IReadOnlyList<IOcrProvider> GetAll(OcrProviderType providerType) =>
        _providers.Where(provider => provider.ProviderType == providerType).ToArray();

    /// <summary>
    /// Best provider of the requested engine type: available first, then the
    /// low-latency server transport over subprocess. Returns null (with reason)
    /// when the engine is not usable — never a different engine.
    /// </summary>
    public (IOcrProvider? Provider, string Reason) GetBest(OcrProviderType providerType)
    {
        var candidates = _providers
            .Where(provider => provider.ProviderType == providerType)
            .OrderByDescending(provider => provider.IsAvailable)
            .ThenByDescending(provider => provider.Name.Contains("Server", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
            return (null, $"{providerType} provider is not registered.");

        var available = candidates.FirstOrDefault(provider => provider.IsAvailable);
        if (available is not null)
            return (available, string.Empty);

        var reason = candidates[0] is UnavailableOcrProvider unavailable
            ? unavailable.Message
            : $"{providerType} provider is not available (server not running or engine not installed).";
        return (null, reason);
    }
}
