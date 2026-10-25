using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentCheckpointStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-checkpoints-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "parux-workspace-" + Guid.NewGuid().ToString("N"));

    public CodingAgentCheckpointStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public void Explicit_session_ids_are_sanitized_and_bounded()
    {
        string id = CodingAgentCheckpointStore.ResolveSessionId(_workspace, "goal", "../task:alpha?*" + new string('x', 200));

        Assert.DoesNotContain("..", id, StringComparison.Ordinal);
        Assert.DoesNotContain(':', id);
        Assert.DoesNotContain('?', id);
        Assert.DoesNotContain('*', id);
        Assert.True(id.Length <= 96);
    }

    [Fact]
    public void Derived_session_id_is_stable_for_same_workspace_and_goal_but_changes_for_another_goal()
    {
        string first = CodingAgentCheckpointStore.ResolveSessionId(_workspace, "fix parser", null);
        string again = CodingAgentCheckpointStore.ResolveSessionId(_workspace, "fix parser", null);
        string different = CodingAgentCheckpointStore.ResolveSessionId(_workspace, "fix renderer", null);

        Assert.Equal(first, again);
        Assert.NotEqual(first, different);
        Assert.StartsWith("chat-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_and_load_round_trip_execution_and_scoped_verification_state()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "task-123",
            WorkspaceRoot = _workspace,
            Task = "fix the regression",
            StepsTaken = 41,
            ConsecutiveNonProgressSteps = 7,
            Plan = "1. inspect\n2. edit\n3. verify",
            VerificationRequired = true,
            VerificationSatisfied = false,
            VerificationRequiredScopes = new List<string> { "primary", "backend" },
            VerificationSatisfiedScopes = new List<string> { "primary" },
            Observations = new List<string> { "edited src/App.cs", "dotnet test exit 1" }
        };

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint? loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "task-123", _workspace, "fix the regression");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.SchemaVersion);
        Assert.Equal(41, loaded.StepsTaken);
        Assert.Equal(7, loaded.ConsecutiveNonProgressSteps);
        Assert.Equal(checkpoint.Plan, loaded.Plan);
        Assert.True(loaded.VerificationRequired);
        Assert.False(loaded.VerificationSatisfied);
        Assert.Equal(new[] { "primary", "backend" }, loaded.VerificationRequiredScopes);
        Assert.Equal(new[] { "primary" }, loaded.VerificationSatisfiedScopes);
        Assert.Equal(checkpoint.Observations, loaded.Observations);
    }

    [Fact]
    public void Save_and_load_round_trips_permission_mode_and_exact_command_approval()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "permission-approval",
            WorkspaceRoot = _workspace,
            Task = "run the accepted check",
            PermissionMode = CodingAgentPermissionMode.Ask,
            PendingCommandApprovalQuestionId = "question-9",
            PendingCommandApprovalText = "dotnet test tests/App.Tests.csproj --filter FullyQualifiedName~Parser",
            ApprovedCommandText = string.Empty,
            PendingFileApprovalQuestionId = "question-file-9",
            PendingFileApprovalKey = "ABC123",
            ApprovedFileApprovalKey = "DEF456"
        };

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint? loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, checkpoint.SessionId, _workspace, checkpoint.Task);

        Assert.NotNull(loaded);
        Assert.Equal(CodingAgentPermissionMode.Ask, loaded!.PermissionMode);
        Assert.Equal("question-9", loaded.PendingCommandApprovalQuestionId);
        Assert.Equal(checkpoint.PendingCommandApprovalText, loaded.PendingCommandApprovalText);
        Assert.Empty(loaded.ApprovedCommandText);
        Assert.Equal("question-file-9", loaded.PendingFileApprovalQuestionId);
        Assert.Equal("ABC123", loaded.PendingFileApprovalKey);
        Assert.Equal("DEF456", loaded.ApprovedFileApprovalKey);
    }

    [Fact]
    public void Answering_user_question_never_waives_pending_verification_scopes_or_command_evidence()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "question-pending-tests",
            WorkspaceRoot = _workspace,
            Task = "repair app after user decision",
            StepsTaken = 21,
            ConsecutiveNonProgressSteps = 17,
            VerificationRequired = true,
            VerificationSatisfied = false,
            VerifiedByCommand = false,
            VerificationRequiredScopes = new List<string> { "primary", "backend" },
            VerificationSatisfiedScopes = new List<string> { "primary" },
            Observations = new List<string> { "changed code", "ask_user -> question-1" }
        };

        CodingAgentCheckpointStore.ResumeAfterUserAnswer(checkpoint);
        Assert.Equal(0, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Equal(21, checkpoint.StepsTaken);
        Assert.True(checkpoint.VerificationRequired);
        Assert.False(checkpoint.VerificationSatisfied);
        Assert.False(checkpoint.VerifiedByCommand);
        Assert.Equal(new[] { "primary", "backend" }, checkpoint.VerificationRequiredScopes);
        Assert.Equal(new[] { "primary" }, checkpoint.VerificationSatisfiedScopes);

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, checkpoint.SessionId, _workspace, checkpoint.Task)!;

        Assert.True(restored.VerificationRequired);
        Assert.False(restored.VerificationSatisfied);
        Assert.False(restored.VerifiedByCommand);
        Assert.Equal(new[] { "primary", "backend" }, restored.VerificationRequiredScopes);
        Assert.Equal(new[] { "primary" }, restored.VerificationSatisfiedScopes);
        Assert.Equal(checkpoint.Observations, restored.Observations);
    }

    [Fact]
    public void Legacy_answered_checkpoint_without_real_verification_receipt_is_invalidated_on_load()
    {
        var legacy = new CodingAgentCheckpoint
        {
            SessionId = "legacy-answered",
            WorkspaceRoot = _workspace,
            Task = "modify the application",
            VerificationRequired = true,
            VerificationSatisfied = true,
            VerifiedByCommand = false,
            VerificationRequiredScopes = new List<string> { "ui", "backend" },
            VerificationSatisfiedScopes = new List<string> { "ui", "backend" },
            Observations = new List<string>
            {
                "edited code with no passing test",
                "[USER INPUT question-123] Question: choose style\\nAnswer: dark"
            }
        };

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, legacy);
        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, legacy.SessionId, _workspace, legacy.Task)!;

        Assert.True(loaded.VerificationRequired);
        Assert.False(loaded.VerificationSatisfied);
        Assert.Empty(loaded.VerificationSatisfiedScopes);
        Assert.Equal(new[] { "ui", "backend" }, loaded.VerificationRequiredScopes);
        Assert.False(loaded.VerifiedByCommand);

        loaded.VerificationSatisfiedScopes.AddRange(loaded.VerificationRequiredScopes);
        loaded.VerificationSatisfied = true;
        loaded.VerifiedByCommand = true;
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, loaded);
        CodingAgentCheckpoint verified = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, loaded.SessionId, _workspace, loaded.Task)!;
        Assert.True(verified.VerificationSatisfied);
        Assert.True(verified.VerifiedByCommand);
    }

    [Fact]
    public void User_answer_preserves_previously_verified_state_without_inventing_new_success()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            ConsecutiveNonProgressSteps = 10,
            VerificationRequired = true,
            VerificationSatisfied = true,
            VerifiedByCommand = true,
            VerificationRequiredScopes = new List<string> { "." },
            VerificationSatisfiedScopes = new List<string> { "." }
        };

        CodingAgentCheckpointStore.ResumeAfterUserAnswer(checkpoint);

        Assert.Equal(0, checkpoint.ConsecutiveNonProgressSteps);
        Assert.True(checkpoint.VerificationRequired);
        Assert.True(checkpoint.VerificationSatisfied);
        Assert.True(checkpoint.VerifiedByCommand);
        Assert.Equal(new[] { "." }, checkpoint.VerificationSatisfiedScopes);
    }

    [Fact]
    public void Editing_after_accepted_verification_invalidates_all_scopes_even_after_restart()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "mutation-after-green",
            WorkspaceRoot = _workspace,
            Task = "refactor the UI",
            VerificationRequired = true,
            VerificationSatisfied = true,
            VerifiedByCommand = true,
            VerificationRequiredScopes = new List<string> { "primary", "backend" },
            VerificationSatisfiedScopes = new List<string> { "primary", "backend" },
            Observations = new List<string> { "prior tests passed" }
        };

        CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(checkpoint);
        Assert.True(checkpoint.VerificationRequired);
        Assert.False(checkpoint.VerificationSatisfied);
        Assert.False(checkpoint.VerifiedByCommand);
        Assert.Equal(new[] { "primary", "backend" }, checkpoint.VerificationRequiredScopes);
        Assert.Empty(checkpoint.VerificationSatisfiedScopes);

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        var reloaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, checkpoint.SessionId, _workspace, checkpoint.Task)!;
        Assert.True(reloaded.VerificationRequired);
        Assert.False(reloaded.VerificationSatisfied);
        Assert.False(reloaded.VerifiedByCommand);
        Assert.Empty(reloaded.VerificationSatisfiedScopes);

        CodingAgentCheckpointStore.RecordVerificationCommandOutcome(reloaded, succeeded: false);
        Assert.False(reloaded.VerificationSatisfied);
        Assert.Empty(reloaded.VerificationSatisfiedScopes);

        CodingAgentCheckpointStore.RecordVerificationCommandOutcome(reloaded, succeeded: true);
        Assert.True(reloaded.VerificationSatisfied);
        Assert.True(reloaded.VerifiedByCommand);
        Assert.Equal(new[] { "primary", "backend" }, reloaded.VerificationSatisfiedScopes);

        // Another edit after the green command must force another check.
        CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(reloaded);
        Assert.False(reloaded.VerificationSatisfied);
        Assert.False(reloaded.VerifiedByCommand);
        Assert.Empty(reloaded.VerificationSatisfiedScopes);
    }

    [Fact]
    public void Browser_interaction_postcondition_survives_checkpoint_roundtrip_and_user_answer()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "browser-observation",
            WorkspaceRoot = _workspace,
            Task = "fix local web app",
            BrowserPostconditionPending = true,
            StepsTaken = 7
        };
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, checkpoint.SessionId, _workspace, checkpoint.Task)!;

        Assert.True(restored.BrowserPostconditionPending);
        CodingAgentCheckpointStore.ResumeAfterUserAnswer(restored);
        Assert.True(restored.BrowserPostconditionPending);
        Assert.False(CodingBrowserPostconditionPolicy.MayReportDone(restored));
        CodingBrowserPostconditionPolicy.Apply(restored, "browser_assert", true,
            "text_equals", "Accepted");
        Assert.False(restored.BrowserPostconditionPending);
    }

    [Fact]
    public void Verified_workspace_fingerprint_survives_restart_but_not_a_later_edit_or_failed_command()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "verification-fingerprint",
            WorkspaceRoot = _workspace,
            Task = "fix native regression",
            VerificationRequired = true,
            VerificationSatisfied = true,
            VerifiedByCommand = true,
            VerificationRequiredScopes = new List<string> { "." },
            VerificationSatisfiedScopes = new List<string> { "." },
            VerifiedWorkspaceFingerprint = new string('a', 64)
        };

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, checkpoint.SessionId, _workspace, checkpoint.Task)!;
        Assert.Equal(new string('a', 64), restored.VerifiedWorkspaceFingerprint);

        CodingAgentCheckpointStore.RecordVerificationCommandOutcome(restored, succeeded: false);
        Assert.Null(restored.VerifiedWorkspaceFingerprint);
        Assert.False(restored.VerifiedByCommand);

        restored.VerifiedWorkspaceFingerprint = new string('b', 64);
        CodingAgentCheckpointStore.RecordVerificationCommandOutcome(restored, succeeded: true);
        Assert.Equal(new string('b', 64), restored.VerifiedWorkspaceFingerprint);

        CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(restored);
        Assert.Null(restored.VerifiedWorkspaceFingerprint);
        Assert.False(restored.VerifiedByCommand);
        Assert.False(restored.VerificationSatisfied);
    }

    [Fact]
    public void No_detected_build_command_cannot_implicitly_certify_mutated_code()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            VerificationRequired = false,
            VerificationSatisfied = true,
            VerifiedByCommand = false
        };
        CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(checkpoint);
        Assert.True(checkpoint.VerificationRequired);
        Assert.False(checkpoint.VerificationSatisfied);
        Assert.Equal(new[] { "." }, checkpoint.VerificationRequiredScopes);
        Assert.Empty(checkpoint.VerificationSatisfiedScopes);
    }

    [Fact]
    public void New_verifier_repair_resets_watchdog_once_without_erasing_execution_evidence()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "repair-task",
            WorkspaceRoot = _workspace,
            Task = "fix the regression",
            StepsTaken = 120,
            ConsecutiveNonProgressSteps = 120,
            Observations = new List<string> { "prior tool evidence" }
        };

        Assert.True(CodingAgentCheckpointStore.BeginRepairAttempt(checkpoint, 1));
        Assert.Equal(0, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Equal(120, checkpoint.StepsTaken);
        Assert.Equal(new[] { "prior tool evidence" }, checkpoint.Observations);

        checkpoint.ConsecutiveNonProgressSteps = 9;
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "repair-task", _workspace, "fix the regression")!;

        Assert.False(CodingAgentCheckpointStore.BeginRepairAttempt(restored, 1));
        Assert.Equal(9, restored.ConsecutiveNonProgressSteps);
        Assert.True(CodingAgentCheckpointStore.BeginRepairAttempt(restored, 2));
        Assert.Equal(0, restored.ConsecutiveNonProgressSteps);
        Assert.Equal(2, restored.LastRepairAttempt);
        Assert.Equal(new[] { "prior tool evidence" }, restored.Observations);
    }

    [Fact]
    public void Legacy_boolean_writer_is_bridged_to_default_scope_instead_of_losing_the_gate()
    {
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "legacy-writer",
            WorkspaceRoot = _workspace,
            Task = "goal",
            VerificationRequired = true,
            VerificationSatisfied = false
        });

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "legacy-writer", _workspace, "goal")!;

        Assert.True(loaded.VerificationRequired);
        Assert.False(loaded.VerificationSatisfied);
        Assert.Equal(new[] { "." }, loaded.VerificationRequiredScopes);
        Assert.Empty(loaded.VerificationSatisfiedScopes);
    }

    [Fact]
    public void Recovery_digest_is_bounded_and_contains_verification_truth_without_faking_success()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            StepsTaken = 12,
            Plan = "Fix command handler and rerun tests",
            VerificationRequired = true,
            VerificationSatisfied = false,
            VerifiedByCommand = false
        };
        var observations = new List<string>
        {
            "read_file sample.cs " + new string('x', 700),
            "replace_range src/App.cs: changed the handler",
            "run_command dotnet test: test failed on line 40",
            "read_file src/Other.cs",
            "read_file src/Other.cs"
        };
        string digest = CodingAgentRecoveryDigest.Build(checkpoint, observations, 650);

        Assert.True(digest.Length <= 650);
        Assert.Contains("Last completed step: 12", digest);
        Assert.Contains("verified by command: False", digest);
        Assert.Contains("verification required: True", digest);
        Assert.Contains("NOT independent verification", digest);
        Assert.Contains("replace_range", digest);
        Assert.Equal(string.Empty, CodingAgentRecoveryDigest.Build(checkpoint, observations, 0));
    }

    [Fact]
    public void Recovery_digest_is_regenerated_from_durable_evidence_instead_of_trusting_stored_claims()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "digest-task",
            WorkspaceRoot = _workspace,
            Task = "fix the regression",
            StepsTaken = 4,
            RecoveryDigest = "Verified and complete without a test",
            VerificationRequired = true,
            VerificationSatisfied = false,
            Observations = new List<string> { "run_command dotnet test: exit 1" }
        };

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "digest-task", _workspace, "fix the regression")!;

        Assert.DoesNotContain("Verified and complete without a test", loaded.RecoveryDigest);
        Assert.Contains("run_command dotnet test: exit 1", loaded.RecoveryDigest);
        Assert.Contains("verification satisfied: False", loaded.RecoveryDigest);
        Assert.Equal(4, loaded.StepsTaken);
        Assert.True(loaded.VerificationRequired);
        Assert.False(loaded.VerificationSatisfied);
        Assert.Equal(new[] { "run_command dotnet test: exit 1" }, loaded.Observations);
    }

    [Fact]
    public void Schema_v1_checkpoint_migrates_to_v2_without_dropping_verification()
    {
        string path = CodingAgentCheckpointStore.PathFor(_tempDir, "v1-session");
        string escapedWorkspace = System.Text.Json.JsonSerializer.Serialize(_workspace);
        File.WriteAllText(path,
            "{\"schemaVersion\":1,\"sessionId\":\"v1-session\",\"workspaceRoot\":" + escapedWorkspace
            + ",\"task\":\"goal\",\"verificationRequired\":true,\"verificationSatisfied\":true,\"observations\":[\"old evidence\"]}");

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "v1-session", _workspace, "goal")!;

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.True(loaded.VerificationRequired);
        Assert.True(loaded.VerificationSatisfied);
        Assert.Equal(new[] { "." }, loaded.VerificationRequiredScopes);
        Assert.Equal(new[] { "." }, loaded.VerificationSatisfiedScopes);
        Assert.Equal(new[] { "old evidence" }, loaded.Observations);
    }

    [Fact]
    public void Verification_scopes_are_normalized_bounded_and_satisfied_scopes_cannot_escape_required_set()
    {
        var required = Enumerable.Range(0, CodingAgentCheckpointStore.MaximumVerificationScopes + 5)
            .Select(index => index == 0 ? "backend\\api" : "repo-" + index)
            .Concat(new[] { "repo-1", "../escape", ".." })
            .ToList();

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "scopes",
            WorkspaceRoot = _workspace,
            Task = "goal",
            VerificationRequiredScopes = required,
            VerificationSatisfiedScopes = new List<string> { "backend/api", "repo-2", "not-required" }
        });

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "scopes", _workspace, "goal")!;

        Assert.True(loaded.VerificationRequiredScopes.Count <= CodingAgentCheckpointStore.MaximumVerificationScopes);
        Assert.Contains("backend/api", loaded.VerificationRequiredScopes);
        Assert.DoesNotContain("../escape", loaded.VerificationRequiredScopes);
        Assert.DoesNotContain("..", loaded.VerificationRequiredScopes);
        Assert.DoesNotContain("not-required", loaded.VerificationSatisfiedScopes);
        Assert.Contains("backend/api", loaded.VerificationSatisfiedScopes);
        Assert.Contains("repo-2", loaded.VerificationSatisfiedScopes);
    }

    [Fact]
    public void Direct_checkpoint_from_another_task_or_any_checkpoint_from_another_workspace_is_not_reused()
    {
        const string directGoal = "goal A";
        string directSession = CodingAgentCheckpointStore.ResolveSessionId(_workspace, directGoal, explicitSessionId: null);
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = directSession,
            WorkspaceRoot = _workspace,
            Task = directGoal,
            Observations = new List<string> { "direct evidence" }
        });

        Assert.Null(CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, directSession, _workspace, "goal B"));
        // The other owner's record is kept as a copy, so the next save of this run cannot destroy it unpreserved.
        string directCopy = Assert.Single(Directory.GetFiles(_tempDir, directSession + ".json.foreign-*"));
        Assert.Contains("direct evidence", File.ReadAllText(directCopy), StringComparison.Ordinal);

        const string durableSession = "task-durable-workspace-check";
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = durableSession,
            WorkspaceRoot = _workspace,
            Task = "goal A",
            Observations = new List<string> { "durable evidence" }
        });
        string otherWorkspace = Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid().ToString("N"));
        Assert.Null(CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, durableSession, otherWorkspace, "goal A with refreshed evidence"));
        string durableCopy = Assert.Single(Directory.GetFiles(_tempDir, durableSession + ".json.foreign-*"));
        Assert.Contains("durable evidence", File.ReadAllText(durableCopy), StringComparison.Ordinal);

        // The fresh run's save replaces the file, and the foreign record remains in its copy.
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = durableSession,
            WorkspaceRoot = otherWorkspace,
            Task = "goal A with refreshed evidence",
            Observations = new List<string> { "fresh run" }
        });
        Assert.Contains("durable evidence", File.ReadAllText(durableCopy), StringComparison.Ordinal);
    }

    [Fact]
    public void Observation_history_is_bounded_and_individual_observations_are_truncated_explicitly()
    {
        var observations = Enumerable.Range(0, CodingAgentCheckpointStore.MaximumObservations + 20)
            .Select(i => i == CodingAgentCheckpointStore.MaximumObservations + 19
                ? new string('z', CodingAgentCheckpointStore.MaximumObservationCharacters + 500)
                : "observation-" + i)
            .ToList();

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "bounded",
            WorkspaceRoot = _workspace,
            Task = "goal",
            Observations = observations
        });

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "bounded", _workspace, "goal")!;

        Assert.Equal(CodingAgentCheckpointStore.MaximumObservations, loaded.Observations.Count);
        Assert.DoesNotContain("observation-0", loaded.Observations);
        Assert.Contains("checkpoint truncated", loaded.Observations[^1], StringComparison.Ordinal);
        Assert.True(loaded.Observations[^1].Length < observations[^1].Length);
    }

    [Fact]
    public void Compact_checkpoint_keeps_real_artifact_handles_without_inventing_partial_ids()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            StepsTaken = 8,
            VerificationRequired = true,
            VerificationSatisfied = false
        };
        const string good = "read_file \"src/App.cs\"\nfile contents clipped "
            + "[FULL EVIDENCE artifactId=art-good-123; kind=workspace-file; chars=45000]";
        const string malicious = "read_file \"other.cs\"\n"
            + "[FULL EVIDENCE artifactId=art-not-safe/path; kind=workspace-file; chars=40000]";

        string digest = CodingAgentRecoveryDigest.Build(
            checkpoint, new[] { good, malicious }, maximumCharacters: 550);

        Assert.Contains("Saved artifact ID (read_artifact): art-good-123", digest);
        Assert.DoesNotContain("art-not-safe/path", digest);
        Assert.Contains("verification satisfied: False", digest);
        Assert.True(digest.Length <= 550);

        string tooSmall = CodingAgentRecoveryDigest.Build(
            checkpoint, new[] { good }, maximumCharacters: 110);
        Assert.DoesNotContain("art-good-", tooSmall);
        Assert.True(tooSmall.Length <= 110);
    }

    [Fact]
    public void Restarted_task_can_recover_full_artifact_id_from_tail_of_truncated_checkpoint()
    {
        const string id = "art-strong-evidence-42";
        string oversized = "read_file \"src/LargeFile.cs\"\n"
            + new string('L', CodingAgentCheckpointStore.MaximumObservationCharacters + 2_000)
            + "\n[FULL EVIDENCE artifactId=" + id
            + "; kind=workspace-file; chars=50000; sha256=feedbeef]";
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "artifact-tail",
            WorkspaceRoot = _workspace,
            Task = "continue fixing a large source file",
            StepsTaken = 6,
            VerificationRequired = true,
            VerificationSatisfied = false,
            Observations = new List<string> { oversized }
        };
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "artifact-tail", _workspace,
            "continue fixing a large source file")!;

        Assert.True(loaded.Observations[0].Length <=
            CodingAgentCheckpointStore.MaximumObservationCharacters);
        Assert.Contains("read_file", loaded.Observations[0]);
        Assert.Contains("checkpoint truncated", loaded.Observations[0]);
        Assert.Contains("artifactId=" + id, loaded.Observations[0]);
        Assert.Contains("Saved artifact ID (read_artifact): " + id, loaded.RecoveryDigest);
        Assert.False(loaded.VerificationSatisfied);
        Assert.Equal(6, loaded.StepsTaken);
    }

    [Fact]
    public void Write_ahead_dispatch_is_durable_and_unknown_outcome_never_claims_success()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "crash-during-write",
            WorkspaceRoot = _workspace,
            Task = "fix the parser",
            StepsTaken = 5,
            VerifiedByCommand = true,
            VerificationRequired = true,
            VerificationSatisfied = true,
            VerificationRequiredScopes = new List<string> { "." },
            VerificationSatisfiedScopes = new List<string> { "." }
        };
        CodingAgentDispatchJournal.Begin(checkpoint, 6, "replace_range");
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);

        CodingAgentCheckpoint resumed = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "crash-during-write", _workspace, "fix the parser")!;
        Assert.Equal(5, resumed.StepsTaken);
        Assert.Equal(6, resumed.PendingActionStep);

        string recovery = CodingAgentDispatchJournal.ReconcileInterruptedAction(resumed);
        Assert.Contains("step 6", recovery);
        Assert.Contains("may or may not", recovery);
        Assert.Equal(6, resumed.StepsTaken);
        Assert.Equal(0, resumed.PendingActionStep);
        Assert.True(resumed.VerificationRequired);
        Assert.False(resumed.VerificationSatisfied);
        Assert.False(resumed.VerifiedByCommand);

        resumed.Observations.Add(recovery);
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, resumed);
        var again = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "crash-during-write", _workspace, "fix the parser")!;
        Assert.Equal(6, again.StepsTaken);
        Assert.Equal(0, again.PendingActionStep);
        Assert.False(again.VerificationSatisfied);
        Assert.Contains("INTERRUPTED ACTION RECOVERY", again.RecoveryDigest
            + string.Join("\n", again.Observations));
        Assert.Equal(string.Empty, CodingAgentDispatchJournal.ReconcileInterruptedAction(again));
    }

    [Fact]
    public void Completed_dispatch_cannot_be_reconciled_or_repeated_as_uncertain()
    {
        var checkpoint = new CodingAgentCheckpoint { StepsTaken = 4 };
        CodingAgentDispatchJournal.Begin(checkpoint, 5, "run_command");
        Assert.Throws<InvalidOperationException>(() =>
            CodingAgentDispatchJournal.Begin(checkpoint, 5, "run_command"));
        Assert.Throws<InvalidOperationException>(() =>
            CodingAgentDispatchJournal.Complete(checkpoint, 6));

        CodingAgentDispatchJournal.Complete(checkpoint, 5);
        checkpoint.StepsTaken = 5;

        Assert.Equal(0, checkpoint.PendingActionStep);
        Assert.Equal(string.Empty, CodingAgentDispatchJournal.ReconcileInterruptedAction(checkpoint));
        Assert.Throws<InvalidOperationException>(() =>
            CodingAgentDispatchJournal.Begin(checkpoint, 5, "write_file"));
    }

    [Fact]
    public void Corrupt_checkpoint_is_preserved_and_stops_the_run_instead_of_being_discarded()
    {
        string corruptPath = CodingAgentCheckpointStore.PathFor(_tempDir, "corrupt");
        File.WriteAllText(corruptPath, "{not-json");

        var error = Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "corrupt", _workspace, "goal"));
        Assert.Equal(corruptPath, error.CheckpointPath);
        Assert.NotNull(error.QuarantinePath);
        Assert.Equal("{not-json", File.ReadAllText(error.QuarantinePath!));
        // The original is never overwritten, so a retry sees the same evidence and reuses the same copy.
        Assert.Equal("{not-json", File.ReadAllText(corruptPath));
        Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "corrupt", _workspace, "goal"));
        Assert.Single(Directory.GetFiles(_tempDir, "corrupt.json.corrupt-*"));
    }

    [Fact]
    public void Zero_length_and_truncated_checkpoints_are_corrupt_not_absent()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "torn-write",
            WorkspaceRoot = _workspace,
            Task = "goal",
            StepsTaken = 5
        };
        CodingAgentDispatchJournal.Begin(checkpoint, 6, "replace_range");
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);
        string path = CodingAgentCheckpointStore.PathFor(_tempDir, "torn-write");
        string complete = File.ReadAllText(path);

        File.WriteAllText(path, complete[..(complete.Length / 2)]);
        Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "torn-write", _workspace, "goal"));

        File.WriteAllText(path, string.Empty);
        Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "torn-write", _workspace, "goal"));
    }

    [Fact]
    public void Missing_checkpoint_is_absent_and_clear_removes_a_readable_one()
    {
        Assert.Null(CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "never-written", _workspace, "goal"));

        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "clear-me",
            WorkspaceRoot = _workspace,
            Task = "goal"
        });
        string path = CodingAgentCheckpointStore.PathFor(_tempDir, "clear-me");
        Assert.True(File.Exists(path));

        CodingAgentCheckpointStore.ClearFromDirectory(_tempDir, "clear-me");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Checkpoint_bytes_are_flushed_to_stable_storage_before_they_replace_the_receipt()
    {
        string final = CodingAgentCheckpointStore.PathFor(_tempDir, "durable");
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "durable",
            WorkspaceRoot = _workspace,
            Task = "goal",
            Observations = new List<string> { "previous receipt" }
        });

        // The seam is the flush itself: it records each flush of this directory's files and still flushes to disk.
        var flushed = new List<(string Temporary, bool FinalHeldPrevious)>();
        CodingDurableFile.FlushToStableStorage = stream =>
        {
            if (stream.Name.StartsWith(_tempDir, StringComparison.Ordinal))
                flushed.Add((stream.Name, File.ReadAllText(final).Contains("previous receipt", StringComparison.Ordinal)));
            stream.Flush(flushToDisk: true);
        };
        try
        {
            CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
            {
                SessionId = "durable",
                WorkspaceRoot = _workspace,
                Task = "goal",
                Observations = new List<string> { "new receipt" }
            });
        }
        finally
        {
            CodingDurableFile.FlushToStableStorage = static stream => stream.Flush(flushToDisk: true);
        }

        (string temporary, bool finalHeldPrevious) = Assert.Single(flushed);
        Assert.Contains(".tmp-", temporary, StringComparison.Ordinal);
        // The previous receipt was still in place when the new bytes were flushed, so a crash there loses nothing.
        Assert.True(finalHeldPrevious);
        Assert.Contains("new receipt", File.ReadAllText(final), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp-*"));
    }

    [Fact]
    public void A_failed_flush_keeps_the_previous_receipt_and_leaves_no_temporary_file()
    {
        string final = CodingAgentCheckpointStore.PathFor(_tempDir, "failed-flush");
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
        {
            SessionId = "failed-flush",
            WorkspaceRoot = _workspace,
            Task = "goal",
            Observations = new List<string> { "previous receipt" }
        });
        string previous = File.ReadAllText(final);

        CodingDurableFile.FlushToStableStorage = stream =>
        {
            if (stream.Name.StartsWith(_tempDir, StringComparison.Ordinal))
                throw new IOException("Simulated disk full during flush.");
            stream.Flush(flushToDisk: true);
        };
        try
        {
            Assert.Throws<IOException>(() => CodingAgentCheckpointStore.SaveToDirectory(_tempDir, new CodingAgentCheckpoint
            {
                SessionId = "failed-flush",
                WorkspaceRoot = _workspace,
                Task = "goal",
                Observations = new List<string> { "new receipt" }
            }));
        }
        finally
        {
            CodingDurableFile.FlushToStableStorage = static stream => stream.Flush(flushToDisk: true);
        }

        // The receipt is replaced only after its bytes are flushed, so the previous one survives intact.
        Assert.Equal(previous, File.ReadAllText(final));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp-*"));
    }

    [Fact]
    public void A_copy_that_cannot_be_written_is_not_reported_as_preserved()
    {
        string corruptPath = CodingAgentCheckpointStore.PathFor(_tempDir, "copy-fails");
        File.WriteAllText(corruptPath, "{not-json");
        CodingDurableFile.FlushToStableStorage = stream =>
        {
            if (stream.Name.StartsWith(_tempDir, StringComparison.Ordinal) && stream.Name.Contains(".corrupt-", StringComparison.Ordinal))
                throw new IOException("Simulated disk full while preserving the copy.");
            stream.Flush(flushToDisk: true);
        };
        CodingAgentCheckpointCorruptException error;
        try
        {
            error = Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
                CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "copy-fails", _workspace, "goal"));
        }
        finally
        {
            CodingDurableFile.FlushToStableStorage = static stream => stream.Flush(flushToDisk: true);
        }

        // A failed copy is never published under its final name, so no truncated file is reported as the evidence.
        Assert.Null(error.QuarantinePath);
        Assert.Empty(Directory.GetFiles(_tempDir, "copy-fails.json.corrupt-*"));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp-*"));
        Assert.Equal("{not-json", File.ReadAllText(corruptPath));
    }

    [Fact]
    public void A_damaged_existing_copy_is_rewritten_from_the_original_before_it_is_reported()
    {
        File.WriteAllText(CodingAgentCheckpointStore.PathFor(_tempDir, "damaged-copy"), "{not-json");
        var first = Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "damaged-copy", _workspace, "goal"));
        Assert.NotNull(first.QuarantinePath);
        // The copy is truncated after it was written, and the original is still intact.
        File.WriteAllText(first.QuarantinePath!, "{no");

        var second = Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "damaged-copy", _workspace, "goal"));

        Assert.Equal(first.QuarantinePath, second.QuarantinePath);
        Assert.Equal("{not-json", File.ReadAllText(second.QuarantinePath!));
        Assert.Single(Directory.GetFiles(_tempDir, "damaged-copy.json.corrupt-*"));
    }

    [Fact]
    public void A_checkpoint_path_that_cannot_be_read_is_an_error_not_an_absent_checkpoint()
    {
        // A directory at the checkpoint path is neither a readable checkpoint nor an absent one.
        Directory.CreateDirectory(CodingAgentCheckpointStore.PathFor(_tempDir, "unreadable"));

        Exception? error = Record.Exception(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_tempDir, "unreadable", _workspace, "goal"));

        Assert.True(error is IOException or UnauthorizedAccessException,
            error?.GetType().FullName ?? "No exception: the run would start fresh.");
    }

    [Fact]
    public void Consumed_user_answer_survives_observation_trimming_so_a_stale_answer_is_not_consumed_again()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "answered",
            WorkspaceRoot = _workspace,
            Task = "goal"
        };
        CodingAgentCheckpointStore.RecordConsumedUserInput(checkpoint, "question-1");
        // The answer's marker line is the oldest entry, so the bounded observation window trims it away.
        checkpoint.Observations = new List<string> { "[USER INPUT question-1] Question: q\nAnswer: a" };
        checkpoint.Observations.AddRange(Enumerable.Range(0, CodingAgentCheckpointStore.MaximumObservations + 20)
            .Select(i => "observation-" + i));
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "answered", _workspace, "goal")!;

        Assert.DoesNotContain(loaded.Observations, entry => entry.StartsWith("[USER INPUT question-1]", StringComparison.Ordinal));
        Assert.True(CodingAgentCheckpointStore.HasConsumedUserInput(loaded, "question-1"));
        Assert.False(CodingAgentCheckpointStore.HasConsumedUserInput(loaded, "question-2"));
    }

    [Fact]
    public void Consumed_user_answer_ids_are_distinct_and_bounded()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "answers-bounded",
            WorkspaceRoot = _workspace,
            Task = "goal"
        };
        int total = CodingAgentCheckpointStore.MaximumConsumedUserInputs + 10;
        for (int i = 0; i < total; i++)
            CodingAgentCheckpointStore.RecordConsumedUserInput(checkpoint, "q-" + i);
        CodingAgentCheckpointStore.RecordConsumedUserInput(checkpoint, "q-" + (total - 1));
        CodingAgentCheckpointStore.SaveToDirectory(_tempDir, checkpoint);

        CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(
            _tempDir, "answers-bounded", _workspace, "goal")!;

        Assert.Equal(CodingAgentCheckpointStore.MaximumConsumedUserInputs, loaded.ConsumedUserInputIds.Count);
        Assert.True(CodingAgentCheckpointStore.HasConsumedUserInput(loaded, "q-" + (total - 1)));
        Assert.False(CodingAgentCheckpointStore.HasConsumedUserInput(loaded, "q-0"));
    }

    [Fact]
    public void Consumed_answer_whose_marker_was_trimmed_is_not_new_and_does_not_reset_the_watchdog()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "trimmed",
            WorkspaceRoot = _workspace,
            Task = "goal",
            ConsecutiveNonProgressSteps = 7
        };
        CodingAgentCheckpointStore.RecordConsumedUserInput(checkpoint, "question-1");
        var log = new List<string> { "observation after the marker was trimmed" };

        bool consumed = CodingAgentCheckpointStore.ConsumeUserAnswers(
            checkpoint, new[] { new CodingAgentCheckpointStore.UserAnswerExchange("question-1", "Which file?", "parser.cs") }, log);

        Assert.False(consumed);
        Assert.Equal(7, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Equal(new[] { "observation after the marker was trimmed" }, log);
    }

    [Fact]
    public void Legacy_checkpoint_with_a_logged_marker_is_not_new_and_its_id_is_recorded_so_trimming_cannot_revive_it()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "legacy",
            WorkspaceRoot = _workspace,
            Task = "goal",
            ConsecutiveNonProgressSteps = 7
        };
        var log = new List<string> { "[USER INPUT question-1] Question: q\nAnswer: a" };

        bool consumed = CodingAgentCheckpointStore.ConsumeUserAnswers(
            checkpoint, new[] { new CodingAgentCheckpointStore.UserAnswerExchange("question-1", "q", "a") }, log);

        Assert.False(consumed);
        Assert.Equal(7, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Single(log);
        Assert.True(CodingAgentCheckpointStore.HasConsumedUserInput(checkpoint, "question-1"));
    }

    [Fact]
    public void Unseen_answer_is_appended_recorded_and_resets_the_watchdog_once()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "fresh-answer",
            WorkspaceRoot = _workspace,
            Task = "goal",
            ConsecutiveNonProgressSteps = 7
        };
        var log = new List<string>();
        var exchanges = new[] { new CodingAgentCheckpointStore.UserAnswerExchange("question-2", "Which file?", "parser.cs") };

        bool consumed = CodingAgentCheckpointStore.ConsumeUserAnswers(checkpoint, exchanges, log);

        Assert.True(consumed);
        Assert.Equal(0, checkpoint.ConsecutiveNonProgressSteps);
        Assert.True(CodingAgentCheckpointStore.HasConsumedUserInput(checkpoint, "question-2"));
        string entry = Assert.Single(log);
        Assert.StartsWith("[USER INPUT question-2] Question: Which file?", entry, StringComparison.Ordinal);
        Assert.Contains("Answer: parser.cs", entry, StringComparison.Ordinal);

        // The same answer seen again on the next resume is not new, so it does not reset the watchdog again.
        checkpoint.ConsecutiveNonProgressSteps = 5;
        Assert.False(CodingAgentCheckpointStore.ConsumeUserAnswers(checkpoint, exchanges, log));
        Assert.Equal(5, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Single(log);
    }

    [Fact]
    public void Empty_answer_is_not_consumed_and_does_not_reset_the_watchdog()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "empty-answer",
            WorkspaceRoot = _workspace,
            Task = "goal",
            ConsecutiveNonProgressSteps = 7
        };
        var log = new List<string>();

        bool consumed = CodingAgentCheckpointStore.ConsumeUserAnswers(
            checkpoint, new[] { new CodingAgentCheckpointStore.UserAnswerExchange("question-3", "Which file?", "   ") }, log);

        Assert.False(consumed);
        Assert.Equal(7, checkpoint.ConsecutiveNonProgressSteps);
        Assert.Empty(log);
        // The question stays open, so a later real answer is still delivered.
        Assert.False(CodingAgentCheckpointStore.HasConsumedUserInput(checkpoint, "question-3"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
