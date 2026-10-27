using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Settings lists the models PaRuX can see. The authoritative source for PaRuX-managed models is the
    /// managed library the Model Hub shows (served by PaRuX Core from PaRuXDataRoot\models), which is a
    /// different folder from the external Local AI stack's models folder. The two lists are merged here:
    /// managed entries first (their size is the whole split set), then external-only entries, never twice.
    /// </summary>
    public static class InstalledModelMerge
    {
        public static List<GgufModelInfo> Merge(IEnumerable<GgufModelInfo> managed, IEnumerable<GgufModelInfo> external)
        {
            var result = new List<GgufModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GgufModelInfo model in managed.Concat(external))
            {
                string key;
                try { key = Path.GetFullPath(model.FilePath); } catch (ArgumentException) { key = model.FilePath; }
                if (seen.Add(key)) result.Add(model);
            }
            return result;
        }
    }
}
