namespace PsGameTranslator.App.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel(
        CaptureViewModel capture,
        RegionViewModel region,
        OcrViewModel ocr,
        MonitoringViewModel monitoring,
        OcrServerViewModel ocrServer,
        TranslationViewModel translation,
        GlossaryViewModel glossary,
        LearningViewModel learning,
        OverlayViewModel overlay,
        SettingsViewModel settings,
        ModelManagerViewModel modelManager)
    {
        Capture     = capture;
        Region      = region;
        Ocr         = ocr;
        Monitoring  = monitoring;
        OcrServer   = ocrServer;
        Translation = translation;
        Glossary    = glossary;
        Learning    = learning;
        Overlay     = overlay;
        Settings    = settings;
        ModelManager = modelManager;
    }

    public CaptureViewModel     Capture     { get; }
    public RegionViewModel      Region      { get; }
    public OcrViewModel         Ocr         { get; }
    public MonitoringViewModel  Monitoring  { get; }
    public OcrServerViewModel   OcrServer   { get; }
    public TranslationViewModel Translation { get; }
    public GlossaryViewModel    Glossary    { get; }
    public LearningViewModel    Learning    { get; }
    public OverlayViewModel     Overlay     { get; }
    public SettingsViewModel    Settings    { get; }
    public ModelManagerViewModel ModelManager { get; }
}
