namespace PsGameTranslator.Core.Translation;

public enum TranslationProviderType
{
    None = 0,
    OpusMT = 1,
    MachineTranslation = OpusMT,
    Ollama = 2,
    HybridMachineThenOllama = 3,
    GoogleTranslate = 4,
    DeepL = 5,
    Gemini = 6,
    ChatGPT = 7,
    Groq = 8,
    LMStudio = 9,
    Mistral = 10,
    Mock = 11,
}
