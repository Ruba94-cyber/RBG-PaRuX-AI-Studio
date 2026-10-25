using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record RepositoryReviewEntry(
        string Alias,
        bool HasChanges,
        int ChangedFiles,
        string DiffStat,
        string? Error = null);

    public sealed record MultiRepositoryReviewResult(
        bool Success,
        bool HasChanges,
        IReadOnlyList<RepositoryReviewEntry> Repositories,
        string Summary,
        string DiffStat,
        string? Error = null);

    public sealed record MultiRepositoryApplyResult(
        bool Success,
        string Message,
        IReadOnlyList<RepositoryReviewEntry> Repositories,
        bool RollbackAttempted = false,
        bool RollbackSucceeded = true,
        // Repositories this attempt wrote, and repositories that already held the reviewed change and were left alone.
        // Callers derive their status from these counts, never from the wording of Message.
        int AppliedCount = 0,
        int AlreadyPresentCount = 0);

    /// <summary>
    /// Review/apply boundary for an aggregate PaRuX multi-repository task workspace. Every binary
    /// patch must pass `git apply --check` against the current target; unrelated staged, unstaged and
    /// untracked target changes are preserved, while overlapping patches are rejected before any
    /// repository is changed. An apply holds an exclusive cross-process lease on every original repository and writes
    /// a durable journal, containing the reviewed patches and their digests, before the first mutation. If a later
    /// apply or verification fails, PaRuX reverse-applies the patches it wrote, in reverse order, and reports whether
    /// rollback succeeded. A journal that survives a crash is settled by the next apply of the same set: each patch is
    /// classified with reverse and forward checks, wholly present patches are reversed, and nothing is written at all
    /// unless every repository is in a known state. No commit, push, rebase, stash or merge is ever performed.
    /// </summary>
    public static class MultiRepositoryApplyService
    {
        private const string JournalFileName = "PENDING.json";
        private const string JournalFolderName = "multi-apply-journal";

        // Absent: the patch applies forward, so nothing it writes is present. Present: the whole patch reverses, so the
        // reviewed change is in the repository. Inconclusive: neither, a partly applied or externally edited repository.
        private enum PatchState
        {
            Absent,
            Present,
            Inconclusive
        }

        private sealed class Prepared
        {
            public required AgentRepositoryAuthorityDto Authority;
            public required string Alias;
            public required string Target;
            public required string Worktree;
            public required string PatchPath;
            public required RepositoryReviewEntry Review;
            public bool Applied;
            public bool ApplyStarted;
            // True once a non-empty patch was written for this repository. A prepared patch that later
            // disappears must fail the apply, never be treated as "nothing to apply".
            public bool HasPatch;
            // True when the reviewed patch already reverses cleanly in the original repository. This attempt never
            // writes such an alias and never reverses it, because it did not bring the change in.
            public bool AlreadyPresent;
            public List<string> IntentAdded { get; } = new();
        }

        // Written before the first mutation of an apply. It names only the repositories this attempt writes.
        private sealed class JournalManifest
        {
            public string? SetKey { get; set; }
            public string? CreatedUtc { get; set; }
            public bool Completed { get; set; }
            public JournalRepository[]? Repositories { get; set; }
        }

        private sealed class JournalRepository
        {
            public string? Alias { get; set; }
            public string? TargetRoot { get; set; }
            public string? PatchFile { get; set; }
            public string? PatchSha256 { get; set; }
        }

        public static async Task<MultiRepositoryReviewResult> ReviewAsync(
            AgentRepositorySetDto authorities,
            string aggregateWorkspace,
            CancellationToken ct)
        {
            if (authorities == null || authorities.Repositories.Count < 2)
                return new MultiRepositoryReviewResult(false, false, Array.Empty<RepositoryReviewEntry>(),
                    "Multi-repository review requires at least two explicit repository authorities.", string.Empty,
                    "Repository authority set is missing or not multi-repository.");
            if (authorities.Repositories.Count > 16
                || authorities.Repositories.Any(a => a == null
                    || !CodingRepositoryAliasPolicy.IsValid(a.Alias))
                || authorities.Repositories.Select(a => a.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != authorities.Repositories.Count)
                return new MultiRepositoryReviewResult(false, false, Array.Empty<RepositoryReviewEntry>(),
                    "Unsafe, duplicate or excessive repository aliases.", string.Empty,
                    "Repository authority identities failed validation before review.");
            if (string.IsNullOrWhiteSpace(aggregateWorkspace) || !Directory.Exists(aggregateWorkspace))
                return new MultiRepositoryReviewResult(false, false, Array.Empty<RepositoryReviewEntry>(),
                    "The multi-repository task workspace no longer exists.", string.Empty,
                    "Aggregate worktree directory is missing.");

            var reviews = new List<RepositoryReviewEntry>();
            foreach (AgentRepositoryAuthorityDto authority in authorities.Repositories)
            {
                ct.ThrowIfCancellationRequested();
                string alias;
                try { alias = ValidateAlias(authority.Alias); }
                catch (InvalidDataException ex)
                {
                    reviews.Add(new RepositoryReviewEntry(authority.Alias, false, 0, string.Empty, ex.Message));
                    continue;
                }
                string worktree = Path.Combine(Path.GetFullPath(aggregateWorkspace), alias);
                if (!Directory.Exists(worktree))
                {
                    reviews.Add(new RepositoryReviewEntry(alias, false, 0, string.Empty, "Task worktree is missing."));
                    continue;
                }
                GitResult probe = await GitAsync(worktree, ct, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
                if (!probe.Ok)
                {
                    reviews.Add(new RepositoryReviewEntry(alias, false, 0, string.Empty,
                        "Task alias is not a valid Git worktree: " + Bound(probe.Error, 400)));
                    continue;
                }
                GitResult status = await GitAsync(worktree, ct, "status", "--porcelain=v1").ConfigureAwait(false);
                GitResult stat = await GitAsync(worktree, ct, "diff", "--stat", "HEAD", "--").ConfigureAwait(false);
                bool changed = status.Ok && !string.IsNullOrWhiteSpace(status.Output);
                reviews.Add(new RepositoryReviewEntry(
                    alias,
                    changed,
                    changed ? CountStatusRecords(status.Output) : 0,
                    stat.Ok ? Bound(stat.Output.Trim(), 2_000) : string.Empty,
                    status.Ok ? null : Bound(status.Error, 500)));
            }

            bool success = reviews.Count == authorities.Repositories.Count && reviews.All(item => item.Error == null);
            bool any = reviews.Any(item => item.HasChanges);
            string combined = string.Join("\n\n", reviews.Select(item =>
                "[" + item.Alias + "] " + (item.HasChanges ? item.ChangedFiles + " changed file(s)" : "clean")
                + (string.IsNullOrWhiteSpace(item.DiffStat) ? string.Empty : "\n" + item.DiffStat)
                + (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : "\nERROR: " + item.Error)));
            string summary = success
                ? (any
                    ? reviews.Where(item => item.HasChanges).Sum(item => item.ChangedFiles) + " changed file(s) across "
                        + reviews.Count(item => item.HasChanges) + " repository alias(es)."
                    : "No uncommitted task changes across the multi-repository workspace.")
                : "One or more repository aliases could not be reviewed safely.";
            return new MultiRepositoryReviewResult(success, any, reviews, summary, Bound(combined, 8_000),
                success ? null : "Review is incomplete; apply is blocked.");
        }

        public static async Task<MultiRepositoryApplyResult> ApplyAsync(
            AgentRepositorySetDto authorities,
            string aggregateWorkspace,
            CancellationToken ct,
            string? approvedWorkspaceFingerprint = null)
        {
            MultiRepositoryReviewResult review = await ReviewAsync(authorities, aggregateWorkspace, ct).ConfigureAwait(false);
            if (!review.Success) return new MultiRepositoryApplyResult(false, review.Error ?? review.Summary, review.Repositories);
            if (!review.HasChanges) return new MultiRepositoryApplyResult(false, "The task workspace has no changes to apply.", review.Repositories);

            if (approvedWorkspaceFingerprint != null)
            {
                string? atAdmission = await CodingWorkspaceVerificationFingerprint
                    .CaptureAsync(aggregateWorkspace, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(atAdmission)
                    || !string.Equals(atAdmission, approvedWorkspaceFingerprint, StringComparison.Ordinal))
                    return new MultiRepositoryApplyResult(false,
                        "One or more coding worktrees changed after user review. Nothing was applied; review all repositories again.",
                        review.Repositories);
            }

            var targets = new List<(string Alias, string Target)>();
            try
            {
                foreach (AgentRepositoryAuthorityDto authority in authorities.Repositories)
                {
                    string alias = ValidateAlias(authority.Alias);
                    targets.Add((alias, CanonicalDirectory(authority.WorkspaceRoot, "original repository " + alias)));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or DirectoryNotFoundException)
            {
                return new MultiRepositoryApplyResult(false,
                    "An original repository could not be resolved. Nothing was applied. " + ex.Message, review.Repositories);
            }

            // Exclusive cross-process leases over every original repository, taken in one fixed order and held until the
            // apply, its rollback and its journal are finished. A second PaRuX process, or a recovery, fails closed.
            var leases = new List<FileStream>();
            try
            {
                foreach (string target in targets.Select(item => item.Target).OrderBy(path => path, StringComparer.Ordinal))
                {
                    try
                    {
                        leases.Add(ShadowApplyTransaction.AcquireProjectLease(target));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new MultiRepositoryApplyResult(false,
                            "Another PaRuX apply or recovery is using the original repository '" + target
                            + "'. Nothing was applied. " + ex.Message, review.Repositories);
                    }
                }
                return await ApplyUnderLeasesAsync(authorities, aggregateWorkspace, ct,
                    approvedWorkspaceFingerprint, review, targets).ConfigureAwait(false);
            }
            finally
            {
                foreach (FileStream lease in leases) lease.Dispose();
            }
        }

        private static async Task<MultiRepositoryApplyResult> ApplyUnderLeasesAsync(
            AgentRepositorySetDto authorities,
            string aggregateWorkspace,
            CancellationToken ct,
            string? approvedWorkspaceFingerprint,
            MultiRepositoryReviewResult review,
            IReadOnlyList<(string Alias, string Target)> targets)
        {
            string setKey = SetKey(targets);
            // A journal left by an interrupted apply of this exact set is settled before anything else is decided.
            string? refusal;
            try
            {
                refusal = await ResolveEarlierJournalsAsync(setKey, targets, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new MultiRepositoryApplyResult(false,
                    "An earlier multi-repository apply journal could not be read safely. Nothing was changed. " + ex.Message,
                    review.Repositories);
            }
            if (refusal != null) return new MultiRepositoryApplyResult(false, refusal, review.Repositories);

            string journalDirectory = Path.Combine(JournalRoot(), setKey + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journalDirectory);
            var prepared = new List<Prepared>();
            JournalManifest? manifest = null;
            bool keepJournal = false;
            try
            {
                foreach (AgentRepositoryAuthorityDto authority in authorities.Repositories)
                {
                    ct.ThrowIfCancellationRequested();
                    string alias = ValidateAlias(authority.Alias);
                    string target = targets.First(item => item.Alias == alias).Target;
                    string source = CanonicalDirectory(Path.Combine(aggregateWorkspace, alias), "task worktree " + alias);

                    if (!await SameRepositoryAsync(target, source, ct).ConfigureAwait(false))
                        return Fail("Repository authority mismatch for alias '" + alias + "'. Nothing was applied.");

                    GitResult targetStatus = await GitAsync(target, ct, "status", "--porcelain=v1").ConfigureAwait(false);
                    if (!targetStatus.Ok)
                        return Fail("Could not inspect original repository '" + alias + "'. Nothing was applied. " + Bound(targetStatus.Error, 500));

                    GitResult untracked = await GitAsync(source, ct, "status", "--porcelain=v1", "-z").ConfigureAwait(false);
                    var entry = new Prepared
                    {
                        Authority = authority,
                        Alias = alias,
                        Target = target,
                        Worktree = source,
                        PatchPath = Path.Combine(journalDirectory, alias + ".patch"),
                        Review = review.Repositories.First(item => string.Equals(item.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    };
                    prepared.Add(entry);

                    foreach (string relative in ParseUntracked(untracked))
                    {
                        GitResult intent = await GitAsync(source, ct, "add", "-N", "--", relative).ConfigureAwait(false);
                        if (!intent.Ok) return Fail("Could not include new file '" + relative + "' from alias '" + alias + "'. Nothing was applied.");
                        entry.IntentAdded.Add(relative);
                    }

                    GitResult diff = await GitAsync(source, ct, "diff", "--binary", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
                    if (!diff.Ok) return Fail("Could not create patch for alias '" + alias + "'. Nothing was applied.");
                    if (string.IsNullOrWhiteSpace(diff.Output)) continue;
                    try
                    {
                        await WritePatchDurablyAsync(entry.PatchPath, diff.Output, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return Fail("The task patch for alias '" + alias + "' could not be written. Nothing was applied. "
                            + ex.Message);
                    }
                    entry.HasPatch = true;
                    (PatchState state, string error) = await ClassifyAsync(target, entry.PatchPath, ct).ConfigureAwait(false);
                    if (state == PatchState.Inconclusive)
                        return Fail("Patch for alias '" + alias + "' no longer applies cleanly. Nothing was applied. " + Bound(error, 700));
                    entry.AlreadyPresent = state == PatchState.Present;
                }

                if (approvedWorkspaceFingerprint != null)
                {
                    // Intent-to-add temporarily changes the source Git view.
                    // Restore it before proving that *every* repository still
                    // matches the exact state approved in the confirmation UI.
                    foreach (Prepared entry in prepared)
                    {
                        foreach (string relative in entry.IntentAdded)
                        {
                            GitResult reset = await GitAsync(entry.Worktree, ct,
                                "reset", "--", relative).ConfigureAwait(false);
                            if (!reset.Ok)
                                return Fail("Could not restore temporary staging for alias '"
                                    + entry.Alias + "'. Nothing was applied.");
                        }
                    }

                    string? preparedState = await CodingWorkspaceVerificationFingerprint
                        .CaptureAsync(aggregateWorkspace, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(preparedState)
                        || !string.Equals(preparedState, approvedWorkspaceFingerprint,
                            StringComparison.Ordinal))
                        return Fail("The aggregate Git state changed during patch preparation. "
                            + "Nothing was applied; review every affected repository again.");
                }

                // Recheck every patch against its target before the first mutation. Unrelated user changes are
                // expected; a patch that is neither forward-applicable nor already present is a conflict.
                foreach (Prepared entry in prepared.Where(value => value.HasPatch))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(entry.PatchPath))
                        return Fail("The prepared patch for alias '" + entry.Alias
                            + "' is missing. Nothing was applied; review the repository again.");
                    (PatchState state, string error) = await ClassifyAsync(entry.Target, entry.PatchPath, ct).ConfigureAwait(false);
                    if (state != (entry.AlreadyPresent ? PatchState.Present : PatchState.Absent))
                        return Fail("Original repository '" + entry.Alias
                            + "' changed during preflight or the task patch conflicts. Nothing was applied. "
                            + Bound(error, 700));
                }

                // Only the patches this attempt will write are journaled. Recovery never reverses an alias that was
                // already present before the attempt, because this attempt did not bring that change in.
                List<Prepared> pending = prepared.Where(value => value.HasPatch && !value.AlreadyPresent).ToList();
                int presentCount = prepared.Count(value => value.AlreadyPresent);
                if (pending.Count == 0)
                {
                    // Nothing is written. A set that is already in place is a success. A review that yields no patch
                    // content is not an apply, so it is refused, and the caller does not latch the card as applied.
                    if (presentCount > 0)
                        return new MultiRepositoryApplyResult(true,
                            "The reviewed changes are already present in the original repositories. Nothing was changed.",
                            review.Repositories, AlreadyPresentCount: presentCount);
                    return Fail("The reviewed task changes contain no patch content to apply. Nothing was changed.");
                }

                try
                {
                    manifest = PublishJournal(journalDirectory, setKey, pending);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail("The apply journal could not be written, so no original repository was changed. " + ex.Message);
                }
                ct.ThrowIfCancellationRequested();

                // Once the mutation starts, the set is finished or rolled back even if the caller cancels, so the
                // cancellation token is not observed below.
                try
                {
                    foreach (Prepared entry in pending)
                    {
                        ShadowApplyFaults.Reach("multi.before-repository", entry.Alias);
                        entry.ApplyStarted = true;
                        GitResult apply = await GitAsync(entry.Target, CancellationToken.None,
                            "apply", "--binary", "--whitespace=nowarn", entry.PatchPath).ConfigureAwait(false);
                        if (!apply.Ok)
                        {
                            // Whether this patch wrote anything is decided by the reverse and forward checks in
                            // RollbackAsync, never by the exit code: git can fail after it has already written files.
                            bool attempted = prepared.Any(value => value.Applied || value.ApplyStarted);
                            (bool rollbackOk, string rollbackMessage) = await RollbackAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                            keepJournal = !rollbackOk;
                            return new MultiRepositoryApplyResult(false,
                                "Apply unexpectedly failed for alias '" + entry.Alias + "' after successful preflight. "
                                + rollbackMessage + " " + Bound(apply.Error, 700), review.Repositories,
                                RollbackAttempted: attempted, RollbackSucceeded: rollbackOk);
                        }
                        entry.Applied = true;
                        entry.ApplyStarted = false;
                        ShadowApplyFaults.Reach("multi.after-repository", entry.Alias);
                    }

                    foreach (Prepared entry in pending)
                    {
                        GitResult reverseCheck = await GitAsync(entry.Target, CancellationToken.None,
                            "apply", "--reverse", "--check", "--binary", "--whitespace=nowarn", entry.PatchPath).ConfigureAwait(false);
                        if (!reverseCheck.Ok)
                        {
                            bool attempted = prepared.Any(value => value.Applied || value.ApplyStarted);
                            (bool rollbackOk, string rollbackMessage) = await RollbackAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                            keepJournal = !rollbackOk;
                            return new MultiRepositoryApplyResult(false,
                                "PaRuX could not verify the applied patch for alias '" + entry.Alias + "'. "
                                + rollbackMessage + " " + Bound(reverseCheck.Error, 700), review.Repositories,
                                RollbackAttempted: attempted, RollbackSucceeded: rollbackOk);
                        }
                    }
                }
                catch (Exception exception)
                {
                    bool attempted = prepared.Any(value => value.Applied || value.ApplyStarted);
                    (bool rollbackOk, string rollbackMessage) = await RollbackAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                    keepJournal = !rollbackOk;
                    return new MultiRepositoryApplyResult(false,
                        "Multi-repository apply failed unexpectedly. " + rollbackMessage + " " + Bound(exception.Message, 700),
                        review.Repositories, RollbackAttempted: attempted, RollbackSucceeded: rollbackOk);
                }

                int appliedCount = prepared.Count(value => value.Applied);
                string success = "Applied reviewed changes across " + appliedCount
                    + " original repositories as uncommitted local changes"
                    + (presentCount == 0 ? string.Empty : "; " + presentCount + " repository(ies) already present")
                    + ". No commit, push, rebase, merge or stash was performed.";
                // The journal is marked complete before it is removed, so a later apply never reverses a finished set.
                if (manifest != null && !TryMarkComplete(journalDirectory, manifest))
                {
                    keepJournal = true;
                    return new MultiRepositoryApplyResult(true, success
                        + " The apply journal could not be marked complete and is retained; the next apply of this set reconciles it.",
                        review.Repositories, AppliedCount: appliedCount, AlreadyPresentCount: presentCount);
                }
                return new MultiRepositoryApplyResult(true, success, review.Repositories,
                    AppliedCount: appliedCount, AlreadyPresentCount: presentCount);
            }
            finally
            {
                foreach (Prepared entry in prepared)
                {
                    foreach (string relative in entry.IntentAdded)
                    {
                        try { _ = await GitAsync(entry.Worktree, CancellationToken.None, "reset", "--", relative).ConfigureAwait(false); }
                        catch { }
                    }
                }
                if (!keepJournal) TryDeleteDirectory(journalDirectory);
            }

            MultiRepositoryApplyResult Fail(string message) =>
                new(false, message, review.Repositories);
        }

        /// <summary>
        /// Settles the journals that an interrupted apply of this exact set left behind. Every journaled patch is
        /// classified first, and nothing is written unless all of them are decidable. Wholly present patches are then
        /// reversed, absent ones are left alone, and any repository in neither state blocks the whole recovery and keeps
        /// its journal. Returns a refusal message, or null when no unresolved journal remains.
        /// </summary>
        private static async Task<string?> ResolveEarlierJournalsAsync(string setKey,
            IReadOnlyList<(string Alias, string Target)> targets, CancellationToken ct)
        {
            foreach (string directory in Directory.EnumerateDirectories(JournalRoot(), setKey + "-*")
                .OrderBy(item => item, StringComparer.Ordinal).ToList())
            {
                if (!File.Exists(Path.Combine(directory, JournalFileName))) continue;
                JournalManifest? manifest = ReadManifest(Path.Combine(directory, JournalFileName));
                if (manifest == null || !string.Equals(manifest.SetKey, setKey, StringComparison.Ordinal)
                    || manifest.Repositories == null
                    || manifest.Repositories.Any(repository => !IsKnownRepository(repository, targets)))
                    return "An earlier multi-repository apply left a journal that cannot be verified at '" + directory
                        + "'. Nothing was changed; inspect that folder before applying again.";
                if (manifest.Completed)
                {
                    TryDeleteDirectory(directory);
                    continue;
                }

                // Phase one classifies every journaled repository and writes nothing.
                var decisions = new List<(JournalRepository Repository, string Patch, PatchState State)>();
                foreach (JournalRepository repository in manifest.Repositories)
                {
                    string patch = Path.Combine(directory, repository.PatchFile!);
                    if (!File.Exists(patch)
                        || !string.Equals(HashFile(patch), repository.PatchSha256, StringComparison.Ordinal))
                        return "The journaled patch for alias '" + repository.Alias + "' is missing or altered. "
                            + "Nothing was changed; the journal is retained at '" + directory + "'.";
                    (PatchState state, string error) = await ClassifyAsync(repository.TargetRoot!, patch, ct).ConfigureAwait(false);
                    if (state == PatchState.Inconclusive)
                        return "Repository '" + repository.Alias + "' is neither in its pre-apply state nor in the reviewed state "
                            + "after an interrupted apply. Nothing was changed; the journal is retained at '" + directory
                            + "' for manual inspection. " + Bound(error, 400);
                    decisions.Add((repository, patch, state));
                }

                // Phase two reverses the wholly present patches. A failure keeps the journal, and the next attempt
                // classifies every repository again, so the recovery converges.
                foreach ((JournalRepository repository, string patch, PatchState state) in decisions
                    .Where(item => item.State == PatchState.Present))
                {
                    GitResult reverse = await GitAsync(repository.TargetRoot!, CancellationToken.None,
                        "apply", "--reverse", "--binary", "--whitespace=nowarn", patch).ConfigureAwait(false);
                    if (!reverse.Ok)
                        return "Could not revert the interrupted apply in repository '" + repository.Alias
                            + "'. The journal is retained at '" + directory + "'. " + Bound(reverse.Error, 400);
                }
                TryDeleteDirectory(directory);
            }
            return null;
        }

        /// <summary>The first unresolved journal of this exact repository set, or null. Used by recovery and tests.</summary>
        internal static string? FindUnresolvedJournal(AgentRepositorySetDto authorities)
        {
            var targets = authorities.Repositories
                .Select(authority => (Alias: ValidateAlias(authority.Alias),
                    Target: CanonicalDirectory(authority.WorkspaceRoot, "original repository")))
                .ToList();
            string setKey = SetKey(targets);
            return Directory.EnumerateDirectories(JournalRoot(), setKey + "-*")
                .OrderBy(item => item, StringComparer.Ordinal)
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, JournalFileName)));
        }

        /// <summary>
        /// Classifies one patch against the current content of its repository. Present means the whole patch reverses
        /// cleanly, Absent means it applies forward cleanly, and anything else is Inconclusive, which is never guessed at.
        /// </summary>
        private static async Task<(PatchState State, string Error)> ClassifyAsync(string target, string patch, CancellationToken ct)
        {
            if (await CodeWorktreeApplyService.IsPatchAppliedAsync(target, patch).ConfigureAwait(false))
                return (PatchState.Present, string.Empty);
            GitResult forward = await GitAsync(target, ct, "apply", "--check", "--binary", "--whitespace=nowarn", patch).ConfigureAwait(false);
            return forward.Ok ? (PatchState.Absent, string.Empty) : (PatchState.Inconclusive, forward.Error);
        }

        private static async Task<(bool Success, string Message)> RollbackAsync(
            IReadOnlyList<Prepared> prepared, CancellationToken ct)
        {
            bool all = true;
            var failures = new List<string>();
            foreach (Prepared entry in prepared.Where(value => value.Applied || value.ApplyStarted).Reverse())
            {
                (PatchState state, string error) = await ClassifyAsync(entry.Target, entry.PatchPath, ct).ConfigureAwait(false);
                if (state == PatchState.Absent)
                {
                    // Nothing of this patch is present, so nothing it wrote remains. A partly written patch cannot pass
                    // its forward check, because each file it wrote then differs from its preimage.
                    entry.Applied = false;
                    entry.ApplyStarted = false;
                    continue;
                }
                if (state == PatchState.Inconclusive)
                {
                    // Neither the pre-apply nor the reviewed state: the repository may be partly changed and needs inspection.
                    all = false;
                    failures.Add(entry.Alias + ": neither the pre-apply nor the applied state; inspection required. "
                        + Bound(error, 350));
                    continue;
                }

                GitResult reverse = await GitAsync(entry.Target, ct,
                    "apply", "--reverse", "--binary", "--whitespace=nowarn", entry.PatchPath).ConfigureAwait(false);
                if (reverse.Ok)
                {
                    entry.Applied = false;
                    entry.ApplyStarted = false;
                }
                else
                {
                    all = false;
                    failures.Add(entry.Alias + ": " + Bound(reverse.Error, 400));
                }
            }
            return all
                ? (true, "PaRuX rolled back every repository already modified; original repositories returned to the pre-apply state.")
                : (false, "Rollback was incomplete. Manual inspection is required for: " + string.Join("; ", failures)
                    + ". The recovery journal is retained.");
        }

        /// <summary>
        /// Writes one reviewed patch and flushes it to disk. The journal records the digest of each patch, so the bytes must be
        /// durable before the manifest that names them is published. Otherwise a power loss could leave a short patch under a
        /// complete manifest, and recovery would then refuse the whole set.
        /// </summary>
        private static async Task WritePatchDurablyAsync(string path, string text, CancellationToken ct)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        private static string SetKey(IEnumerable<(string Alias, string Target)> targets) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
                targets.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => item.Alias + "\t" + item.Target.ToUpperInvariant())))))
                .ToLowerInvariant();

        private static string JournalRoot()
        {
            AppPaths.EnsureCreated();
            string root = Path.Combine(AppPaths.CodingAgentStateDir, JournalFolderName);
            Directory.CreateDirectory(root);
            return root;
        }

        private static JournalManifest PublishJournal(string directory, string setKey, IReadOnlyList<Prepared> pending)
        {
            var manifest = new JournalManifest
            {
                SetKey = setKey,
                CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Completed = false,
                Repositories = pending.Select(entry => new JournalRepository
                {
                    Alias = entry.Alias,
                    TargetRoot = entry.Target,
                    PatchFile = Path.GetFileName(entry.PatchPath),
                    PatchSha256 = HashFile(entry.PatchPath)
                }).ToArray()
            };
            WriteManifest(directory, manifest);
            return manifest;
        }

        private static bool TryMarkComplete(string directory, JournalManifest manifest)
        {
            try
            {
                manifest.Completed = true;
                WriteManifest(directory, manifest);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void WriteManifest(string directory, JournalManifest manifest)
        {
            string path = Path.Combine(directory, JournalFileName);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }

        private static JournalManifest? ReadManifest(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 2_000_000) return null;
                return JsonSerializer.Deserialize<JournalManifest>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }
        }

        private static bool IsKnownRepository(JournalRepository repository,
            IReadOnlyList<(string Alias, string Target)> targets)
        {
            if (string.IsNullOrEmpty(repository.Alias) || string.IsNullOrEmpty(repository.TargetRoot)
                || string.IsNullOrEmpty(repository.PatchFile) || string.IsNullOrEmpty(repository.PatchSha256)
                || !CodingRepositoryAliasPolicy.IsValid(repository.Alias)
                || repository.PatchFile != repository.Alias + ".patch")
                return false;
            return targets.Any(target => string.Equals(target.Alias, repository.Alias, StringComparison.Ordinal)
                && PathEquals(target.Target, repository.TargetRoot));
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                // The manifest goes first. Without PENDING.json the folder is no longer a journal, so a crash part way
                // through removing the patches cannot leave a journal whose patch is missing, which would block the set.
                string manifest = Path.Combine(directory, JournalFileName);
                if (File.Exists(manifest)) File.Delete(manifest);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static async Task<bool> SameRepositoryAsync(string target, string source, CancellationToken ct)
        {
            GitResult a = await GitAsync(target, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            GitResult b = await GitAsync(source, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            return a.Ok && b.Ok && PathEquals(a.Output.Trim(), b.Output.Trim());
        }

        private static string ValidateAlias(string? alias)
        {
            string value = (alias ?? string.Empty).Trim();
            if (!CodingRepositoryAliasPolicy.IsValid(value))
                throw new InvalidDataException("Unsafe repository alias '" + value + "'.");
            return value;
        }

        private static string CanonicalDirectory(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException(label + " path is empty.");
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(label + " no longer exists: " + full);
            return full;
        }

        private static IReadOnlyList<string> ParseUntracked(GitResult status)
        {
            if (!status.Ok || string.IsNullOrEmpty(status.Output)) return Array.Empty<string>();
            return status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(record => record.StartsWith("?? ", StringComparison.Ordinal) && record.Length > 3)
                .Select(record => record[3..])
                .ToList();
        }

        private static int CountStatusRecords(string status) =>
            status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        private sealed record GitResult(
            int ExitCode, string Output, string Error, bool TimedOut = false, bool ProcessStarted = false)
        {
            public bool Ok => ExitCode == 0;
        }

        private static async Task<GitResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] args)
        {
            // Agent worktrees are sandbox-writable: never run host Git there once its Git link was altered.
            if (!CodingAgentTools.IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
                return new GitResult(-1, string.Empty, "git refused: " + untrusted);
            var info = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(workingDirectory);
            foreach (string arg in args) info.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = info };
            bool processStarted = false;
            try
            {
                process.Start();
                processStarted = true;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Process.Dispose does not stop a child that may still be writing target files.
                    // Kill and wait before returning control to apply/rollback logic.
                    string terminationError = await StopAndWaitAsync(process).ConfigureAwait(false);
                    string stoppedOutput = await stdout.ConfigureAwait(false);
                    string stoppedError = await stderr.ConfigureAwait(false);
                    if (ct.IsCancellationRequested) throw;
                    return new GitResult(-1, stoppedOutput,
                        "Git command timed out after 45 seconds; its process exited or was terminated before rollback. "
                        + Bound(terminationError, 300) + " " + stoppedError,
                        TimedOut: true, ProcessStarted: true);
                }
                return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false),
                    ProcessStarted: true);
            }
            catch (OperationCanceledException)
            {
                if (processStarted) _ = await StopAndWaitAsync(process).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // I/O failures can occur after Git starts. Stop and await it before the caller
                // classifies the apply state or begins rollback.
                if (processStarted) _ = await StopAndWaitAsync(process).ConfigureAwait(false);
                return new GitResult(-1, string.Empty, ex.Message, ProcessStarted: processStarted);
            }
        }

        private static async Task<string> StopAndWaitAsync(Process process)
        {
            string error = string.Empty;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Exception exception) { error = exception.Message; }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        private static bool PathEquals(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string Bound(string? value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text[..max] + "\n… (truncated)";
        }
    }
}
