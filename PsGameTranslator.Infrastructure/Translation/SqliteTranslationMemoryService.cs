using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// SQLite-backed translation memory. Only accepted/edited records are stored.
/// </summary>
public sealed class SqliteTranslationMemoryService : ITranslationMemoryService
{
    private static readonly string DbPath = Path.Combine(
        AppContext.BaseDirectory, "data", "translation_memory.db");

    private static string ConnectionString =>
        $"Data Source={DbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True;";

    private const string GlobalGameName = "__global__";

    private readonly ILogger<SqliteTranslationMemoryService> _logger;

    public SqliteTranslationMemoryService(ILogger<SqliteTranslationMemoryService> logger)
    {
        _logger = logger;
    }

    public string NormalizeSourceKey(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return string.Empty;
        var normalized = sourceText.ToLowerInvariant().Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized;
    }

    public async Task<TranslationMemoryEntry?> LookupAsync(
        string gameName, string sourceText, bool useGlobalFallback)
    {
        var key = NormalizeSourceKey(sourceText);
        if (string.IsNullOrEmpty(key)) return null;

        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            // Try game-specific first
            var entry = await QueryMemoryAsync(conn, gameName, key);
            if (entry is not null) return entry;

            // Fallback to global
            if (useGlobalFallback && !string.Equals(gameName, GlobalGameName, StringComparison.Ordinal))
                return await QueryMemoryAsync(conn, GlobalGameName, key);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TranslationMemory lookup failed for key={Key}", key);
            return null;
        }
    }

    public async Task UpsertAsync(string gameName, TranslationRecord record)
    {
        if (record.Status is not TranslationRecordStatus.AcceptedByUser
            and not TranslationRecordStatus.EditedByUser)
            return;

        // Prefer the key stored on the record — it may include the speaker segment
        // when IncludeSpeakerInMemoryKey was enabled at capture time (Part D).
        var key = string.IsNullOrWhiteSpace(record.NormalizedSourceKey)
            ? NormalizeSourceKey(record.SourceText)
            : record.NormalizedSourceKey;
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(record.FinalTranslation))
            return;

        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO translation_memory
                    (game_name, normalized_source_key, source_text, final_translation,
                     status, created_at, updated_at, usage_count)
                VALUES
                    (@game, @key, @src, @final,
                     @status, datetime('now'), datetime('now'), 0)
                ON CONFLICT(game_name, normalized_source_key) DO UPDATE SET
                    final_translation = excluded.final_translation,
                    status = excluded.status,
                    updated_at = datetime('now')
                """;
            AddParam(cmd, "@game", gameName);
            AddParam(cmd, "@key", key);
            AddParam(cmd, "@src", record.SourceText);
            AddParam(cmd, "@final", record.FinalTranslation);
            AddParam(cmd, "@status", record.Status.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TranslationMemory upsert failed for key={Key}", key);
        }
    }

    public async Task IncrementUsageAsync(long memoryEntryId)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE translation_memory
                SET usage_count = usage_count + 1, last_used_at = datetime('now')
                WHERE id = @id
                """;
            AddParam(cmd, "@id", memoryEntryId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "IncrementUsage failed for id={Id}", memoryEntryId); }
    }

    public async Task<int> GetEntryCountAsync()
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translation_memory";
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        catch { return 0; }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<TranslationMemoryEntry?> QueryMemoryAsync(
        SqliteConnection conn, string gameName, string normalizedKey)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_name, normalized_source_key, source_text, final_translation,
                   status, created_at, updated_at, usage_count, last_used_at
            FROM translation_memory
            WHERE game_name = @game AND normalized_source_key = @key
            LIMIT 1
            """;
        AddParam(cmd, "@game", gameName);
        AddParam(cmd, "@key", normalizedKey);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new TranslationMemoryEntry
        {
            Id = reader.GetInt64(0),
            GameName = reader.GetString(1),
            NormalizedSourceKey = reader.GetString(2),
            SourceText = reader.GetString(3),
            FinalTranslation = reader.GetString(4),
            Status = ParseStatus(reader.GetString(5)),
            CreatedAt = ParseDate(reader.GetString(6)),
            UpdatedAt = ParseDate(reader.GetString(7)),
            UsageCount = reader.GetInt32(8),
            LastUsedAt = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
        };
    }

    private static TranslationRecordStatus ParseStatus(string s) =>
        Enum.TryParse<TranslationRecordStatus>(s, out var v) ? v : TranslationRecordStatus.AcceptedByUser;

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.Now;

    private static void AddParam(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
