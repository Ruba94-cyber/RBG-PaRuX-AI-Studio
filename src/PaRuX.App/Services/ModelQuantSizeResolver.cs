using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Hugging Face's search endpoint never returns file sizes, so every quantisation choice it yields
    /// has <c>SizeBytes = 0</c>; the single-repository details call (with blobs) does. The automatic
    /// first-run setup used the unsized search choices and discarded all of them, so it could never pick
    /// a model on a real machine. This fills the sizes from the details file list.
    /// </summary>
    public static class ModelQuantSizeResolver
    {
        public static IReadOnlyList<ModelQuantChoice> Fill(
            IReadOnlyList<ModelQuantChoice> choices, IEnumerable<ModelFileDetails>? files)
        {
            if (choices.Count == 0 || files == null) return choices;
            var sizeByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelFileDetails file in files)
            {
                if (string.IsNullOrWhiteSpace(file.FileName) || file.SizeBytes is not > 0) continue;
                sizeByName[Path.GetFileName(file.FileName.Replace((char)92, '/'))] = file.SizeBytes.Value;
            }

            foreach (ModelQuantChoice choice in choices)
            {
                if (choice.SizeBytes > 0 || choice.FilePaths.Count == 0) continue;
                long total = 0;
                bool allKnown = true;
                foreach (string path in choice.FilePaths)
                {
                    if (sizeByName.TryGetValue(Path.GetFileName(path.Replace((char)92, '/')), out long size)) total += size;
                    else { allKnown = false; break; }
                }
                if (allKnown) choice.SizeBytes = total;
            }
            return choices;
        }
    }
}
