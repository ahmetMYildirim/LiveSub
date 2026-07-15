using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed record OnnxGameIdentificationResult(
    bool Success,
    string? GameTitle,
    string? Label,
    float Confidence,
    float Margin);

/// <summary>
/// EfficientNet-B0 game classifier (trained on ~500 IGDB titles, exported to
/// ONNX by tools/game-recognition/train_game_classifier.py). Runs fully local
/// and offline. It is optional: if the model assets are missing it simply
/// returns no result, and the caller falls back to the vision LLM.
///
/// Self-contained on purpose — it needs only the .onnx and labels.json that
/// ship next to it. The display title is derived from the label slug itself
/// (labels look like "201156_star-wars-jedi-survivor"), so there is no separate
/// games.jsonl asset to keep in sync (that file used to be required and was
/// missing at runtime, which silently disabled the whole identifier).
/// </summary>
public sealed class OnnxGameIdentifier : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "models", "game-recognition");
    private InferenceSession? _session;
    private string[]? _labels;

    /// <summary>True once the model + labels are present and loaded.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _session is not null && _labels is not null;
        }
    }

    public Task<OnnxGameIdentificationResult> IdentifyAsync(byte[] frame) =>
        IdentifyAsync([frame]);

    /// <summary>
    /// Classifies one or more captured frames. When multiple frames are given
    /// their logits are averaged before the softmax, which the offline
    /// benchmarks showed is meaningfully steadier than a single frame; a single
    /// frame is fully supported too.
    /// </summary>
    public Task<OnnxGameIdentificationResult> IdentifyAsync(IReadOnlyList<byte[]> frames)
    {
        try
        {
            EnsureLoaded();
            if (_session is null || _labels is null || frames.Count == 0)
                return Task.FromResult(new OnnxGameIdentificationResult(false, null, null, 0, 0));

            var sums = new float[_labels.Length];
            var used = 0;
            foreach (var frame in frames.Take(3))
            {
                var input = NamedOnnxValue.CreateFromTensor("image", ToTensor(frame));
                using var values = _session.Run([input]);
                var logits = values.First().AsEnumerable<float>().ToArray();
                for (var i = 0; i < sums.Length; i++) sums[i] += logits[i];
                used++;
            }
            if (used == 0)
                return Task.FromResult(new OnnxGameIdentificationResult(false, null, null, 0, 0));

            for (var i = 0; i < sums.Length; i++) sums[i] /= used;

            var top = Enumerable.Range(0, sums.Length).OrderByDescending(i => sums[i]).Take(2).ToArray();
            var max = sums.Max();
            var total = sums.Sum(x => MathF.Exp(x - max));
            var first = MathF.Exp(sums[top[0]] - max) / total;
            var second = sums.Length > 1 ? MathF.Exp(sums[top[1]] - max) / total : 0f;
            var label = _labels[top[0]];
            return Task.FromResult(
                new OnnxGameIdentificationResult(true, TitleFromLabel(label), label, first, first - second));
        }
        catch
        {
            return Task.FromResult(new OnnxGameIdentificationResult(false, null, null, 0, 0));
        }
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;
        var model = Path.Combine(_root, "game_recognition_efficientnet_b0.onnx");
        var labels = Path.Combine(_root, "labels.json");
        if (!File.Exists(model) || !File.Exists(labels)) return;
        _labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(labels)) ?? [];
        _session = new InferenceSession(model);
    }

    /// <summary>
    /// "201156_star-wars-jedi-survivor" -> "Star Wars Jedi Survivor".
    /// The label is the IGDB slug prefixed with the numeric id; strip the id
    /// and title-case the slug. Not a perfect reconstruction of punctuation
    /// (colons, apostrophes) but good enough for display and for the coordinator's
    /// keyword-based glossary match.
    /// </summary>
    internal static string TitleFromLabel(string label)
    {
        var underscore = label.IndexOf('_');
        var slug = underscore >= 0 ? label[(underscore + 1)..] : label;
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', words);
    }

    private static DenseTensor<float> ToTensor(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var source = new Bitmap(stream);

        // Match the training/eval transform: Resize(256) on the shorter side
        // (aspect-ratio preserving) then CenterCrop(224). The previous code
        // stretched straight to 224x224, distorting the frame and hurting
        // accuracy because it no longer matched what the model was trained on.
        const int resize = 256;
        const int crop = 224;
        var scale = resize / (float)Math.Min(source.Width, source.Height);
        var newWidth = Math.Max(crop, (int)Math.Round(source.Width * scale));
        var newHeight = Math.Max(crop, (int)Math.Round(source.Height * scale));

        using var resized = new Bitmap(newWidth, newHeight);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, newWidth, newHeight);
        }

        var offsetX = (newWidth - crop) / 2;
        var offsetY = (newHeight - crop) / 2;
        var tensor = new DenseTensor<float>([1, 3, crop, crop]);
        for (var y = 0; y < crop; y++)
        {
            for (var x = 0; x < crop; x++)
            {
                var c = resized.GetPixel(offsetX + x, offsetY + y);
                tensor[0, 0, y, x] = (c.R / 255f - .485f) / .229f;
                tensor[0, 1, y, x] = (c.G / 255f - .456f) / .224f;
                tensor[0, 2, y, x] = (c.B / 255f - .406f) / .225f;
            }
        }
        return tensor;
    }

    public void Dispose() => _session?.Dispose();
}
