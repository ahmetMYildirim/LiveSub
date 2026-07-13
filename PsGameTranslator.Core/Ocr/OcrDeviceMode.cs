namespace PsGameTranslator.Core.Ocr;

/// <summary>Compute device PaddleOCR should run on.</summary>
public enum OcrDeviceMode
{
    /// <summary>Use the GPU when a CUDA-capable install/GPU is detected, otherwise CPU.</summary>
    Auto = 0,
    Cpu = 1,
    Gpu = 2,
}
