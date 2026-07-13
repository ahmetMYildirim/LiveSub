using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// SQLite-backed translation record repository.
/// All operations open/close their own connection — SQLite connection pooling is automatic.
/// </summary>
public sealed class SqliteTranslationRecordRepository : ITranslationRecordRepository
{
    private static readonly string DbPath = Path.Combine(
        AppContext.BaseDirectory, "data", "translation_memory.db");

    private static string ConnectionString =>
        $"Data Source={DbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True;";

    private readonly ILogger<SqliteTranslationRecordRepository> _logger;

    public SqliteTranslationRecordRepository(ILogger<SqliteTranslationRecordRepository> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    id INTEGER PRIMARY KEY,
                    version INTEGER NOT NULL,
                    applied_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS translation_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_name TEXT NOT NULL DEFAULT 'Default',
                    source_text TEXT NOT NULL,
                    normalized_source_key TEXT NOT NULL,
                    opus_translation TEXT,
                    glossary_translation TEXT,
                    ollama_postedit_translation TEXT,
                    user_correction TEXT,
                    final_translation TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT 'AutoSaved',
                    timestamp TEXT NOT NULL,
                    used_glossary_terms_json TEXT,
                    provider_name TEXT NOT NULL DEFAULT '',
                    source_language TEXT NOT NULL DEFAULT 'en',
                    target_language TEXT NOT NULL DEFAULT 'tr',
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    notes TEXT
                );

                CREATE TABLE IF NOT EXISTS translation_memory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_name TEXT NOT NULL DEFAULT 'Default',
                    normalized_source_key TEXT NOT NULL,
                    source_text TEXT NOT NULL,
                    final_translation TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    usage_count INTEGER NOT NULL DEFAULT 0,
                    last_used_at TEXT,
                    UNIQUE(game_name, normalized_source_key)
                );

                CREATE TABLE IF NOT EXISTS glossary_usage (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    translation_record_id INTEGER NOT NULL,
                    source_term TEXT NOT NULL,
                    target_term TEXT NOT NULL,
                    category TEXT,
                    is_protected INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (translation_record_id) REFERENCES translation_records(id)
                );

                CREATE INDEX IF NOT EXISTS idx_records_norm_key ON translation_records(normalized_source_key);
                CREATE INDEX IF NOT EXISTS idx_records_status ON translation_records(status);
                CREATE INDEX IF NOT EXISTS idx_records_ts ON translation_records(timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_memory_lookup ON translation_memory(game_name, normalized_source_key);

                INSERT OR IGNORE INTO schema_version(id, version, applied_at)
                    VALUES(1, 1, datetime('now'));
                """;
            await cmd.ExecuteNonQueryAsync();

            await MigrateSchemaAsync(conn);

            _logger.LogInformation("Translation learning database initialized at {DbPath}", DbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize translation learning database");
        }
    }

    /// <summary>Schema v2: nullable speaker_name on translation_records (Part G).
    /// ALTER TABLE ADD COLUMN is idempotent-guarded via the version table.</summary>
    private async Task MigrateSchemaAsync(SqliteConnection conn)
    {
        await using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "SELECT version FROM schema_version WHERE id = 1";
        var version = Convert.ToInt32(await versionCmd.ExecuteScalarAsync() ?? 1);

        if (version < 2)
        {
            try
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE translation_records ADD COLUMN speaker_name TEXT";
                await alter.ExecuteNonQueryAsync();
            }
            catch (SqliteException exception) when (
                exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Column already exists (e.g. version row was lost) — safe to continue.
            }

            await using var bump = conn.CreateCommand();
            bump.CommandText = "UPDATE schema_version SET version = 2, applied_at = datetime('now') WHERE id = 1";
            await bump.ExecuteNonQueryAsync();
            _logger.LogInformation("translation_records migrated to schema v2 (speaker_name added)");
        }
    }

    public async Task<long> SaveRecordAsync(TranslationRecord record)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO translation_records
                    (game_name, source_text, normalized_source_key,
                     opus_translation, glossary_translation, ollama_postedit_translation,
                     user_correction, final_translation, status, timestamp,
                     used_glossary_terms_json, provider_name,
                     source_language, target_language, duration_ms, notes, speaker_name)
                VALUES
                    (@game, @src, @norm,
                     @opus, @gloss, @postedit,
                     @usercorr, @final, @status, @ts,
                     @glossterms, @provider,
                     @sl, @tl, @dur, @notes, @speaker);
                SELECT last_insert_rowid();
                """;
            AddParam(cmd, "@game", record.GameName);
            AddParam(cmd, "@src", record.SourceText);
            AddParam(cmd, "@norm", record.NormalizedSourceKey);
            AddParam(cmd, "@opus", (object?)record.OpusTranslation ?? DBNull.Value);
            AddParam(cmd, "@gloss", (object?)record.GlossaryTranslation ?? DBNull.Value);
            AddParam(cmd, "@postedit", (object?)record.OllamaPosteditTranslation ?? DBNull.Value);
            AddParam(cmd, "@usercorr", (object?)record.UserCorrection ?? DBNull.Value);
            AddParam(cmd, "@final", record.FinalTranslation);
            AddParam(cmd, "@status", record.Status.ToString());
            AddParam(cmd, "@ts", record.Timestamp.ToString("o"));
            AddParam(cmd, "@glossterms", (object?)record.UsedGlossaryTermsJson ?? DBNull.Value);
            AddParam(cmd, "@provider", record.ProviderName);
            AddParam(cmd, "@sl", record.SourceLanguage);
            AddParam(cmd, "@tl", record.TargetLanguage);
            AddParam(cmd, "@dur", record.DurationMs);
            AddParam(cmd, "@notes", (object?)record.Notes ?? DBNull.Value);
            AddParam(cmd, "@speaker", (object?)record.SpeakerName ?? DBNull.Value);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveRecordAsync failed");
            return -1;
        }
    }

    public async Task UpdateStatusAsync(long recordId, TranslationRecordStatus status)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE translation_records SET status=@status WHERE id=@id";
            AddParam(cmd, "@status", status.ToString());
            AddParam(cmd, "@id", recordId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpdateStatusAsync failed for id={Id}", recordId); }
    }

    public async Task UpdateUserCorrectionAsync(long recordId, string correctedTranslation, TranslationRecordStatus status)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE translation_records
                SET user_correction=@uc, final_translation=@ft, status=@status
                WHERE id=@id
                """;
            AddParam(cmd, "@uc", correctedTranslation);
            AddParam(cmd, "@ft", correctedTranslation);
            AddParam(cmd, "@status", status.ToString());
            AddParam(cmd, "@id", recordId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpdateUserCorrectionAsync failed for id={Id}", recordId); }
    }

    public async Task UpdatePosteditAsync(long recordId, string postedit, TranslationRecordStatus status)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE translation_records
                SET ollama_postedit_translation=@pe, status=@status
                WHERE id=@id
                """;
            AddParam(cmd, "@pe", postedit);
            AddParam(cmd, "@status", status.ToString());
            AddParam(cmd, "@id", recordId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpdatePosteditAsync failed for id={Id}", recordId); }
    }

    public async Task<IReadOnlyList<TranslationRecord>> GetRecentRecordsAsync(int count)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, game_name, source_text, normalized_source_key,
                       opus_translation, glossary_translation, ollama_postedit_translation,
                       user_correction, final_translation, status, timestamp,
                       used_glossary_terms_json, provider_name,
                       source_language, target_language, duration_ms, notes, speaker_name
                FROM translation_records
                ORDER BY id DESC
                LIMIT @count
                """;
            AddParam(cmd, "@count", count);
            return await ReadRecordsAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecentRecordsAsync failed");
            return [];
        }
    }

    public async Task<IReadOnlyList<TranslationRecord>> GetExportableRecordsAsync(string? gameName)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            if (string.IsNullOrWhiteSpace(gameName))
            {
                cmd.CommandText = """
                    SELECT id, game_name, source_text, normalized_source_key,
                           opus_translation, glossary_translation, ollama_postedit_translation,
                           user_correction, final_translation, status, timestamp,
                           used_glossary_terms_json, provider_name,
                           source_language, target_language, duration_ms, notes, speaker_name
                    FROM translation_records
                    WHERE status IN ('AcceptedByUser', 'EditedByUser')
                    ORDER BY id
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, game_name, source_text, normalized_source_key,
                           opus_translation, glossary_translation, ollama_postedit_translation,
                           user_correction, final_translation, status, timestamp,
                           used_glossary_terms_json, provider_name,
                           source_language, target_language, duration_ms, notes, speaker_name
                    FROM translation_records
                    WHERE status IN ('AcceptedByUser', 'EditedByUser')
                      AND game_name = @game
                    ORDER BY id
                    """;
                AddParam(cmd, "@game", gameName);
            }
            return await ReadRecordsAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetExportableRecordsAsync failed");
            return [];
        }
    }

    public async Task<int> GetCountByStatusAsync(TranslationRecordStatus status)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translation_records WHERE status=@status";
            AddParam(cmd, "@status", status.ToString());
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCountByStatusAsync failed");
            return 0;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<TranslationRecord>> ReadRecordsAsync(SqliteCommand cmd)
    {
        var list = new List<TranslationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new TranslationRecord
            {
                Id = reader.GetInt64(0),
                GameName = reader.GetString(1),
                SourceText = reader.GetString(2),
                NormalizedSourceKey = reader.GetString(3),
                OpusTranslation = reader.IsDBNull(4) ? null : reader.GetString(4),
                GlossaryTranslation = reader.IsDBNull(5) ? null : reader.GetString(5),
                OllamaPosteditTranslation = reader.IsDBNull(6) ? null : reader.GetString(6),
                UserCorrection = reader.IsDBNull(7) ? null : reader.GetString(7),
                FinalTranslation = reader.GetString(8),
                Status = ParseStatus(reader.GetString(9)),
                Timestamp = DateTimeOffset.Parse(reader.GetString(10)),
                UsedGlossaryTermsJson = reader.IsDBNull(11) ? null : reader.GetString(11),
                ProviderName = reader.GetString(12),
                SourceLanguage = reader.GetString(13),
                TargetLanguage = reader.GetString(14),
                DurationMs = reader.GetInt64(15),
                Notes = reader.IsDBNull(16) ? null : reader.GetString(16),
                SpeakerName = reader.IsDBNull(17) ? null : reader.GetString(17),
            });
        }
        return list;
    }

    private static TranslationRecordStatus ParseStatus(string s) =>
        Enum.TryParse<TranslationRecordStatus>(s, out var v) ? v : TranslationRecordStatus.AutoSaved;

    private static void AddParam(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
