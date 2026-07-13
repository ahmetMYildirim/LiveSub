using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PsGameTranslator.Infrastructure.Configuration
{
    public sealed class AppSettings
    {
        public const string SectionName = "AppSettings";
        public string DefaultSourceLanguage { get; set; } = "en";
        public string DefaultTargetLanguage { get; set; } = "tr";
        public string ProfilesDirectory { get; set; } = "Profiles";

        /// <summary>
        /// Full path to the Python executable used for OCR.
        /// Leave empty to auto-detect from PATH.
        /// Example: C:\Users\ahmet\AppData\Local\Programs\Python\Python311\python.exe
        /// </summary>
        public string PythonExePath { get; set; } = string.Empty;

        public bool EnableOcrCache { get; set; } = true;

        public int OcrIntervalMilliseconds { get; set; } = 1000;

        public double MinimumConfidenceThreshold { get; set; } = 0.5;
    }
}
