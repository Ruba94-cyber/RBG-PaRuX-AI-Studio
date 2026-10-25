using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Durable human-in-loop primitive for long-running agents. Questions and answers are written into
/// the existing SQLite-backed task log so they survive Core restarts/backups without a parallel
/// transcript store. User text is data only: it never widens task capabilities or executes code.
/// </summary>
public sealed class AgentUserQuestionService
{
    public const string WaitingStage = "Waiting for user input";
    private const string Prefix = "[parux:user-question:v1] ";
    private const int MaxQuestionCharacters = 2_000;
    private const int MaxAnswerCharacters = 4_000;
    private const int MaxOptions = 6;
    private const int MaxOptionCharacters = 240;
    private const int MaxRecentExchanges = 12;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TaskStore _tasks;

    public AgentUserQuestionService(TaskStore tasks) => _tasks = tasks;

    private sealed class LedgerEvent
    {
        public string Kind { get; set; } = string.Empty;
        public string QuestionId { get; set; } = string.Empty;
        public string? Question { get; set; }
        public List<string>? Options { get; set; }
        public bool AllowOther { get; set; }
        public string? Answer { get; set; }
    }

    private sealed record QuestionRecord(AgentUserQuestionDto Question, string? Answer, DateTime? AnsweredUtc);

    public AgentUserQuestionDto Ask(string taskId, AskAgentUserQuestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentTaskDto task = RequireTask(taskId);
        AgentUserQuestionStateDto existing = GetState(taskId);
        if (existing.PendingQuestion != null)
        {
            EnsureWaiting(task, existing.PendingQuestion.Id);
            return existing.PendingQuestion;
        }
        if (!string.Equals(task.Status, AgentTaskStatus.Running, StringComparison.Ordinal))
            throw new InvalidDataException("Only a running task may raise a new user question.");

        string question = BoundRequired(request.Question, MaxQuestionCharacters, "question");
        List<string> options = NormalizeOptions(request.Options);
        bool allowOther = request.AllowOther || options.Count == 0;
        var dto = new AgentUserQuestionDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Question = question,
            Options = options,
            AllowOther = allowOther,
            AskedUtc = DateTime.UtcNow
        };

        AppendEvent(taskId, new LedgerEvent
        {
            Kind = "question",
            QuestionId = dto.Id,
            Question = dto.Question,
            Options = dto.Options,
            AllowOther = dto.AllowOther
        });
        EnsureWaiting(task, dto.Id);
        return dto;
    }

    public AgentUserQuestionStateDto GetState(string taskId)
    {
        _ = RequireTask(taskId);
        List<QuestionRecord> records = ReadRecords(taskId);
        QuestionRecord? pending = records.LastOrDefault(record => record.Answer == null);
        return new AgentUserQuestionStateDto
        {
            PendingQuestion = pending?.Question,
            RecentExchanges = records
                .Where(record => record.Answer != null && record.AnsweredUtc.HasValue)
                .TakeLast(MaxRecentExchanges)
                .Select(record => new AgentUserQuestionExchangeDto
                {
                    QuestionId = record.Question.Id,
                    Question = record.Question.Question,
                    Options = record.Question.Options.ToList(),
                    AllowOther = record.Question.AllowOther,
                    Answer = record.Answer!,
                    AskedUtc = record.Question.AskedUtc,
                    AnsweredUtc = record.AnsweredUtc!.Value
                })
                .ToList()
        };
    }

    public AgentUserQuestionStateDto Answer(string taskId, AnswerAgentUserQuestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentTaskDto task = RequireTask(taskId);
        string questionId = BoundRequired(request.QuestionId, 96, "questionId");
        string answer = BoundRequired(request.Answer, MaxAnswerCharacters, "answer");
        List<QuestionRecord> records = ReadRecords(taskId);

        QuestionRecord? alreadyAnswered = records.LastOrDefault(record =>
            string.Equals(record.Question.Id, questionId, StringComparison.Ordinal)
            && record.Answer != null);
        if (alreadyAnswered != null)
        {
            if (!string.Equals(alreadyAnswered.Answer, answer, StringComparison.Ordinal))
                throw new InvalidDataException("That user question was already answered with a different value.");
            EnsureRunningAfterAnswer(taskId, task, questionId);
            return GetState(taskId);
        }

        QuestionRecord? pending = records.LastOrDefault(record => record.Answer == null);
        if (pending == null || !string.Equals(pending.Question.Id, questionId, StringComparison.Ordinal))
            throw new InvalidDataException("The requested user question is not the task's pending question.");
        if (!string.Equals(task.Status, AgentTaskStatus.Waiting, StringComparison.Ordinal)
            || !string.Equals(task.Stage, WaitingStage, StringComparison.Ordinal))
            throw new InvalidDataException("This task is not currently paused for user input.");

        if (!pending.Question.AllowOther && pending.Question.Options.Count > 0
            && !pending.Question.Options.Contains(answer, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Answer must match one of the offered options for this question.");

        AppendEvent(taskId, new LedgerEvent
        {
            Kind = "answer",
            QuestionId = questionId,
            Answer = answer
        });
        EnsureRunningAfterAnswer(taskId, task, questionId);
        return GetState(taskId);
    }

    private AgentTaskDto RequireTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidDataException("A task id is required.");
        return _tasks.Get(taskId.Trim()) ?? throw new InvalidDataException("Task not found.");
    }

    private void EnsureWaiting(AgentTaskDto task, string questionId)
    {
        if (task.Status == AgentTaskStatus.Waiting && string.Equals(task.Stage, WaitingStage, StringComparison.Ordinal)) return;
        if (task.Status != AgentTaskStatus.Running)
            throw new InvalidDataException("Task state changed before the user question could be paused.");
        _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = WaitingStage,
            ProgressPercent = task.ProgressPercent
        });
        _tasks.AppendLog(task.Id, "Agent paused for durable user question " + questionId + ".");
    }

    private void EnsureRunningAfterAnswer(string taskId, AgentTaskDto task, string questionId)
    {
        AgentTaskDto current = _tasks.Get(taskId) ?? task;
        if (current.Status == AgentTaskStatus.Running) return;
        if (current.Status != AgentTaskStatus.Waiting || !string.Equals(current.Stage, WaitingStage, StringComparison.Ordinal))
            throw new InvalidDataException("Task state changed before the answered question could resume it.");
        _tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Resumed after user input",
            ProgressPercent = current.ProgressPercent
        });
        _tasks.AppendLog(taskId, "User answered durable question " + questionId + "; task resumed from preserved state.");
    }

    private void AppendEvent(string taskId, LedgerEvent value) =>
        _tasks.AppendLog(taskId, Prefix + JsonSerializer.Serialize(value, JsonOptions));

    private List<QuestionRecord> ReadRecords(string taskId)
    {
        var records = new List<QuestionRecord>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AgentTaskLogEntryDto entry in _tasks.GetLogs(taskId))
        {
            if (!entry.Message.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            LedgerEvent? value;
            try { value = JsonSerializer.Deserialize<LedgerEvent>(entry.Message[Prefix.Length..], JsonOptions); }
            catch (JsonException) { continue; }
            if (value == null || string.IsNullOrWhiteSpace(value.QuestionId)) continue;

            if (string.Equals(value.Kind, "question", StringComparison.Ordinal))
            {
                if (index.ContainsKey(value.QuestionId)) continue;
                string? question = NormalizeLedgerText(value.Question, MaxQuestionCharacters);
                if (question == null) continue;
                List<string> options = NormalizeLedgerOptions(value.Options);
                index[value.QuestionId] = records.Count;
                records.Add(new QuestionRecord(new AgentUserQuestionDto
                {
                    Id = value.QuestionId,
                    Question = question,
                    Options = options,
                    AllowOther = value.AllowOther || options.Count == 0,
                    AskedUtc = entry.TimestampUtc
                }, null, null));
                continue;
            }

            if (string.Equals(value.Kind, "answer", StringComparison.Ordinal)
                && index.TryGetValue(value.QuestionId, out int recordIndex)
                && records[recordIndex].Answer == null)
            {
                string? answer = NormalizeLedgerText(value.Answer, MaxAnswerCharacters);
                if (answer != null)
                    records[recordIndex] = records[recordIndex] with { Answer = answer, AnsweredUtc = entry.TimestampUtc };
            }
        }
        return records;
    }

    private static string BoundRequired(string? value, int max, string field)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) throw new InvalidDataException(field + " is required.");
        if (normalized.Length > max) throw new InvalidDataException(field + " exceeds " + max + " characters.");
        return normalized;
    }

    private static List<string> NormalizeOptions(IEnumerable<string>? values)
    {
        var result = (values ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (result.Count > MaxOptions) throw new InvalidDataException("options exceeds " + MaxOptions + " items.");
        if (result.Any(value => value.Length > MaxOptionCharacters))
            throw new InvalidDataException("An option exceeds " + MaxOptionCharacters + " characters.");
        return result;
    }

    private static string? NormalizeLedgerText(string? value, int max)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > max) return null;
        return normalized;
    }

    private static List<string> NormalizeLedgerOptions(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length is > 0 and <= MaxOptionCharacters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxOptions)
            .ToList();
}
