using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PsGameTranslator.Core.Models;


namespace PsGameTranslator.Ocr
{
    public interface IOcrService
    {
        // TODO: Implement using the selected OCR provider.
        Task<OcrResult> RecognizeAsync(
            ReadOnlyMemory<byte> imageData,
            CaptureRegion? region = null,
            CancellationToken cancellationToken = default);
    }
}
