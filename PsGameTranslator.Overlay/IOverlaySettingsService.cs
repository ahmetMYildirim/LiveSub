using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

public interface IOverlaySettingsService
{
    Task<OverlaySettings> LoadAsync();
    Task SaveAsync(OverlaySettings settings);
}
