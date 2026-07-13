using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

public interface IOverlayService
{
    bool IsOpen { get; }
    OverlaySettings CurrentSettings { get; }
    SubtitleReplacementOverlaySnapshot? LastReplacementSnapshot { get; }
    void Open(OverlaySettings settings);
    void Close();
    void UpdateText(string text);
    void UpdateReplacementOverlay(SubtitleReplacementOverlayUpdate update);
    void ApplySettings(OverlaySettings settings);
    void EnterConfigMode();
    (double X, double Y, double Width, double Height) ExitConfigMode();
}
