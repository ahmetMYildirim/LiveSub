using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.App.Views;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newStyle);

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TRANSPARENT = 0x00000020;

    private bool _autoFitHeight = true;
    private double _maxAutoHeight = 320;
    private SubtitleOverlayStyleSettings _style = SubtitleOverlayStyleSettings.CreatePreset(SubtitlePreset.Cinematic);
    private bool _isReplacementMode;

    public bool IsReplacementMode => _isReplacementMode;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetText(string text)
    {
        var safeText = text ?? string.Empty;
        SetTextBlockText(OcrTextBlock, safeText);
        SetTextBlockText(OutlineTopLeftTextBlock, safeText);
        SetTextBlockText(OutlineTopRightTextBlock, safeText);
        SetTextBlockText(OutlineBottomLeftTextBlock, safeText);
        SetTextBlockText(OutlineBottomRightTextBlock, safeText);

        // Normal (non-replacement) OCR text never carries a separate speaker line.
        SpeakerTextBlock.Visibility = Visibility.Collapsed;

        if (_autoFitHeight && !_isReplacementMode)
            AutoFitToText();
    }

    public void ApplyStyle(SubtitleOverlayStyleSettings? style)
    {
        _style = style ?? SubtitleOverlayStyleSettings.CreatePreset(SubtitlePreset.Cinematic);
        if (_isReplacementMode) return;
        ApplyNormalVisuals();
    }

    public void ShowReplacementOverlay(
        string text,
        SubtitleReplacementOverlaySettings settings,
        bool showMaskOnly,
        double targetWidthDip = 0,
        double targetHeightDip = 0,
        string speakerName = "")
    {
        _isReplacementMode = true;
        ApplyReplacementVisuals(settings);

        var visibleText = showMaskOnly ? string.Empty : text;
        ApplySpeaker(showMaskOnly ? string.Empty : speakerName);

        // Part C: the mask keeps its fixed region size; the text adapts instead.
        // Step the font down (never below ReplacementMinFontSize) until the wrapped
        // Turkish fits inside the mask region, so it cannot overflow.
        if (settings.ReplacementAutoFitText &&
            visibleText.Length > 0 &&
            targetWidthDip > 40 &&
            targetHeightDip > 20)
        {
            var fittedSize = ComputeFittedFontSize(visibleText, settings, targetWidthDip, targetHeightDip);
            if (fittedSize < OcrTextBlock.FontSize)
            {
                OcrTextBlock.FontSize = fittedSize;
                OutlineTopLeftTextBlock.FontSize = fittedSize;
                OutlineTopRightTextBlock.FontSize = fittedSize;
                OutlineBottomLeftTextBlock.FontSize = fittedSize;
                OutlineBottomRightTextBlock.FontSize = fittedSize;
            }
        }

        SetTextBlockText(OcrTextBlock, visibleText);
        SetTextBlockText(OutlineTopLeftTextBlock, visibleText);
        SetTextBlockText(OutlineTopRightTextBlock, visibleText);
        SetTextBlockText(OutlineBottomLeftTextBlock, visibleText);
        SetTextBlockText(OutlineBottomRightTextBlock, visibleText);
    }

    private double ComputeFittedFontSize(
        string text,
        SubtitleReplacementOverlaySettings settings,
        double targetWidthDip,
        double targetHeightDip)
    {
        var availableWidth = Math.Max(40, targetWidthDip
            - settings.ReplacementMaskPaddingLeft - settings.ReplacementMaskPaddingRight);
        var availableHeight = Math.Max(20, targetHeightDip
            - settings.ReplacementMaskPaddingTop - settings.ReplacementMaskPaddingBottom);
        var minFontSize = Math.Max(10, settings.ReplacementMinFontSize);
        var typeface = new Typeface(
            SafeFontFamily(settings.ReplacementFontFamily),
            FontStyles.Normal,
            SafeFontWeight(settings.ReplacementFontWeight),
            FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var size = Math.Clamp(settings.ReplacementFontSize, minFontSize, 72);
             size >= minFontSize;
             size -= 1)
        {
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                Brushes.White,
                dpi)
            {
                MaxTextWidth = availableWidth,
                MaxLineCount = Math.Max(1, settings.ReplacementMaxLines),
                Trimming = TextTrimming.None,
            };

            if (formatted.Height <= availableHeight &&
                formatted.MinWidth <= availableWidth)
                return size;
        }

        return minFontSize;
    }

    public void ClearReplacementMode()
    {
        if (!_isReplacementMode) return;
        _isReplacementMode = false;
        ApplyNormalVisuals();
        SetText(string.Empty);
    }

    private void ApplyNormalVisuals()
    {
        TextContainer.Margin = new Thickness(0);
        OuterBorder.HorizontalAlignment = HorizontalAlignment.Center;
        OuterBorder.VerticalAlignment = VerticalAlignment.Bottom;

        ApplyTextStyle(OcrTextBlock);
        ApplyTextStyle(OutlineTopLeftTextBlock);
        ApplyTextStyle(OutlineTopRightTextBlock);
        ApplyTextStyle(OutlineBottomLeftTextBlock);
        ApplyTextStyle(OutlineBottomRightTextBlock);

        OuterBorder.Background = _style.BackgroundEnabled
            ? new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(_style.BackgroundOpacity * 255.0, 0, 255), 0, 0, 0))
            : Brushes.Transparent;
        OuterBorder.CornerRadius = new CornerRadius(Math.Max(0, _style.BackgroundCornerRadius));
        OuterBorder.Padding = new Thickness(
            Math.Max(0, _style.PaddingHorizontal),
            Math.Max(0, _style.PaddingVertical),
            Math.Max(0, _style.PaddingHorizontal),
            Math.Max(0, _style.PaddingVertical));
        OuterBorder.Margin = new Thickness(8, 8, 8, Math.Max(0, _style.BottomMargin));
        OuterBorder.MaxWidth = Math.Max(180, Width * Math.Clamp(_style.MaxWidthPercent, 0.25, 1.0));
        OcrTextBlock.Effect = _style.ShadowEnabled
            ? new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 2,
                Color = Colors.Black,
                Opacity = 0.9,
            }
            : null;

        var outlineVisibility = _style.OutlineEnabled ? Visibility.Visible : Visibility.Collapsed;
        OutlineTopLeftTextBlock.Visibility = outlineVisibility;
        OutlineTopRightTextBlock.Visibility = outlineVisibility;
        OutlineBottomLeftTextBlock.Visibility = outlineVisibility;
        OutlineBottomRightTextBlock.Visibility = outlineVisibility;

        var thickness = Math.Max(0, _style.OutlineThickness);
        OutlineTopLeftTransform.X = -thickness;
        OutlineTopLeftTransform.Y = -thickness;
        OutlineTopRightTransform.X = thickness;
        OutlineTopRightTransform.Y = -thickness;
        OutlineBottomLeftTransform.X = -thickness;
        OutlineBottomLeftTransform.Y = thickness;
        OutlineBottomRightTransform.X = thickness;
        OutlineBottomRightTransform.Y = thickness;

        if (_autoFitHeight && !_isReplacementMode)
            AutoFitToText();
    }

    private void ApplyReplacementVisuals(SubtitleReplacementOverlaySettings settings)
    {
        TextContainer.Margin = new Thickness(0);
        OuterBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        OuterBorder.VerticalAlignment = VerticalAlignment.Stretch;
        OuterBorder.Margin = new Thickness(0);
        OuterBorder.Padding = new Thickness(
            Math.Max(0, settings.ReplacementMaskPaddingLeft),
            Math.Max(0, settings.ReplacementMaskPaddingTop),
            Math.Max(0, settings.ReplacementMaskPaddingRight),
            Math.Max(0, settings.ReplacementMaskPaddingBottom));
        OuterBorder.CornerRadius = new CornerRadius(Math.Max(0, settings.ReplacementMaskCornerRadius));
        OuterBorder.MaxWidth = double.PositiveInfinity;
        OuterBorder.Background = settings.ReplacementMaskEnabled
            ? CreateBrush(settings.ReplacementMaskColor, settings.ReplacementMaskOpacity)
            : Brushes.Transparent;
        OuterBorder.BorderBrush = settings.ShowReplacementRectOutline ? Brushes.LimeGreen : null;
        OuterBorder.BorderThickness = settings.ShowReplacementRectOutline
            ? new Thickness(2)
            : new Thickness(0);

        ApplyReplacementTextStyle(OcrTextBlock, settings);
        ApplyReplacementTextStyle(OutlineTopLeftTextBlock, settings);
        ApplyReplacementTextStyle(OutlineTopRightTextBlock, settings);
        ApplyReplacementTextStyle(OutlineBottomLeftTextBlock, settings);
        ApplyReplacementTextStyle(OutlineBottomRightTextBlock, settings);
        OutlineTopLeftTextBlock.Foreground = Brushes.Black;
        OutlineTopRightTextBlock.Foreground = Brushes.Black;
        OutlineBottomLeftTextBlock.Foreground = Brushes.Black;
        OutlineBottomRightTextBlock.Foreground = Brushes.Black;

        var outlineVisibility = settings.ReplacementOutlineEnabled ? Visibility.Visible : Visibility.Collapsed;
        OutlineTopLeftTextBlock.Visibility = outlineVisibility;
        OutlineTopRightTextBlock.Visibility = outlineVisibility;
        OutlineBottomLeftTextBlock.Visibility = outlineVisibility;
        OutlineBottomRightTextBlock.Visibility = outlineVisibility;

        OcrTextBlock.Effect = settings.ReplacementTextShadowEnabled
            ? new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 2,
                Color = Colors.Black,
                Opacity = 0.9,
            }
            : null;

        const double replacementOutlineThickness = 1;
        OutlineTopLeftTransform.X = -replacementOutlineThickness;
        OutlineTopLeftTransform.Y = -replacementOutlineThickness;
        OutlineTopRightTransform.X = replacementOutlineThickness;
        OutlineTopRightTransform.Y = -replacementOutlineThickness;
        OutlineBottomLeftTransform.X = -replacementOutlineThickness;
        OutlineBottomLeftTransform.Y = replacementOutlineThickness;
        OutlineBottomRightTransform.X = replacementOutlineThickness;
        OutlineBottomRightTransform.Y = replacementOutlineThickness;
    }

    public void SetAutoFit(bool enabled, double maxHeight)
    {
        _autoFitHeight = enabled;
        _maxAutoHeight = Math.Max(50, maxHeight);
    }

    public void SetClickThrough(bool enable)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (enable)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
        else
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
    }

    public void EnterConfigMode()
    {
        ConfigHeader.Visibility = _isReplacementMode ? Visibility.Collapsed : Visibility.Visible;
        ResizeThumb.Visibility = _isReplacementMode ? Visibility.Collapsed : Visibility.Visible;
        OuterBorder.BorderBrush = Brushes.DodgerBlue;
        OuterBorder.BorderThickness = new Thickness(2);
    }

    public void ExitConfigMode()
    {
        ConfigHeader.Visibility = Visibility.Collapsed;
        ResizeThumb.Visibility = Visibility.Collapsed;
        OuterBorder.BorderBrush = null;
        OuterBorder.BorderThickness = new Thickness(0);
    }

    private void AutoFitToText()
    {
        const double chrome = 16;
        var availableWidth = Math.Max(150, OuterBorder.MaxWidth);
        OcrTextBlock.Measure(new Size(availableWidth, double.PositiveInfinity));
        var desiredTextHeight = OcrTextBlock.DesiredSize.Height;
        var desiredHeight = desiredTextHeight + OuterBorder.Padding.Top + OuterBorder.Padding.Bottom +
            _style.BottomMargin + chrome;
        Height = Math.Min(_maxAutoHeight, Math.Max(80, desiredHeight));
    }

    private void ApplyTextStyle(TextBlock textBlock)
    {
        textBlock.FontFamily = SafeFontFamily(_style.FontFamily);
        textBlock.FontSize = Math.Clamp(_style.FontSize, 12, 72);
        textBlock.FontWeight = SafeFontWeight(_style.FontWeight);
        textBlock.TextAlignment = SafeTextAlignment(_style.TextAlignment);
        textBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
        textBlock.VerticalAlignment = VerticalAlignment.Top;
        textBlock.TextWrapping = TextWrapping.Wrap;
        textBlock.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        textBlock.LineHeight = double.NaN;
        textBlock.Foreground = CreateBrush(_style.TextColor ?? "#FFFFFF", 1.0);
    }

    private static void ApplyReplacementTextStyle(
        TextBlock textBlock,
        SubtitleReplacementOverlaySettings settings)
    {
        textBlock.FontFamily = SafeFontFamily(settings.ReplacementFontFamily);
        textBlock.FontSize = Math.Clamp(settings.ReplacementFontSize, 12, 72);
        textBlock.FontWeight = SafeFontWeight(settings.ReplacementFontWeight);
        textBlock.TextAlignment = SafeTextAlignment(settings.ReplacementTextAlignment);
        textBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
        textBlock.VerticalAlignment = VerticalAlignment.Center;
        textBlock.TextWrapping = TextWrapping.Wrap;
        textBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        textBlock.LineHeight = Math.Max(12, settings.ReplacementFontSize + settings.ReplacementLineSpacing);
        textBlock.Foreground = CreateBrush(settings.ReplacementTextColor, 1.0);
    }

    private static FontFamily SafeFontFamily(string? fontFamily)
    {
        try
        {
            return string.IsNullOrWhiteSpace(fontFamily)
                ? new FontFamily("Segoe UI")
                : new FontFamily(fontFamily);
        }
        catch
        {
            return new FontFamily("Segoe UI");
        }
    }

    private static FontWeight SafeFontWeight(string? fontWeight)
    {
        var converter = new FontWeightConverter();
        try
        {
            var value = converter.ConvertFromString(fontWeight ?? "SemiBold");
            return value is FontWeight weight ? weight : FontWeights.SemiBold;
        }
        catch
        {
            return FontWeights.SemiBold;
        }
    }

    private static TextAlignment SafeTextAlignment(string? textAlignment)
    {
        return textAlignment?.Trim().ToLowerInvariant() switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            "justify" => TextAlignment.Justify,
            _ => TextAlignment.Center,
        };
    }

    private static void SetTextBlockText(TextBlock textBlock, string text)
    {
        textBlock.Text = string.Empty;
        textBlock.TextTrimming = TextTrimming.None;
        textBlock.Text = text;
    }

    // Renders the speaker name on its own right-aligned line above the subtitle,
    // at ~80% of the subtitle font so it reads as a label. Hidden when there is
    // no speaker (or the overlay is showing the mask only).
    private void ApplySpeaker(string? speakerName)
    {
        var name = (speakerName ?? string.Empty).Trim().TrimEnd(':').Trim();
        if (name.Length == 0)
        {
            SpeakerTextBlock.Visibility = Visibility.Collapsed;
            SpeakerTextBlock.Text = string.Empty;
            return;
        }

        SpeakerTextBlock.FontSize = Math.Max(10, OcrTextBlock.FontSize * 0.8);
        SpeakerTextBlock.FontFamily = OcrTextBlock.FontFamily;
        SpeakerTextBlock.Text = name;
        SpeakerTextBlock.Visibility = Visibility.Visible;
    }

    private static Brush CreateBrush(string colorText, double opacity)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorText);
            color.A = (byte)Math.Clamp(opacity * 255.0, 0, 255);
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(opacity * 255.0, 0, 255), 0, 0, 0));
        }
    }

    private void ConfigHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(240, ActualWidth + e.HorizontalChange);
        Height = Math.Max(80, ActualHeight + e.VerticalChange);
        OuterBorder.MaxWidth = Math.Max(180, Width * Math.Clamp(_style.MaxWidthPercent, 0.25, 1.0));
    }
}
