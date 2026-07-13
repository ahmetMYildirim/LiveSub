namespace PsGameTranslator.Core.Translation;

public enum MachineTranslationServerState
{
    NotChecked,
    Starting,
    Running,
    // Health check succeeded but the process was not started by this app instance
    // (e.g. the user started it manually). Never killed by StopServerAsync.
    RunningExternal,
    Unreachable,
    Failed,
    Stopped,
}
