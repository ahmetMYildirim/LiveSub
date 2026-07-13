using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Translation
{
    // Backward-compatible service name. New providers should depend on
    // ITranslationProvider directly.
    public interface ITranslationService : ITranslationProvider
    {
    }
}
