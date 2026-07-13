using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Add game profiles load and saving with this class

namespace PsGameTranslator.Infrastructure.Configuration
{
    public static class JsonConfigurationHelper
    {
        public static IConfigurationRoot Build(string basePath)
        {
            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(
                    path: "appsettings.json",
                    optional: false,
                    reloadOnChange: true)
                // User-entered overrides (API keys, engine/model picks made from
                // the UI) — layered on top of appsettings.json so they survive
                // a restart without touching the checked-in defaults file.
                .AddJsonFile(
                    path: Path.Combine("config", "user_settings.json"),
                    optional: true,
                    reloadOnChange: true)
                .Build();
        }
    }
}
