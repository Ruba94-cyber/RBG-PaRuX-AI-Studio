using System;
using System.IO;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Every filesystem location PaRuX owns or reads. PaRuX writes only under
    /// %LOCALAPPDATA%\PaRuX; the LocalAI paths are read-only to us.
    /// </summary>
    public static class AppPaths
    {
        public static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // ---- PaRuX-owned ----
        public static string PaRuXRoot => PaRuX.Contracts.PaRuXDataRoot.Path;
        public static string HistoryDir => Path.Combine(PaRuXRoot, "history");
        public static string AttachmentsDir => Path.Combine(PaRuXRoot, "attachments");
        public static string GeneratedImagesDir => Path.Combine(PaRuXRoot, "generated", "images");
        public static string GeneratedVideosDir => Path.Combine(PaRuXRoot, "generated", "videos");
        public static string GeneratedAudioDir => Path.Combine(PaRuXRoot, "generated", "audio");
        public static string GeneratedThreedDir => Path.Combine(PaRuXRoot, "generated", "3d");
        public static string GeneratedPresentationsDir => Path.Combine(PaRuXRoot, "generated", "presentations");
        public static string BackupsDir => Path.Combine(PaRuXRoot, "backups");
        /// <summary>
        /// Durable local state for coding-agent sessions. This intentionally lives outside every
        /// selected repository/worktree so checkpoint files can never pollute a user's Git diff.
        /// </summary>
        public static string CodingAgentStateDir => Path.Combine(PaRuXRoot, "coding-agent-state");
        public static string SettingsFile => Path.Combine(PaRuXRoot, "settings.json");
        public static string DatabaseFile => Path.Combine(PaRuXRoot, "parux.db");
        public static string LogFile => Path.Combine(PaRuXRoot, "parux.log");

        // ---- External LocalAI stack, read-only except through its controller ----
        public static string LocalAiStateDir => PaRuX.Contracts.PaRuXLocalAiStateRoot.Path;
        public static string ModeFile => Path.Combine(LocalAiStateDir, "mode.txt");
        public static string TextProfileFile => Path.Combine(LocalAiStateDir, "text-profile.txt");
        public static string ActiveProfileFile => Path.Combine(LocalAiStateDir, "active-profile.txt");
        public static string LlamaPidFile => Path.Combine(LocalAiStateDir, "llama-server.pid");
        public static string GameMarkerFile => Path.Combine(LocalAiStateDir, "game-active.json");

        public static string LocalAiRoot
        {
            get
            {
                try
                {
                    string configPath = Path.Combine(PaRuXRoot, "localai-paths.json");
                    if (File.Exists(configPath))
                    {
                        var saved = JsonSerializer.Deserialize<LocalAiRootConfig>(File.ReadAllText(configPath));
                        if (saved != null && !string.IsNullOrWhiteSpace(saved.Root) && Directory.Exists(saved.Root))
                            return saved.Root;
                    }
                }
                catch { /* fall through to auto-detect */ }

                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string legacyCandidate = Path.Combine(documents, "LocalAI");
                if (Directory.Exists(Path.Combine(legacyCandidate, "runtime")) || Directory.Exists(Path.Combine(legacyCandidate, "models")))
                    return legacyCandidate;

                return Path.Combine(PaRuXRoot, "LocalAI");
            }
        }

        public static string LocalAiModelsDir => Path.Combine(LocalAiRoot, "models");

        private sealed class LocalAiRootConfig
        {
            public string Root { get; set; } = string.Empty;
        }

        public static void EnsureCreated()
        {
            Directory.CreateDirectory(PaRuXRoot);
            Directory.CreateDirectory(HistoryDir);
            Directory.CreateDirectory(AttachmentsDir);
            Directory.CreateDirectory(GeneratedImagesDir);
            Directory.CreateDirectory(GeneratedVideosDir);
            Directory.CreateDirectory(GeneratedAudioDir);
            Directory.CreateDirectory(GeneratedThreedDir);
            Directory.CreateDirectory(GeneratedPresentationsDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(CodingAgentStateDir);
        }
    }
}
