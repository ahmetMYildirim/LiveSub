using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PsGameTranslator.Core.Models;


namespace PsGameTranslator.Capture
{
    public interface IWindowCaptureService
    {
        Task<IReadOnlyList<CapturedWindow>> GetAvailableWindowsAsync(
                CancellationToken cancellationToken = default);

        // TODO: Implement frame capture and finalize the returned image format.
        Task<byte[]> CaptureAsync(
            CapturedWindow window,
            CaptureRegion? region = null,
            CancellationToken cancellationToken = default);
    }
}
