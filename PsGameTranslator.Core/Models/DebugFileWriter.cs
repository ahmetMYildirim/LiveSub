using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PsGameTranslator.Core.Models;

/// <summary>
/// Best-effort background writer for debug snapshot files.
///
/// The live pipeline used to serialize + write JSON diagnostics synchronously
/// several times per subtitle (some inside locks), adding tens of milliseconds
/// of disk latency per line. This writer keeps only the LATEST payload per
/// file and flushes on a background loop, so the hot path pays a dictionary
/// insert instead of a disk write. Files still end up with current content —
/// intermediate states nobody could read anyway are skipped.
/// </summary>
public static class DebugFileWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> _pending = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(300);

    static DebugFileWriter()
    {
        var thread = new Thread(FlushLoop)
        {
            IsBackground = true,
            Name = "debug-file-writer",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Queues a payload to be serialized and written to <paramref name="path"/>.
    /// The payload should be an immutable snapshot (anonymous object / record).</summary>
    public static void Queue(string path, object payload) => _pending[path] = payload;

    /// <summary>Queues pre-rendered text content.</summary>
    public static void QueueText(string path, string content) => _pending[path] = content;

    /// <summary>Drop-in replacement for File.WriteAllText(path, content, encoding)
    /// call sites — files are always written UTF-8 (no BOM); the encoding argument
    /// exists only to keep the signature compatible.</summary>
    public static void QueueText(string path, string content, Encoding _) => _pending[path] = content;

    /// <summary>Writes everything still pending (used by tests / shutdown).</summary>
    public static void FlushNow()
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (!_pending.TryRemove(key, out var payload)) continue;
            WriteOne(key, payload);
        }
    }

    private static void FlushLoop()
    {
        while (true)
        {
            try
            {
                FlushNow();
            }
            catch
            {
                // Diagnostics are strictly best-effort — never disturb the pipeline.
            }
            Thread.Sleep(FlushInterval);
        }
    }

    private static void WriteOne(string path, object payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var content = payload as string ?? JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(path, content, Utf8NoBom);
        }
        catch
        {
            // Best-effort.
        }
    }
}
