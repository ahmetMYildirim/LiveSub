using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Region;

public sealed class RegionPersistenceService : IRegionPersistenceService
{
    private readonly ILogger<RegionPersistenceService> _logger;

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config", "region.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public RegionPersistenceService(ILogger<RegionPersistenceService> logger)
    {
        _logger = logger;
    }

    public async Task SaveAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        var dto = new RegionDto { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        await File.WriteAllTextAsync(ConfigPath, json, Encoding.UTF8, cancellationToken);
        _logger.LogInformation("Region saved to {Path}", ConfigPath);
    }

    public async Task<CaptureRegion?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPath))
            return null;

        var json = await File.ReadAllTextAsync(ConfigPath, Encoding.UTF8, cancellationToken);
        var dto = JsonSerializer.Deserialize<RegionDto>(json);
        if (dto is null)
            return null;

        return new CaptureRegion { X = dto.X, Y = dto.Y, Width = dto.Width, Height = dto.Height };
    }

    private sealed class RegionDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
