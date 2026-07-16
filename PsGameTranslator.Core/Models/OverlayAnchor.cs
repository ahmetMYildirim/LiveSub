namespace PsGameTranslator.Core.Models;

/// <summary>
/// One of the nine preset placements on the target monitor's working area.
/// Subtitles almost always belong at BottomCenter, but games put their dialogue
/// in different corners, so all nine are offered as one-click anchors.
/// </summary>
public enum OverlayAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}
