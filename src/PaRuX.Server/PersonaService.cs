using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Named system-prompt presets that shape tone/personality, independent of route/skill logic.
/// "default" (empty SystemPrompt, built-in, can't be deleted) means "no persona flavor" - exactly
/// today's behavior before this feature existed, so an unconfigured install is unaffected.
/// Custom personas + the active selection persist to a single JSON file, same atomic-write
/// pattern as InternetSettingsService; built-ins are a static in-memory list, never written to disk.
/// </summary>
public sealed class PersonaService
{
    private static readonly IReadOnlyList<Persona> BuiltIns = new List<Persona>
    {
        new() { Id = "default", Name = "Default", SystemPrompt = "", IsBuiltIn = true },
        new()
        {
            Id = "concise", Name = "Concise", IsBuiltIn = true,
            SystemPrompt = "Be extremely brief. Answer in as few words as possible, prefer single sentences or short lists over paragraphs, and skip preamble/caveats unless the user actually asks a follow-up."
        },
        new()
        {
            Id = "friendly", Name = "Friendly & Casual", IsBuiltIn = true,
            SystemPrompt = "Speak like a warm, casual friend - contractions, plain language, the occasional bit of enthusiasm. Still accurate and useful, just not stiff or formal."
        },
        new()
        {
            Id = "professional", Name = "Professional", IsBuiltIn = true,
            SystemPrompt = "Write in a precise, formal, business-appropriate register. No slang, no emoji, no casual asides. Structure longer answers with clear headings or numbered steps."
        },
        new()
        {
            Id = "socratic", Name = "Socratic Tutor", IsBuiltIn = true,
            SystemPrompt = "Teach by asking guiding questions rather than giving the answer outright. When the user is working through a problem, prompt them toward the next step instead of stating it, unless they explicitly ask for the direct answer."
        },
        new()
        {
            Id = "creative", Name = "Creative Writer", IsBuiltIn = true,
            SystemPrompt = "Favor vivid, evocative language and a strong narrative voice, even in ordinary explanations. Use metaphor and imagery where it genuinely helps rather than just decorates."
        },
        new()
        {
            Id = "blunt", Name = "Blunt / No-Fluff", IsBuiltIn = true,
            SystemPrompt = "Skip hedging, disclaimers, and diplomatic softening. State your actual assessment plainly, including when something is a bad idea or won't work, before offering alternatives."
        }
    };

    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private List<Persona>? _customCache;
    private string? _activeCache;

    public PersonaService() : this(ServerState.StateDirectory) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point this service at an isolated temp directory instead of the real
    /// ServerState.StateDirectory - production code always goes through the parameterless
    /// constructor above (the DI singleton registered in Program.cs).</summary>
    public PersonaService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string StorePath => Path.Combine(_stateDirectory, "personas.json");

    public PersonaListResponse GetAll()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var all = new List<Persona>(BuiltIns);
            all.AddRange(_customCache!);
            return new PersonaListResponse { Personas = all, ActivePersonaId = _activeCache! };
        }
    }

    public Persona GetActive()
    {
        PersonaListResponse all = GetAll();
        return all.Personas.FirstOrDefault(p => p.Id == all.ActivePersonaId) ?? BuiltIns[0];
    }

    public Persona Create(string name, string systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A persona needs a name.");
        if (string.IsNullOrWhiteSpace(systemPrompt)) throw new InvalidDataException("A persona needs a system prompt.");

        var persona = new Persona
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            SystemPrompt = systemPrompt.Trim(),
            IsBuiltIn = false
        };
        lock (_gate)
        {
            EnsureLoaded();
            _customCache!.Add(persona);
            Save();
        }
        return persona;
    }

    public void Delete(string id)
    {
        if (BuiltIns.Any(p => p.Id == id)) throw new InvalidDataException("Built-in personas can't be deleted.");
        lock (_gate)
        {
            EnsureLoaded();
            _customCache!.RemoveAll(p => p.Id == id);
            if (_activeCache == id) _activeCache = "default";
            Save();
        }
    }

    public void SetActive(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            bool exists = BuiltIns.Any(p => p.Id == id) || _customCache!.Any(p => p.Id == id);
            if (!exists) throw new InvalidDataException("Unknown persona id.");
            _activeCache = id;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_customCache != null) return;
        _customCache = new List<Persona>();
        _activeCache = "default";
        try
        {
            if (File.Exists(StorePath))
            {
                var doc = JsonSerializer.Deserialize<PersonaStoreDocument>(File.ReadAllText(StorePath));
                if (doc != null)
                {
                    _customCache = doc.Custom ?? new List<Persona>();
                    if (!string.IsNullOrWhiteSpace(doc.ActivePersonaId)) _activeCache = doc.ActivePersonaId;
                }
            }
        }
        catch
        {
            // A corrupt store falls back to the safe default (no customs, default active).
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            string temp = StorePath + ".tmp";
            var doc = new PersonaStoreDocument { Custom = _customCache!, ActivePersonaId = _activeCache! };
            File.WriteAllText(temp, JsonSerializer.Serialize(doc));
            File.Move(temp, StorePath, true);
        }
        catch
        {
            // Best-effort persistence; the in-memory cache still reflects the request for this session.
        }
    }

    /// <summary>Live snapshot for backup - null if nothing has ever been customized (no custom
    /// personas, active selection still the default), matching every other backup-config method's
    /// "don't fabricate an empty entry" convention.</summary>
    internal PersonaStoreDocument? ExportForBackup()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_customCache!.Count == 0 && _activeCache == "default") return null;
            return new PersonaStoreDocument { Custom = new List<Persona>(_customCache!), ActivePersonaId = _activeCache };
        }
    }

    /// <summary>Restores personas from a backup: updates the live in-memory cache immediately (so
    /// an already-running server serves the restored personas without a restart) and persists the
    /// same content to disk.</summary>
    internal void ImportForBackup(PersonaStoreDocument document)
    {
        lock (_gate)
        {
            _customCache = document.Custom ?? new List<Persona>();
            _activeCache = string.IsNullOrWhiteSpace(document.ActivePersonaId) ? "default" : document.ActivePersonaId;
            Save();
        }
    }

    internal sealed class PersonaStoreDocument
    {
        public List<Persona>? Custom { get; set; }
        public string? ActivePersonaId { get; set; }
    }
}
