namespace PsGameTranslator.Core.Models;

public sealed class SubtitleReplacementOverlaySettings
{
    public bool ReplacementMaskEnabled { get; set; } = true;
    public double ReplacementMaskOpacity { get; set; } = 0.90;
    public double ReplacementMaskCornerRadius { get; set; } = 8;
    public string ReplacementMaskColor { get; set; } = "#FF000000";

    // ── Manual replacement region (Part A) ───────────────────────────────────────
    // Dynamic OCR bounding boxes are unreliable; the user-selected fixed region is
    // the primary positioning method. Coordinates are window-relative pixels so the
    // region follows the game window. Width/Height = 0 means "not set yet".
    public bool UseManualReplacementRegion { get; set; } = true;
    public double ManualReplacementRegionX { get; set; }
    public double ManualReplacementRegionY { get; set; }
    public double ManualReplacementRegionWidth { get; set; }
    public double ManualReplacementRegionHeight { get; set; }

    /// <summary>The mask always fills the full manual region — it never shrinks to
    /// the Turkish text size (Part B/C).</summary>
    public bool ReplacementMaskUseFixedRegionSize { get; set; } = true;

    /// <summary>Shrink the font (down to <see cref="ReplacementMinFontSize"/>) until
    /// the wrapped Turkish text fits inside the mask region (Part C).</summary>
    public bool ReplacementAutoFitText { get; set; } = true;
    public double ReplacementMinFontSize { get; set; } = 20;

    public double ReplacementMaskPaddingLeft { get; set; } = 24;
    public double ReplacementMaskPaddingTop { get; set; } = 12;
    public double ReplacementMaskPaddingRight { get; set; } = 24;
    public double ReplacementMaskPaddingBottom { get; set; } = 12;
    public double ReplacementMinWidth { get; set; } = 420;
    public double ReplacementMaxWidthPercent { get; set; } = 0.82;
    public double ReplacementMinHeight { get; set; } = 56;
    public bool ShowReplacementRectOutline { get; set; }
    public bool RejectHudControlText { get; set; } = true;
    public bool UseSubtitleCandidateScoring { get; set; } = true;

    public string ReplacementFontFamily { get; set; } = "Segoe UI";
    public double ReplacementFontSize { get; set; } = 26;
    public string ReplacementFontWeight { get; set; } = "SemiBold";
    public string ReplacementTextColor { get; set; } = "#FFFFFFFF";
    public string ReplacementTextAlignment { get; set; } = "Center";
    public int ReplacementMaxLines { get; set; } = 3;
    public double ReplacementLineSpacing { get; set; } = 2;
    public bool ReplacementTextShadowEnabled { get; set; } = true;
    public bool ReplacementOutlineEnabled { get; set; } = true;
    public string ReplacementRectSource { get; set; } = "ManualReplacementRegion";
    public string ReplacementPendingMode { get; set; } = "MaskOnly";
}
