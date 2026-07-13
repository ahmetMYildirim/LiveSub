using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PsGameTranslator.App.Services;

/// <summary>
/// Gate for the hidden Egitim (training) page. The page itself only ever
/// appears in the sidebar behind Developer Mode (Ctrl+Shift+D) — this adds a
/// second, independent lock so knowing that shortcut alone isn't enough.
/// Change the PIN by editing DefaultPin below, or by dropping
/// config/training_access.json with a SHA-256 hex hash in "PinHash" (use
/// ComputeHash(yourPin) to generate it) — the file wins if present.
/// </summary>
public sealed class TrainingAccessService
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config", "training_access.json");

    // Change this if you don't want to manage a separate config file.
    private const string DefaultPin = "260726";

    public bool ValidatePin(string? pin) =>
        !string.IsNullOrEmpty(pin) && ComputeHash(pin) == GetStoredHash();

    private static string GetStoredHash()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<TrainingAccessConfig>(json);
                if (!string.IsNullOrWhiteSpace(config?.PinHash))
                    return config.PinHash;
            }
        }
        catch
        {
            // Fall back to the default PIN below.
        }
        return ComputeHash(DefaultPin);
    }

    public static string ComputeHash(string pin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin)));

    private sealed record TrainingAccessConfig(string? PinHash);
}
