namespace PsGameTranslator.Core.Ocr;

public enum OcrProviderState
{
    Available,
    NotConfigured,
    NotInstalled,
    NotImplemented,
    ServerNotRunning,
    Starting,
    Running,
    Failed,
    Unreachable,
    ModelLoading,
    RunningExternal,
    Stopped,
    Disabled,
    Unknown
}
