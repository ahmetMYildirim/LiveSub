using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PsGameTranslator.App.Services;

/// <summary>Finds a public Steam header image for a selected game and caches it locally.</summary>
public sealed class GameCoverService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "covers");

    public async Task<BitmapSource?> GetCoverAsync(string? gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;
        var cachePath = Path.Combine(CacheDirectory, SanitizeFileName(gameName) + ".jpg");
        try
        {
            if (File.Exists(cachePath)) return LoadBitmap(await File.ReadAllBytesAsync(cachePath, cancellationToken));

            var url = "https://store.steampowered.com/api/storesearch/?cc=us&l=en&term=" + Uri.EscapeDataString(gameName);
            using var response = await Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
            var appId = items[0].GetProperty("id").GetInt32();
            var imageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
            var bytes = await Client.GetByteArrayAsync(imageUrl, cancellationToken);
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            return LoadBitmap(bytes);
        }
        catch
        {
            return null; // Cover art is cosmetic; the live capture preview remains the fallback.
        }
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
