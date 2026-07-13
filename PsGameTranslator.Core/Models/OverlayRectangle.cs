namespace PsGameTranslator.Core.Models;

public sealed class OverlayRectangle
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double Right => X + Width;
    public double Bottom => Y + Height;

    public OverlayRectangle Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
    };

    public override string ToString() => $"({X:F0}, {Y:F0}) {Width:F0}x{Height:F0}";
}
