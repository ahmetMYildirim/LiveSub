namespace PsGameTranslator.Core.Ocr;

public enum OcrExecutionMode
{
    SingleProvider = 0,
    ParallelBestResult = 1,
    FastFirstThenVerify = 2,
}
