namespace PsGameTranslator.Core.Models;

public sealed class OcrLine
{
    public string Text { get; init; } = string.Empty;
    public double Confidence { get; init; }

    /// <summary>Left edge of the line's bounding box in cropped-image pixels; -1 when unknown.</summary>
    public int X { get; init; } = -1;

    /// <summary>Top edge of the line's bounding box in cropped-image pixels; -1 when unknown.</summary>
    public int Y { get; init; } = -1;

    /// <summary>Right edge of the line's bounding box in cropped-image pixels; -1 when unknown.</summary>
    public int Right { get; init; } = -1;

    /// <summary>Bottom edge of the line's bounding box in cropped-image pixels; -1 when unknown.</summary>
    public int Bottom { get; init; } = -1;

    public bool HasBoundingBox => X >= 0 && Y >= 0 && Right > X && Bottom > Y;

    public double Width  => HasBoundingBox ? Right - X : -1;
    public double Height => HasBoundingBox ? Bottom - Y : -1;
    public double CenterX => HasBoundingBox ? X + (Right - X) / 2.0 : -1;
    public double CenterY => HasBoundingBox ? Y + (Bottom - Y) / 2.0 : -1;

    /// <summary>Bounding box as "x,y,width,height" for diagnostics; empty when unknown.</summary>
    public string BoundingBox => HasBoundingBox ? $"{X},{Y},{Width:F0},{Height:F0}" : string.Empty;

    // Relative values against the final OCR crop image (0..1); -1 when not computed.
    // Populated by SubtitleLineClassifier: RelativeX = X / cropWidth, etc.
    public double RelativeX { get; set; } = -1;
    public double RelativeY { get; set; } = -1;
    public double RelativeWidth { get; set; } = -1;
    public double RelativeHeight { get; set; } = -1;
    public double RelativeCenterY { get; set; } = -1;

    public void ComputeRelative(int cropWidth, int cropHeight)
    {
        if (!HasBoundingBox || cropWidth <= 0 || cropHeight <= 0) return;
        RelativeX = X / (double)cropWidth;
        RelativeY = Y / (double)cropHeight;
        RelativeWidth = Width / cropWidth;
        RelativeHeight = Height / cropHeight;
        RelativeCenterY = CenterY / cropHeight;
    }
}
