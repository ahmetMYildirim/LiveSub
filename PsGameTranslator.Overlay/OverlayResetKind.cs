namespace PsGameTranslator.Overlay;

/// <summary>Which default-position rules to use when resetting/recovering the overlay (Part D).</summary>
public enum OverlayResetKind
{
    /// <summary>Generic "Reset Overlay Position" default: bottom-center, 70% width, ~160px height, 100px bottom margin.</summary>
    Default,

    /// <summary>The current subtitle overlay window: bottom-center, 105px bottom margin.</summary>
    NativeSubtitleOverlay,

    /// <summary>Reserved for a future translation-panel overlay: bottom-left, 40px left margin, 90px bottom margin.</summary>
    TranslationPanelOverlay,
}
