using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Region;

public interface IRegionPersistenceService
{
    Task SaveAsync(CaptureRegion region, CancellationToken cancellationToken = default);
    Task<CaptureRegion?> LoadAsync(CancellationToken cancellationToken = default);
}
