using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only detector for GGUF models that already live in the external LocalAI stack. PaRuX
    /// deliberately never installs, deletes or moves those externally-owned files in place.
    ///
    /// This is no longer the whole Model Manager: Core's ModelDownloadService owns PaRuX-managed
    /// search/download/import/delete/alias metadata, model-library endpoints explicitly load and
    /// unload managed files, model-role routing selects defaults/roles, and benchmark/update
    /// services own measured recommendations and revision lifecycle. Keeping this detector read-only
    /// preserves the external-stack trust boundary instead of duplicating managed-library behavior.
    /// </summary>
    public static class ModelManagerService
    {
        /// <summary>The external Local AI models plus the PaRuX-managed library (the Model Hub's source), each once.</summary>
        public static List<GgufModelInfo> ListInstalledModels(IEnumerable<PaRuX.Contracts.ManagedModelInfo> managed)
        {
            var managedInfos = new List<GgufModelInfo>();
            foreach (PaRuX.Contracts.ManagedModelInfo model in managed)
            {
                try
                {
                    GgufModelInfo info = File.Exists(model.FilePath) ? GgufMetadataReader.Read(model.FilePath) : new GgufModelInfo();
                    info.FileName = model.FileName;
                    info.FilePath = model.FilePath;
                    info.SizeBytes = model.SizeBytes; // a split set is its whole size
                    if (string.IsNullOrWhiteSpace(info.Name)) info.Name = string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias;
                    managedInfos.Add(info);
                }
                catch (Exception ex) { Log.Write("ModelManagerService: failed to read managed " + model.FileName + ": " + ex.Message); }
            }
            return InstalledModelMerge.Merge(managedInfos, ListInstalledModels());
        }

        public static List<GgufModelInfo> ListInstalledModels()
        {
            string? modelsDir = StorageQuotaService.TryLocateExternalModelsDir();
            if (modelsDir == null) return new List<GgufModelInfo>();

            var results = new List<GgufModelInfo>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
            try
            {
                foreach (string file in Directory.EnumerateFiles(modelsDir, "*.gguf", options))
                {
                    try
                    {
                        GgufModelInfo info = GgufMetadataReader.Read(file);
                        if (info.ParseError != null)
                            Log.Write("GgufMetadataReader failed for " + file + ": " + info.ParseError);
                        results.Add(info);
                    }
                    catch (Exception ex) { Log.Write("ModelManagerService: failed to read " + file + ": " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.Write("ModelManagerService: failed to enumerate " + modelsDir + ": " + ex.Message);
            }

            return results.OrderByDescending(m => m.SizeBytes).ToList();
        }
    }
}
