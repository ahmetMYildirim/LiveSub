using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Ocr;

namespace PsGameTranslator.App.Services;

public sealed record TrainingRunOptions(
    IReadOnlyList<string> DatasetFiles,
    IReadOnlyList<string> TestFiles,
    string OutputDir,
    int Epochs,
    int BatchSize,
    double LearningRate,
    int MaxLength,
    double ValSplit,
    int LoraR,
    int LoraAlpha,
    bool EstimateOnly);

/// <summary>
/// Launches tools/translation/train_opus_mt.py and turns its stdout into two
/// event streams: every raw line (for the live log console) and parsed
/// PROGRESS_JSON lines (for epoch/loss/VRAM/metric widgets). One instance is
/// shared by the Training page — RunTrainingAsync is not reentrant while a
/// run is already in progress.
/// </summary>
public sealed class TrainingService
{
    private readonly PythonEnvironmentService _python;
    private readonly ILogger<TrainingService> _logger;
    private CancellationTokenSource? _cts;

    public event Action<string>? LogLineReceived;
    public event Action<JsonElement>? ProgressReceived;

    public bool IsRunning { get; private set; }

    public TrainingService(PythonEnvironmentService python, ILogger<TrainingService> logger)
    {
        _python = python;
        _logger = logger;
    }

    public async Task<int> RunAsync(TrainingRunOptions options, CancellationToken cancellationToken)
    {
        if (IsRunning)
            throw new InvalidOperationException("A training run is already in progress.");

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "translation", "train_opus_mt.py");
        if (!File.Exists(scriptPath))
        {
            LogLineReceived?.Invoke($"[train] Script not found at {scriptPath}");
            return -1;
        }

        var args = BuildArgs(options);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = runCts;
        IsRunning = true;
        try
        {
            var progress = new Progress<string>(HandleLine);
            return await _python.RunScriptAsync(scriptPath, args, progress, runCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "training_run_failed");
            LogLineReceived?.Invoke($"[train] Unexpected error: {exception.Message}");
            return -1;
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_cts, runCts))
                _cts = null;

            runCts.Dispose();
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Stop can be clicked while RunAsync is completing and disposing.
        }
    }

    private static List<string> BuildArgs(TrainingRunOptions options)
    {
        var args = new List<string>
        {
            "--epochs", options.Epochs.ToString(),
            "--batch-size", options.BatchSize.ToString(),
            "--learning-rate", options.LearningRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-length", options.MaxLength.ToString(),
            "--val-split", options.ValSplit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--lora-r", options.LoraR.ToString(),
            "--lora-alpha", options.LoraAlpha.ToString(),
        };

        if (options.EstimateOnly)
        {
            args.Add("--estimate-only");
            return args;
        }

        args.Add("--output-dir");
        args.Add(options.OutputDir);
        if (options.DatasetFiles.Count > 0)
        {
            args.Add("--dataset-files");
            args.AddRange(options.DatasetFiles);
        }
        if (options.TestFiles.Count > 0)
        {
            args.Add("--test-files");
            args.AddRange(options.TestFiles);
        }
        return args;
    }

    private void HandleLine(string line)
    {
        LogLineReceived?.Invoke(line);

        const string marker = "PROGRESS_JSON:";
        if (!line.StartsWith(marker, StringComparison.Ordinal))
            return;

        try
        {
            using var doc = JsonDocument.Parse(line[marker.Length..]);
            ProgressReceived?.Invoke(doc.RootElement.Clone());
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "training_progress_line_unparseable - line={Line}", line);
        }
    }
}
